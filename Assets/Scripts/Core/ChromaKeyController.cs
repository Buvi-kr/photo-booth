// =============================================================================
//  ChromaKeyController.cs
//  포천아트밸리 천문과학관 무인 포토부스 — 크로마키 파이프라인 컨트롤러
//
//  역할:
//  ▪ WebCamTexture 를 RawImage 에 표시하고, ChromaKey.shader 를 적용
//  ▪ BackgroundConfig 를 받아 명세서의 4단계 파이프라인을 셰이더에 동기화
//  ▪ True Crop: Pixel → 비율 정규화, uvRect + sizeDelta 1:1 동기화
//  ▪ Transform: Zoom / MoveX / MoveY → uvRect 조절
//  ▪ Override Logic: GetEffectiveSensitivity/Smoothness/SpillRemoval 경유
// =============================================================================

using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class ChromaKeyController : MonoBehaviour
{
    // ── Inspector 연결 ─────────────────────────────────────────────────────────
    [Header("셰이더 참조 (ChromaKey.shader)")]
    public Shader chromaKeyShader;

    [Header("웹캠 설정 (Fallback용)")]
    [Tooltip("config.json 에 설정이 없을 때 사용")]
    public string preferredCameraName = "";     // 빈 문자열 = 기본 카메라
    public int    requestedWidth  = 1920;
    public int    requestedHeight = 1080;
    public int    requestedFPS    = 30;

    // ── 내부 컴포넌트 ─────────────────────────────────────────────────────────
    private RawImage      _rawImage;
    private RectTransform _rectTransform;
    private Material      _chromaMaterial;
    private WebCamTexture _webcamTexture;
    private bool          _isInitialized = false;

    // 웹캠 실제 해상도 (매 프레임 갱신 전까지 초기값)
    private int _camWidth  = 1920;
    private int _camHeight = 1080;

    // 현재 적용된 설정 (재적용 판단용)
    private BackgroundConfig _currentConfig;

    // ── 셰이더 프로퍼티 ID 캐시 (GetPropertyID 반복 호출 회피) ──────────────
    private static readonly int ID_TargetColor  = Shader.PropertyToID("_TargetColor");
    private static readonly int ID_Sensitivity  = Shader.PropertyToID("_Sensitivity");
    private static readonly int ID_Smoothness   = Shader.PropertyToID("_Smoothness");
    private static readonly int ID_SpillRemoval = Shader.PropertyToID("_SpillRemoval");
    private static readonly int ID_Brightness   = Shader.PropertyToID("_Brightness");
    private static readonly int ID_Contrast     = Shader.PropertyToID("_Contrast");
    private static readonly int ID_Saturation   = Shader.PropertyToID("_Saturation");
    private static readonly int ID_Hue          = Shader.PropertyToID("_Hue");

    // ── 라이프사이클 ──────────────────────────────────────────────────────────

    private void Awake()
    {
        EnsureInit();
    }

    private void EnsureInit()
    {
        if (_isInitialized) return;
        
        _rawImage      = GetComponent<RawImage>();
        _rectTransform = GetComponent<RectTransform>();

        InitMaterial();
        StartWebcam();

        // Config 로더가 이미 로드됐으면 즉시 적용, 아니면 이벤트 구독
        if (PhotoBoothConfigLoader.Instance != null)
        {
            PhotoBoothConfigLoader.Instance.OnConfigReloaded += OnConfigReloaded;

            if (PhotoBoothConfigLoader.Instance.IsLoaded)
                ApplyGlobalChroma(PhotoBoothConfigLoader.Instance.Config.Global);
        }

        _isInitialized = true;
    }

    private void OnDestroy()
    {
        if (PhotoBoothConfigLoader.Instance != null)
            PhotoBoothConfigLoader.Instance.OnConfigReloaded -= OnConfigReloaded;

        if (_webcamTexture != null && _webcamTexture.isPlaying)
            _webcamTexture.Stop();

        if (_chromaMaterial != null)
            Destroy(_chromaMaterial);
    }

    // ── 공개 API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 배경 설정을 적용한다.
    /// OverlayBGManager 가 SetConfig(index) 를 호출할 때 위임받는다.
    /// </summary>
    public void ApplyConfig(BackgroundConfig bgConfig)
    {
        EnsureInit();

        if (bgConfig == null)
        {
            Debug.LogWarning("[ChromaKey] ApplyConfig: bgConfig is null. 기본값 유지.");
            return;
        }

        _currentConfig = bgConfig;

        var global = PhotoBoothConfigLoader.Instance?.Config?.Global
                     ?? new GlobalChromaConfig();

        // 순서 보장: Crop → Transform → ChromaKey(Alpha) → ColorGrading(RGB)
        ApplyTrueCrop(bgConfig.Crop);
        ApplyTransform(bgConfig.Transform);
        ApplyChromaKey(bgConfig, global);
        ApplyColorGrading(bgConfig.Color);

        Debug.Log("[ChromaKey] Config 적용: \"" + bgConfig.BgName + "\"" +
                  (bgConfig.Chroma.UseLocalChroma ? " [LocalChroma]" : " [MasterChroma]"));
    }

    /// <summary>웹캠 피드를 일시 숨긴다 (스탠바이 복귀 시).</summary>
    public void Hide()
    {
        EnsureInit();
        if (_rawImage != null) _rawImage.enabled = false;
    }

    /// <summary>웹캠 피드를 다시 표시한다.</summary>
    public void Show()
    {
        EnsureInit();
        if (_rawImage != null) _rawImage.enabled = true;
    }

    // ── 내부 초기화 ───────────────────────────────────────────────────────────

    private void InitMaterial()
    {
        if (chromaKeyShader == null)
        {
            chromaKeyShader = Shader.Find("PhotoBooth/ChromaKey");
            if (chromaKeyShader == null)
            {
                Debug.LogError("[ChromaKey] 'PhotoBooth/ChromaKey' 셰이더를 찾을 수 없습니다!" +
                               " Inspector 에서 chromaKeyShader 를 직접 연결해주세요.");
                return;
            }
        }

        _chromaMaterial        = new Material(chromaKeyShader);
        _chromaMaterial.name   = "ChromaKey_Runtime";
        _rawImage.material     = _chromaMaterial;
    }

    private void StartWebcam()
    {
        WebCamDevice[] devices = WebCamTexture.devices;

        if (devices.Length == 0)
        {
            Debug.LogError("[ChromaKey] 연결된 카메라가 없습니다!");
            return;
        }

        // CameraConfig 로드 (없으면 Fallback)
        var cameraConfig = PhotoBoothConfigLoader.Instance?.Config?.Camera;
        bool useDefault = cameraConfig?.UseDefaultDevice ?? true;
        string configDeviceName = cameraConfig?.DeviceName ?? preferredCameraName;
        int reqW = cameraConfig?.RequestedWidth ?? requestedWidth;
        int reqH = cameraConfig?.RequestedHeight ?? requestedHeight;
        int reqFPS = cameraConfig?.RequestedFPS ?? requestedFPS;

        // 지정 카메라명 우선, 없으면 인덱스 0
        string targetName = devices[0].name;
        if (!useDefault && !string.IsNullOrEmpty(configDeviceName))
        {
            foreach (var d in devices)
            {
                if (d.name.Contains(configDeviceName))
                {
                    targetName = d.name;
                    break;
                }
            }
        }

        _webcamTexture = new WebCamTexture(targetName, reqW, reqH, reqFPS);
        _webcamTexture.Play();
        _rawImage.texture = _webcamTexture;

        Debug.Log("[ChromaKey] 웹캠 시작: " + targetName +
                  " (" + reqW + "x" + reqH + " @" + reqFPS + "fps)");
    }

    private void Update()
    {
        if (!Input.GetMouseButtonDown(0)) return;

        bool webcamOk = _webcamTexture != null && _webcamTexture.isPlaying;
        bool pickModeOk = AppStateManager.Instance != null && AppStateManager.Instance.isColorPickingMode;

        if (webcamOk && pickModeOk)
        {
            Debug.Log("[ChromaKey] 🖱️ 스포이드 모드 활성 상태에서 좌클릭 감지 → 색상 추출 시작");
            ExtractColorFromMouse();
        }
    }

    private void ExtractColorFromMouse()
    {
        if (_webcamTexture == null || !_webcamTexture.isPlaying) return;

        // Screen 좌표 기반 정규화 (Canvas Scaler 영향 받지 않음)
        float nx = Input.mousePosition.x / Screen.width;
        float ny = Input.mousePosition.y / Screen.height;

        if (nx >= 0f && nx <= 1f && ny >= 0f && ny <= 1f)
        {
            float trueU = nx;
            float trueV = ny;
            if (_rawImage != null)
            {
                trueU = _rawImage.uvRect.x + nx * _rawImage.uvRect.width;
                trueV = _rawImage.uvRect.y + ny * _rawImage.uvRect.height;
            }

            int px = Mathf.Clamp(Mathf.FloorToInt(trueU * _webcamTexture.width), 0, _webcamTexture.width - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt(trueV * _webcamTexture.height), 0, _webcamTexture.height - 1);

            Color pickedColor = _webcamTexture.GetPixel(px, py);
            string hexColor = "#" + ColorUtility.ToHtmlStringRGB(pickedColor);

            if (PhotoBoothConfigLoader.Instance != null && PhotoBoothConfigLoader.Instance.IsLoaded)
            {
                var globalCfg = PhotoBoothConfigLoader.Instance.Config.Global;
                globalCfg.TargetColor = hexColor;

                if (_chromaMaterial != null)
                    _chromaMaterial.SetColor(ID_TargetColor, pickedColor);

                PhotoBoothConfigLoader.Instance.SaveConfig();
                Debug.Log("[ChromaKey] 🎨 색상 추출 성공: " + hexColor);
                
                // 1회성 추출 종료 처리
                if (AppStateManager.Instance != null)
                {
                    AppStateManager.Instance.ToggleColorPickMode(); // 다시 꺼줌
                }
            }
        }
    }

    // ── 파이프라인 단계별 메서드 ──────────────────────────────────────────────

    /// <summary>
    /// [단계 0] True Crop — Pixel → 비율 정규화, uvRect + sizeDelta 1:1 동기화
    /// 명세서 3.1: 이미지 찌그러짐 차단
    /// </summary>
    private void ApplyTrueCrop(CropConfig crop)
    {
        if (_rawImage == null || _rectTransform == null) return;

        if (crop == null || crop.IsIdentity)
        {
            // 크롭 없음 — 원본 그대로
            _rawImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            return;
        }

        // 웹캠이 시작됐으면 실제 해상도, 아니면 요청 해상도 사용
        int w = (_webcamTexture != null && _webcamTexture.width  > 4) ? _webcamTexture.width  : requestedWidth;
        int h = (_webcamTexture != null && _webcamTexture.height > 4) ? _webcamTexture.height : requestedHeight;

        _camWidth  = w;
        _camHeight = h;

        // Pixel → 정규화 (0~1)
        float u0 = (float)crop.Left   / w;
        float v0 = (float)crop.Bottom / h;
        float u1 = 1f - (float)crop.Right / w;
        float v1 = 1f - (float)crop.Top   / h;

        float uvW = Mathf.Max(u1 - u0, 0.001f);
        float uvH = Mathf.Max(v1 - v0, 0.001f);

        // ─ uvRect 동기화 ─────────────────────────────────────────────────────
        _rawImage.uvRect = new Rect(u0, v0, uvW, uvH);

        // ─ sizeDelta 1:1 동기화 (찌그러짐 차단) ──────────────────────────────
        // 원래 크기에서 크롭 비율만큼 줄인다
        Vector2 originalSize = _rectTransform.sizeDelta;
        _rectTransform.sizeDelta = new Vector2(originalSize.x * uvW, originalSize.y * uvH);
    }

    /// <summary>
    /// [단계 0] Transform — Zoom / MoveX / MoveY
    /// uvRect 의 width·height·x·y 를 조절하여 확대·이동 적용
    /// </summary>
    private void ApplyTransform(TransformConfig tr)
    {
        if (_rawImage == null) return;
        if (tr == null || tr.IsIdentity) return;

        Rect uv = _rawImage.uvRect;

        // Zoom: uvRect 크기를 Zoom 으로 나누면 확대 효과
        float scaledW = uv.width  / Mathf.Max(tr.Zoom, 0.01f);
        float scaledH = uv.height / Mathf.Max(tr.Zoom, 0.01f);

        // MoveX/Y: uv 오프셋 이동 (양수 = 오른쪽/위쪽)
        float originX = uv.x + (uv.width  - scaledW) * 0.5f - tr.MoveX * scaledW;
        float originY = uv.y + (uv.height - scaledH) * 0.5f - tr.MoveY * scaledH;

        _rawImage.uvRect = new Rect(originX, originY, scaledW, scaledH);
    }

    /// <summary>
    /// [단계 1+2] Branch A + Spill — 셰이더에 크로마키 파라미터 전달
    /// Override Logic: bgConfig.GetEffective*() 를 경유하여 Global/Local 자동 선택
    /// </summary>
    private void ApplyChromaKey(BackgroundConfig bgConfig, GlobalChromaConfig global)
    {
        if (_chromaMaterial == null) return;

        _chromaMaterial.SetColor(ID_TargetColor,  global.GetTargetColor());
        _chromaMaterial.SetFloat(ID_Sensitivity,  bgConfig.GetEffectiveSensitivity(global));
        _chromaMaterial.SetFloat(ID_Smoothness,   bgConfig.GetEffectiveSmoothness(global));
        _chromaMaterial.SetFloat(ID_SpillRemoval, bgConfig.GetEffectiveSpillRemoval(global));
    }

    /// <summary>
    /// [단계 3] Branch B 색상 보정 — 셰이더에 ColorGrading 파라미터 전달
    /// Alpha Mask (분기 A) 에는 영향 없음
    /// </summary>
    private void ApplyColorGrading(ColorGradingConfig colorCfg)
    {
        if (_chromaMaterial == null || colorCfg == null) return;

        _chromaMaterial.SetFloat(ID_Brightness, colorCfg.Brightness);
        _chromaMaterial.SetFloat(ID_Contrast,   colorCfg.Contrast);
        _chromaMaterial.SetFloat(ID_Saturation, colorCfg.Saturation);
        _chromaMaterial.SetFloat(ID_Hue,        colorCfg.Hue);
    }

    /// <summary>
    /// Global 마스터 크로마 설정만 적용 (배경 미선택 상태의 기본값).
    /// </summary>
    private void ApplyGlobalChroma(GlobalChromaConfig global)
    {
        if (_chromaMaterial == null || global == null) return;

        _chromaMaterial.SetColor(ID_TargetColor,  global.GetTargetColor());
        _chromaMaterial.SetFloat(ID_Sensitivity,  global.MasterSensitivity);
        _chromaMaterial.SetFloat(ID_Smoothness,   global.MasterSmoothness);
        _chromaMaterial.SetFloat(ID_SpillRemoval, global.MasterSpillRemoval);
        _chromaMaterial.SetFloat(ID_Brightness,   0f);
        _chromaMaterial.SetFloat(ID_Contrast,     1f);
        _chromaMaterial.SetFloat(ID_Saturation,   1f);
        _chromaMaterial.SetFloat(ID_Hue,          0f);
    }

    // ── 이벤트 핸들러 ─────────────────────────────────────────────────────────

    private void OnConfigReloaded(PhotoBoothConfig config)
    {
        // 핫리로드 시: 현재 선택된 배경이 있으면 재적용
        if (_currentConfig != null && config != null)
        {
            var updated = config.FindByName(_currentConfig.BgName);
            if (updated != null)
            {
                Debug.Log("[ChromaKey] 핫리로드 재적용: \"" + updated.BgName + "\"");
                ApplyConfig(updated);
            }
        }
        else
        {
            // 배경 미선택 상태면 Global 마스터값만 갱신
            ApplyGlobalChroma(config?.Global ?? new GlobalChromaConfig());
        }
    }
}
