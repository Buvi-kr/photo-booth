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
    private Mask          _mask;
    private Image         _maskImage;
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
    // 크롭으로 인한 마스크 오프셋 — ApplyTransform이 이걸 더해서 최종 위치 계산
    private Vector2 _cropMaskOffset = Vector2.zero;
    // EnsureInit 시점의 mask 컨테이너 원본 크기 (Canvas units) — 크롭/사이즈 계산의 base
    private Vector2 _originalMaskSize = new Vector2(1920f, 1080f);
    // 디자이너가 캔버스에 잡아둔 원본 크기 (orientation 변경 후에도 불변, 복원용)
    private Vector2 _designMaskSize   = new Vector2(1920f, 1080f);
    private bool _isPortraitMode = false;

    /// <summary>현재 실행 중인 WebCamTexture (고품질 캡처용)</summary>
    public WebCamTexture WebcamTexture => _webcamTexture;
    /// <summary>현재 적용된 ChromaKey 머티리얼 (고품질 캡처용)</summary>
    public Material ChromaMaterial => _chromaMaterial;
    /// <summary>WebCam RawImage의 RectTransform (캡처 UV 변환용)</summary>
    public RectTransform WebcamRectTransform => _rectTransform;
    /// <summary>Crop Padding (저장용 흉내)</summary>
    public Vector4 CropPadding => _lastCrop != null 
        ? new Vector4(_lastCrop.Left, _lastCrop.Bottom, _lastCrop.Right, _lastCrop.Top) 
        : Vector4.zero;
    /// <summary>Crop Fade (저장용 흉내)</summary>
    public Vector2 CropFade => _lastCrop != null
        ? new Vector2(_lastCrop.FadeX, _lastCrop.FadeY)
        : Vector2.zero;

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
        _mask          = GetComponentInParent<Mask>();
        if (_mask != null)
        {
            _parentRect = _mask.GetComponent<RectTransform>();
            _maskImage  = _mask.GetComponent<Image>();
        }

        // ★ 앵커 변경 전에 실제 화면상 크기를 먼저 캡처
        //   (stretch 앵커였다가 center 앵커로 바뀌면서 sizeDelta=0이 0×0이 되는 문제 방지)
        if (_parentRect != null)
        {
            Canvas.ForceUpdateCanvases();
            Vector2 measured = _parentRect.rect.size;
            if (measured.x > 0f && measured.y > 0f)
            {
                _originalMaskSize = measured;
                _designMaskSize   = measured; // orientation 복원용 원본
            }
        }

        // 부모(Mask 컨테이너) 앵커/피벗 중앙 고정 + 캡처한 크기로 sizeDelta 세팅
        if (_parentRect != null && _mask != null)
        {
            _parentRect.anchorMin = _parentRect.anchorMax = new Vector2(0.5f, 0.5f);
            _parentRect.pivot = new Vector2(0.5f, 0.5f);
            _parentRect.sizeDelta = _originalMaskSize; // ← center 앵커에서 sizeDelta = 실제 픽셀

            // Mask 흰 이미지가 크로마키 투명 영역을 덮어 BG 합성을 망치는 문제 방지
            _mask.showMaskGraphic = false;
            if (_maskImage != null) _maskImage.raycastTarget = false;
        }

        // ★ 자식(WebCamDisplay) 앵커/피벗 중앙 고정 + sizeDelta = 부모와 동일.
        //   기존 stretch 앵커(0,0)~(1,1) + sizeDelta=(1920,1080) 조합은
        //   "부모 대비 +1920×+1080 패딩"으로 해석되어 이미지가 폭발적으로 확대됐음.
        if (_rectTransform != null)
        {
            _rectTransform.anchorMin = _rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _rectTransform.pivot     = new Vector2(0.5f, 0.5f);
            _rectTransform.sizeDelta = _originalMaskSize;
            _rectTransform.anchoredPosition = Vector2.zero;
            _rectTransform.localScale = Vector3.one;
            _rectTransform.localEulerAngles = Vector3.zero;
        }

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

        if (_webcamTexture != null)
        {
            if (_webcamTexture.isPlaying) _webcamTexture.Stop();
            Destroy(_webcamTexture); // [Kiosk Fix] 하드웨어 점유 강제 해제
            _webcamTexture = null;
        }

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
        // FilterMode를 Bilinear로 명시 (기본값도 Bilinear이나 방어적으로 설정)
        // 2x SSAA 업샘플 시 tex2D 샘플이 Bilinear로 동작하도록 보장
        _webcamTexture.filterMode = FilterMode.Bilinear;
        _webcamTexture.Play();
        _rawImage.texture = _webcamTexture;

        Debug.Log($"[ChromaKey] 웹캠 시작 요청: {targetName} ({reqW}x{reqH} @{reqFPS}fps)");

        // Play() 직후에는 실제 해상도가 확정되지 않음 → 1프레임 후 실제값 확인
        StartCoroutine(LogActualWebcamResolution());
    }

    /// <summary>
    /// Play() 직후 드라이버가 실제로 열어준 해상도를 로그에 기록.
    /// USB 대역폭 부족 등으로 요청 해상도가 무시된 경우를 감지하기 위함.
    /// </summary>
    private System.Collections.IEnumerator LogActualWebcamResolution()
    {
        // 최대 2초까지 대기하며 웹캠이 실제 해상도를 반환할 때까지 기다림
        float timeout = 2f;
        while (_webcamTexture != null && (_webcamTexture.width <= 16 || _webcamTexture.height <= 16))
        {
            timeout -= Time.deltaTime;
            if (timeout <= 0f) break;
            yield return null;
        }

        if (_webcamTexture == null) yield break;

        int actualW = _webcamTexture.width;
        int actualH = _webcamTexture.height;

        if (actualW <= 16 || actualH <= 16)
        {
            Debug.LogWarning($"[ChromaKey] ⚠️ 웹캠 실제 해상도 미확인 (아직 초기화 중?): {actualW}x{actualH}");
        }
        else
        {
            Debug.Log($"[ChromaKey] ✅ 웹캠 실제 해상도 확인: {actualW}x{actualH}" +
                      $" (요청: {_webcamTexture.requestedWidth}x{_webcamTexture.requestedHeight})");

            if (actualW < _webcamTexture.requestedWidth || actualH < _webcamTexture.requestedHeight)
            {
                Debug.LogWarning($"[ChromaKey] ⚠️ 드라이버가 해상도를 낮췄습니다! " +
                                 $"요청 {_webcamTexture.requestedWidth}x{_webcamTexture.requestedHeight} → " +
                                 $"실제 {actualW}x{actualH}. USB 대역폭 또는 카메라 지원 해상도를 확인하세요.");
            }

            // ★ Orientation 자동 감지 + 마스크 사이즈 갱신
            ApplyOrientation(actualW, actualH);
        }
    }

    /// <summary>
    /// 카메라 비율 (또는 config 강제 설정)에 맞춰 mask 컨테이너 크기를 조정한다.
    /// 디자인 원본(_designMaskSize)에서 계산하므로 멱등 — 여러 번 호출해도 안전.
    /// </summary>
    private void ApplyOrientation(int camW, int camH)
    {
        // config override 확인
        var camCfg = PhotoBoothConfigLoader.Instance?.Config?.Camera;
        string mode = (camCfg?.OrientationOverride ?? "auto").ToLowerInvariant();

        bool portrait;
        switch (mode)
        {
            case "landscape": portrait = false; break;
            case "portrait":  portrait = true;  break;
            default: // "auto"
                portrait = camW < camH;
                break;
        }

        _isPortraitMode = portrait;

        // 새 mask 크기 계산
        Vector2 newSize;
        if (portrait)
        {
            // 디자인 높이 유지, 가로폭만 카메라 비율로 축소
            // (ex. 디자인 1920×1080, 카메라 1080×1920 → 1080 * (1080/1920) = 607.5 → 607.5×1080)
            float camAspect = (camH > 0) ? (float)camW / camH : (9f / 16f);
            newSize = new Vector2(_designMaskSize.y * camAspect, _designMaskSize.y);
        }
        else
        {
            // 가로 모드 → 디자인 원본 그대로
            newSize = _designMaskSize;
        }

        // 변경 없으면 스킵 (불필요한 재적용 방지)
        if (Mathf.Approximately(newSize.x, _originalMaskSize.x) &&
            Mathf.Approximately(newSize.y, _originalMaskSize.y))
        {
            Debug.Log($"[ChromaKey] Orientation 변경 없음: {(portrait ? "Portrait" : "Landscape")} {newSize}");
            return;
        }

        _originalMaskSize = newSize;

        // mask + child sizeDelta 갱신, 가운데 정렬 유지
        if (_parentRect != null)
        {
            _parentRect.sizeDelta = newSize;
            _parentRect.anchoredPosition = Vector2.zero;
        }
        if (_rectTransform != null)
        {
            _rectTransform.sizeDelta = newSize;
            _rectTransform.anchoredPosition = Vector2.zero;
        }

        Debug.Log($"[ChromaKey] 🔄 Orientation 적용: {(portrait ? "Portrait" : "Landscape")} " +
                  $"camera={camW}x{camH}, maskSize={newSize.x:F0}x{newSize.y:F0}");

        // 현재 적용된 background config가 있으면 새 base로 다시 적용 (Crop 재계산)
        if (_currentConfig != null) ApplyConfig(_currentConfig);
        else ApplyTrueCrop(_lastCrop ?? new CropConfig()); // 미선택 상태면 최소한 crop만 재적용
    }

    private bool _wasPickMode = false;
    private float _pickModeEnterCooldown = 0f;
    private Canvas _cachedCanvas;

    /// <summary>RectTransformUtility 호출용 카메라. Overlay 캔버스면 null, 그 외엔 worldCamera.</summary>
    private Camera GetUICamera()
    {
        if (_cachedCanvas == null && _rectTransform != null)
            _cachedCanvas = _rectTransform.GetComponentInParent<Canvas>();
        if (_cachedCanvas == null) return null;
        return _cachedCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _cachedCanvas.worldCamera;
    }

    private void Update()
    {
        bool pickModeOk = AppStateManager.Instance != null && AppStateManager.Instance.isColorPickingMode;

        // 픽 모드 진입 직후 0.2초 클릭 무시 — 스포이드 버튼 클릭의 잔여 입력으로 즉시 추출/취소되는 것 방지
        if (pickModeOk && !_wasPickMode) _pickModeEnterCooldown = 0.2f;
        if (_pickModeEnterCooldown > 0f) _pickModeEnterCooldown -= Time.deltaTime;
        _wasPickMode = pickModeOk;

        if (pickModeOk)
        {
            UpdateMagnifier();

            if (_pickModeEnterCooldown <= 0f &&
                Input.GetMouseButtonDown(0) &&
                _webcamTexture != null && _webcamTexture.isPlaying)
            {
                Debug.Log("[ChromaKey] 🖱️ 스포이드 좌클릭 감지 → 색상 추출 시작");
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
            
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_rectTransform, mousePos, GetUICamera(), out Vector2 localPos);
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
        if (_rawImage == null || _rectTransform == null) return;

        // RawImage 로컬 좌표 변환 — Canvas 모드별 카메라 처리 (Overlay=null, Camera/World=worldCamera)
        Camera uiCam = GetUICamera();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _rectTransform, Input.mousePosition, uiCam, out Vector2 localPos);
        Rect rect = _rectTransform.rect;
        float trueU = (localPos.x - rect.x) / rect.width;
        float trueV = (localPos.y - rect.y) / rect.height;

        Debug.Log($"[ChromaKey] click screen={Input.mousePosition} → local={localPos} | rect=({rect.x:F1},{rect.y:F1},{rect.width:F1},{rect.height:F1}) | uv=({trueU:F3},{trueV:F3}) | canvas={(_cachedCanvas?.renderMode.ToString() ?? "null")}");

        // 웹캠 영역 밖 클릭 처리 — 약간의 마진(1%)을 허용 (가장자리 클릭 누락 방지),
        // 그 이상 벗어났으면 사용자 취소 의도로 보고 모드 종료
        const float margin = 0.02f;
        if (trueU < -margin || trueU > 1f + margin || trueV < -margin || trueV > 1f + margin)
        {
            Debug.Log("[ChromaKey] 클릭 위치가 웹캠 영역 밖 — 픽 모드 취소");
            AppStateManager.Instance?.ExitColorPickMode();
            return;
        }
        // 마진 안쪽이면 [0,1]로 클램프해서 진행
        trueU = Mathf.Clamp01(trueU);
        trueV = Mathf.Clamp01(trueV);

        int px = Mathf.Clamp(Mathf.FloorToInt(trueU * _webcamTexture.width),  0, _webcamTexture.width  - 1);
        int py = Mathf.Clamp(Mathf.FloorToInt(trueV * _webcamTexture.height), 0, _webcamTexture.height - 1);

        Color pickedColor = _webcamTexture.GetPixel(px, py);
        string hexColor = "#" + ColorUtility.ToHtmlStringRGB(pickedColor);

        if (PhotoBoothConfigLoader.Instance != null && PhotoBoothConfigLoader.Instance.IsLoaded)
        {
            var appState = AppStateManager.Instance;
            var config = PhotoBoothConfigLoader.Instance.Config;

            // ApplyChromaKey 는 항상 Global.TargetColor 만 사용 → 페이지 무관하게 Global 업데이트
            // (Local 페이지에서 LocalTargetColor 에 저장하던 옛 코드는 화면에 반영 안 되는 버그였음)
            config.Global.TargetColor = hexColor;

            // ★ 머티리얼 양쪽 모두 적용 — Mask가 머티리얼 클론해서 쓰기 때문에
            //   _chromaMaterial 만 SetColor 하면 화면에 반영 안 됨
            SetMatColor(ID_TargetColor, pickedColor);

            PhotoBoothConfigLoader.Instance.SaveConfig();
            Debug.Log("[ChromaKey] 🎨 색상 추출 성공: " + hexColor + $" (px={px}, py={py})");

            // 1회성: 추출 즉시 모드 종료
            if (appState != null)
            {
                appState.ExitColorPickMode();
                appState.RefreshAdminUI();
            }
        }
    }

    // ── 머티리얼 헬퍼: Mask 가 머티리얼을 클론해서 stencil 적용한 후 그걸 렌더링 함.
    //   원본 _chromaMaterial 에만 SetFloat 하면 클론에는 반영이 안 되어
    //   슬라이더/스포이드 변화가 화면에 안 보이는 버그가 발생.
    //   따라서 원본과 _rawImage.materialForRendering(클론) 양쪽 모두에 적용한다.
    private void SetMatFloat(int id, float value)
    {
        if (_chromaMaterial != null) _chromaMaterial.SetFloat(id, value);
        if (_rawImage != null)
        {
            var renderMat = _rawImage.materialForRendering;
            if (renderMat != null && renderMat != _chromaMaterial)
                renderMat.SetFloat(id, value);
        }
    }

    private void SetMatColor(int id, Color value)
    {
        if (_chromaMaterial != null) _chromaMaterial.SetColor(id, value);
        if (_rawImage != null)
        {
            var renderMat = _rawImage.materialForRendering;
            if (renderMat != null && renderMat != _chromaMaterial)
                renderMat.SetColor(id, value);
        }
    }

    // ── 파이프라인 단계별 메서드 ──────────────────────────────────────────────

    /// <summary>
    /// [단계 0] True Crop — Pixel → 비율 정규화, uvRect + sizeDelta 1:1 동기화
    /// 명세서 3.1: 이미지 찌그러짐 차단
    /// </summary>
    private void ApplyTrueCrop(CropConfig crop)
    {
        if (_rawImage == null || _rectTransform == null || _parentRect == null) return;

        _lastCrop = crop;

        // 디자인 베이스: EnsureInit 시점에 저장된 마스크 컨테이너 원본 크기 (Canvas units).
        // 이렇게 해야 mask와 child가 같은 단위/크기로 작동 → 이미지 늘어남 현상 차단.
        float baseW = _originalMaskSize.x > 0f ? _originalMaskSize.x : 1920f;
        float baseH = _originalMaskSize.y > 0f ? _originalMaskSize.y : 1080f;

        // 부모(Mask) 크기 조절 + 음수 클램프
        float croppedW = Mathf.Max(0f, baseW - crop.Left   - crop.Right);
        float croppedH = Mathf.Max(0f, baseH - crop.Bottom - crop.Top);
        _parentRect.sizeDelta = new Vector2(croppedW, croppedH);

        // ★ 핵심 수정: 마스크가 중앙에서 균일 축소되면 양쪽 가장자리가 같이 줄어들어
        //   "이미지가 위로 말려 올라가는" 효과가 발생. 잘리지 않은 가장자리를 고정시키려면
        //   마스크 자체를 cropped 쪽으로 이동시켜야 한다.
        //   예: Top=100 → 마스크 중심이 -50 만큼 내려가서 윗변만 100px 내려옴.
        _cropMaskOffset = new Vector2(
            (crop.Left   - crop.Right) / 2f,
            (crop.Bottom - crop.Top  ) / 2f);
        // ApplyTransform 이 move offset 과 합성하지만, 단독 호출되는 경우도 대비.
        _parentRect.anchoredPosition = _cropMaskOffset;

        // 자식(RawImage)은 마스크 이동의 반대 방향으로 오프셋 → 이미지 자체는 제자리에 머묾
        _rectTransform.sizeDelta = new Vector2(baseW, baseH);
        _rectTransform.anchoredPosition = new Vector2(
            (crop.Right - crop.Left) / 2f,
            (crop.Top   - crop.Bottom) / 2f);

        _rawImage.uvRect = new Rect(0, 0, 1, 1);
    }

    /// <summary>
    /// [단계 0] Transform — Zoom / MoveX / MoveY / Rotation
    /// uvRect 의 width·height·x·y 를 조절하여 확대·이동 적용. RectTransform 의 Z축 조절하여 회전 적용.
    /// </summary>
    private void ApplyTransform(TransformConfig tr)
    {
        if (_rawImage == null || _rectTransform == null || _parentRect == null) return;
        
        // [New Logic] 모든 변환(확대/이동/회전)을 부모(Mask)에게 적용
        // 이렇게 하면 마스크와 인물이 한 몸처럼 움직여서 테두리 부활 현상이 생기지 않음
        
        // Zoom: 부모의 localScale 로 확대 (100% 기준)
        float zoomVal = tr.Zoom <= 5.0f ? tr.Zoom * 100f : tr.Zoom;
        float s = Mathf.Max(zoomVal / 100f, 0.01f);
        _parentRect.localScale = new Vector3(s, s, 1f);

        // MoveX/Y: 부모의 anchoredPosition 을 조절 — crop 오프셋과 합성
        // (crop 단독 적용 시엔 ApplyTrueCrop 에서 _cropMaskOffset 만 세팅하고,
        //  여기서 move 가 있으면 crop + move 로 덮어씀)
        float moveX = (Mathf.Abs(tr.MoveX) <= 1.0f && tr.MoveX != 0f) ? tr.MoveX * 100f : tr.MoveX;
        float moveY = (Mathf.Abs(tr.MoveY) <= 1.0f && tr.MoveY != 0f) ? tr.MoveY * 100f : tr.MoveY;
        _parentRect.anchoredPosition = _cropMaskOffset + new Vector2(moveX * 10f, moveY * 10f);

        // Rotation: 부모 자체를 회전시킴 (마스크도 함께 회전)
        _parentRect.localEulerAngles = new Vector3(0f, 0f, tr.Rotation);
        
        // 자식(웹캠 이미지)은 부모에 대해 상대적으로 고정 (ApplyTrueCrop 에서 잡은 위치 유지)
        _rectTransform.localScale = Vector3.one;
        _rectTransform.localEulerAngles = Vector3.zero;

        // uvRect 는 건드리지 않음
        _rawImage.uvRect = new Rect(0, 0, 1, 1);
    }

    /// <summary>
    /// [단계 1+2] Branch A + Spill — 셰이더에 크로마키 파라미터 전달
    /// Override Logic: 항상 Global Master 값을 사용한다.
    /// </summary>
    private void ApplyChromaKey(GlobalChromaConfig global)
    {
        if (_chromaMaterial == null) return;

        SetMatColor(ID_TargetColor,  global.GetTargetColor());
        SetMatFloat(ID_Sensitivity,  global.MasterSensitivity / 100f);
        SetMatFloat(ID_Smoothness,   global.MasterSmoothness / 100f);
        SetMatFloat(ID_SpillRemoval, global.MasterSpillRemoval / 100f);
        SetMatFloat(ID_LumaWeight,   global.MasterLumaWeight / 100f);
        SetMatFloat(ID_EdgeChoke,    global.MasterEdgeChoke / 100f);
        SetMatFloat(ID_PreBlur,      global.MasterPreBlur / 100f * 5.0f); // 0~100 -> 0~5.0px
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

        SetMatFloat(ID_Brightness, bright / 100f);
        SetMatFloat(ID_Contrast,   c / 100f);
        SetMatFloat(ID_Saturation, sat / 100f);
        SetMatFloat(ID_Hue,        colorCfg.Hue);
    }

    /// <summary>
    /// Global 마스터 크로마 설정만 적용 (배경 미선택 상태의 기본값).
    /// </summary>
    private void ApplyGlobalChroma(GlobalChromaConfig global)
    {
        if (_chromaMaterial == null || global == null) return;

        SetMatColor(ID_TargetColor,  global.GetTargetColor());
        SetMatFloat(ID_Sensitivity,  global.MasterSensitivity / 100f);
        SetMatFloat(ID_Smoothness,   global.MasterSmoothness / 100f);
        SetMatFloat(ID_SpillRemoval, global.MasterSpillRemoval / 100f);
        SetMatFloat(ID_LumaWeight,   global.MasterLumaWeight / 100f);
        SetMatFloat(ID_EdgeChoke,    global.MasterEdgeChoke / 100f);
        SetMatFloat(ID_PreBlur,      global.MasterPreBlur / 100f * 5.0f);
        SetMatFloat(ID_Brightness,   0f);
        SetMatFloat(ID_Contrast,     1f);
        SetMatFloat(ID_Saturation,   1f);
        SetMatFloat(ID_Hue,          0f);
    }

    // ── 이벤트 핸들러 ─────────────────────────────────────────────────────────

    private void OnConfigReloaded(PhotoBoothConfig config)
    {
        // 핫리로드 시: orientationOverride 변경 가능성 → 재평가 (웹캠이 살아있을 때만)
        if (_webcamTexture != null && _webcamTexture.width > 16 && _webcamTexture.height > 16)
        {
            ApplyOrientation(_webcamTexture.width, _webcamTexture.height);
        }

        // 현재 선택된 배경이 있으면 재적용
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
