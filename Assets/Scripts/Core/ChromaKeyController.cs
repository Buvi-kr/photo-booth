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
    private RectMask2D    _rectMask2D;
    private RectTransform _parentRect;
    private Material      _chromaMaterial;
    private WebCamTexture _webcamTexture;
    private bool          _isInitialized = false;

    // 웹캠 실제 해상도 (매 프레임 갱신 전까지 초기값)
    private int _camWidth  = 1920;
    private int _camHeight = 1080;

    // 현재 적용된 설정 (재적용 판단용)
    private BackgroundConfig _currentConfig;
    // 마지막으로 적용된 크롭 설정 (축소 시 배경 부활 방지용)
    private CropConfig _lastCrop = new CropConfig();

    /// <summary>현재 실행 중인 WebCamTexture (고품질 캡처용)</summary>
    public WebCamTexture WebcamTexture => _webcamTexture;
    /// <summary>현재 적용된 ChromaKey 머티리얼 (고품질 캡처용)</summary>
    public Material ChromaMaterial => _chromaMaterial;

    // ── 셰이더 프로퍼티 ID 캐시 (GetPropertyID 반복 호출 회피) ──────────────
    private static readonly int ID_TargetColor  = Shader.PropertyToID("_TargetColor");
    private static readonly int ID_Sensitivity  = Shader.PropertyToID("_Sensitivity");
    private static readonly int ID_Smoothness   = Shader.PropertyToID("_Smoothness");
    private static readonly int ID_SpillRemoval = Shader.PropertyToID("_SpillRemoval");
    private static readonly int ID_LumaWeight   = Shader.PropertyToID("_LumaWeight");
    private static readonly int ID_EdgeChoke    = Shader.PropertyToID("_EdgeChoke");
    private static readonly int ID_PreBlur      = Shader.PropertyToID("_PreBlur");
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
        _rectMask2D    = GetComponentInParent<RectMask2D>();
        if (_rectMask2D != null) _parentRect = _rectMask2D.GetComponent<RectTransform>();

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
        ApplyTrueCrop(global.MasterCrop);
        ApplyTransform(bgConfig.Transform);
        ApplyChromaKey(global);
        ApplyColorGrading(bgConfig.Color);

        Debug.Log("[ChromaKey] Config 적용: \"" + bgConfig.BgName + "\" [MasterChroma & MasterCrop]");
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
        bool pickModeOk = AppStateManager.Instance != null && AppStateManager.Instance.isColorPickingMode;

        if (pickModeOk)
        {
            UpdateMagnifier();

            if (Input.GetMouseButtonDown(0) && _webcamTexture != null && _webcamTexture.isPlaying)
            {
                Debug.Log("[ChromaKey] 🖱️ 스포이드 모드 활성 상태에서 좌클릭 감지 → 색상 추출 시작");
                ExtractColorFromMouse();
            }
        }
        else if (AppStateManager.Instance != null && AppStateManager.Instance.magnifierPanel != null && AppStateManager.Instance.magnifierPanel.activeSelf)
        {
            AppStateManager.Instance.magnifierPanel.SetActive(false);
        }
    }

    private void UpdateMagnifier()
    {
        var appState = AppStateManager.Instance;
        if (appState.magnifierPanel == null || appState.magnifierRawImage == null) return;

        if (!appState.magnifierPanel.activeSelf)
            appState.magnifierPanel.SetActive(true);

        Vector2 mousePos = Input.mousePosition;
        var magRect = appState.magnifierPanel.GetComponent<RectTransform>();
        magRect.position = mousePos + new Vector2(100, 100);

        if (_rawImage != null && _rawImage.texture != null)
        {
            appState.magnifierRawImage.texture = _rawImage.texture;
            
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_rectTransform, mousePos, null, out Vector2 localPos);
            Rect rect = _rectTransform.rect;
            
            float u = (localPos.x - rect.x) / rect.width;
            float v = (localPos.y - rect.y) / rect.height;

            float zoomSize = 0.1f; // 10배율
            appState.magnifierRawImage.uvRect = new Rect(u - zoomSize / 2, v - zoomSize / 2, zoomSize, zoomSize);
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
                var appState = AppStateManager.Instance;
                var config = PhotoBoothConfigLoader.Instance.Config;
                bool isGlobal = appState == null || appState.adminStep == AdminStep.GlobalChroma;

                if (isGlobal)
                {
                    config.Global.TargetColor = hexColor;
                }
                else
                {
                    var bg = config.Backgrounds[appState.adminBgIndex];
                    bg.Chroma.LocalTargetColor = hexColor;
                    bg.Chroma.UseLocalChroma = true;
                }

                if (_chromaMaterial != null)
                    _chromaMaterial.SetColor(ID_TargetColor, pickedColor);

                PhotoBoothConfigLoader.Instance.SaveConfig();
                Debug.Log("[ChromaKey] 🎨 색상 추출 성공: " + hexColor);
                
                // 1회성 추출 종료 처리 및 UI 갱신
                if (appState != null)
                {
                    appState.ToggleColorPickMode(); // 다시 꺼줌
                    appState.RefreshAdminUI();      // 슬라이더 갱신
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
        if (_rawImage == null || _rectTransform == null || _rectMask2D == null || _parentRect == null) return;

        // 크롭 설정 저장 (Transform 적용 후 재보정에 사용)
        _lastCrop = crop;

        // [Fixed Logic] RectMask2D.padding 을 사용하여 실제 '패딩' 처럼 작동하게 합니다.
        _parentRect.offsetMin = Vector2.zero;
        _parentRect.offsetMax = Vector2.zero;
        
        _rectMask2D.padding = new Vector4(crop.Left, crop.Bottom, crop.Right, crop.Top);

        // 페이딩 (부드러운 테두리) 적용
        _rectMask2D.softness = new Vector2Int(crop.FadeX, crop.FadeY);

        // uvRect는 기본값(0,0,1,1)으로 초기화 (Transform 단계에서 다시 계산)
        _rawImage.uvRect = new Rect(0, 0, 1, 1);
    }

    /// <summary>
    /// [단계 0] Transform — Zoom / MoveX / MoveY / Rotation
    /// uvRect 의 width·height·x·y 를 조절하여 확대·이동 적용. RectTransform 의 Z축 조절하여 회전 적용.
    /// </summary>
    private void ApplyTransform(TransformConfig tr)
    {
        if (_rawImage == null || _rectTransform == null) return;
        
        // [New Logic] 물리적인 이동 및 확대 적용 (자연스럽게 마스크에 의해 잘림)
        // Zoom: localScale 로 확대 (100% 기준) - 이전 설정값 호환 처리
        float zoomVal = tr.Zoom <= 5.0f ? tr.Zoom * 100f : tr.Zoom;
        float s = Mathf.Max(zoomVal / 100f, 0.01f);
        _rectTransform.localScale = new Vector3(s, s, 1f);

        // MoveX/Y: -100 ~ 100 범위를 -1000px ~ 1000px 로 환산 (10 곱하기) - 이전 설정값 호환
        float moveX = (Mathf.Abs(tr.MoveX) <= 1.0f && tr.MoveX != 0f) ? tr.MoveX * 100f : tr.MoveX;
        float moveY = (Mathf.Abs(tr.MoveY) <= 1.0f && tr.MoveY != 0f) ? tr.MoveY * 100f : tr.MoveY;
        _rectTransform.anchoredPosition = new Vector2(moveX * 10f, moveY * 10f);

        // Rotation: 기존방식 유지 (-180 ~ 180도)
        _rectTransform.localEulerAngles = new Vector3(0f, 0f, tr.Rotation);
        
        // uvRect 는 건드리지 않음 (0,0,1,1) -> 바둑판 현상 방지
        _rawImage.uvRect = new Rect(0, 0, 1, 1);

        // ── 크롭 재보정: 축소 시 배경 부활 방지 ────────────────────────────────
        // RectMask2D.padding은 컨테이너 좌표계 기준이므로, localScale 이 1 미만이면
        // 이미지가 줄어들어 잘렸던 영역 바깥이 드러난다.
        // Scale < 1인 경우, padding을 scale로 나눠 상대적 마스크 범위를 유지한다.
        if (_rectMask2D != null && _lastCrop != null && s > 0f)
        {
            float inv = 1f / s; // scale이 작을수록 padding을 넓혀서 바깥쪽 노출 차단
            float cropScale = Mathf.Max(inv, 1f); // 100% 이상 줌일 때는 보정 불필요
            _rectMask2D.padding = new Vector4(
                _lastCrop.Left   * cropScale,
                _lastCrop.Bottom * cropScale,
                _lastCrop.Right  * cropScale,
                _lastCrop.Top    * cropScale
            );
        }
    }

    /// <summary>
    /// [단계 1+2] Branch A + Spill — 셰이더에 크로마키 파라미터 전달
    /// Override Logic: 항상 Global Master 값을 사용한다.
    /// </summary>
    private void ApplyChromaKey(GlobalChromaConfig global)
    {
        if (_chromaMaterial == null) return;

        _chromaMaterial.SetColor(ID_TargetColor,  global.GetTargetColor());
        _chromaMaterial.SetFloat(ID_Sensitivity,  global.MasterSensitivity / 100f);
        _chromaMaterial.SetFloat(ID_Smoothness,   global.MasterSmoothness / 100f);
        _chromaMaterial.SetFloat(ID_SpillRemoval, global.MasterSpillRemoval / 100f);
        _chromaMaterial.SetFloat(ID_LumaWeight,   global.MasterLumaWeight / 100f);
        _chromaMaterial.SetFloat(ID_EdgeChoke,    global.MasterEdgeChoke / 100f);
        _chromaMaterial.SetFloat(ID_PreBlur,      global.MasterPreBlur / 100f * 5.0f); // 0~100 -> 0~5.0px
    }

    /// <summary>
    /// [단계 3] Branch B 색상 보정 — 셰이더에 ColorGrading 파라미터 전달
    /// Alpha Mask (분기 A) 에는 영향 없음
    /// </summary>
    private void ApplyColorGrading(ColorGradingConfig colorCfg)
    {
        if (_chromaMaterial == null || colorCfg == null) return;

        // 이전 설정값 호환 처리 (0~2 범위였던 것을 0~200 으로 변환)
        float c = colorCfg.Contrast <= 2.0f && colorCfg.Contrast > 0f ? colorCfg.Contrast * 100f : colorCfg.Contrast;
        float sat = colorCfg.Saturation <= 2.0f && colorCfg.Saturation > 0f ? colorCfg.Saturation * 100f : colorCfg.Saturation;
        float bright = Mathf.Abs(colorCfg.Brightness) <= 1.0f && colorCfg.Brightness != 0f ? colorCfg.Brightness * 100f : colorCfg.Brightness;

        _chromaMaterial.SetFloat(ID_Brightness, bright / 100f);
        _chromaMaterial.SetFloat(ID_Contrast,   c / 100f);
        _chromaMaterial.SetFloat(ID_Saturation, sat / 100f);
        _chromaMaterial.SetFloat(ID_Hue,        colorCfg.Hue);
    }

    /// <summary>
    /// Global 마스터 크로마 설정만 적용 (배경 미선택 상태의 기본값).
    /// </summary>
    private void ApplyGlobalChroma(GlobalChromaConfig global)
    {
        if (_chromaMaterial == null || global == null) return;

        _chromaMaterial.SetColor(ID_TargetColor,  global.GetTargetColor());
        _chromaMaterial.SetFloat(ID_Sensitivity,  global.MasterSensitivity / 100f);
        _chromaMaterial.SetFloat(ID_Smoothness,   global.MasterSmoothness / 100f);
        _chromaMaterial.SetFloat(ID_SpillRemoval, global.MasterSpillRemoval / 100f);
        _chromaMaterial.SetFloat(ID_LumaWeight,   global.MasterLumaWeight / 100f);
        _chromaMaterial.SetFloat(ID_EdgeChoke,    global.MasterEdgeChoke / 100f);
        _chromaMaterial.SetFloat(ID_PreBlur,      global.MasterPreBlur / 100f * 5.0f);
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
