using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using System.Collections;
using System.IO;

public enum AppState
{
    Standby, SelectBG, Capture, Calibration, Processing, Result
}

public enum AdminStep 
{ 
    GlobalChroma, LocalBackground 
}

public class AppStateManager : MonoBehaviour
{
    public static AppStateManager Instance { get; private set; }

    [Header("현재 상태")]
    [SerializeField] private AppState currentState = AppState.Standby;

    [Header("관리자 UI 연결")]
    public GameObject adminPanel;
    public AdminStep adminStep = AdminStep.GlobalChroma;
    public int adminBgIndex = 0;
    public Slider sensitivitySlider;
    public Slider smoothnessSlider;
    public Slider spillRemovalSlider;
    public Slider brightnessSlider;
    public Slider contrastSlider;
    public Slider saturationSlider;
    public Slider hueSlider;
    public Toggle useLocalChromaToggle;
    public TextMeshProUGUI adminStepTitleText;
    public TextMeshProUGUI adminTargetNameText;
    private bool _isAdminUIUpdating = false;

    [Header("화면 패널 연결")]
    public GameObject panelStandby;
    public GameObject panelSelectBG;
    public GameObject panelCapture;
    public GameObject panelResult;

    // ※ bgSprites, photoBackground 제거 → OverlayBGManager.bgConfigs로 통합

    [Header("재합성용 참조")]
    public PhotoCaptureManager photoCaptureManager;

    [Header("관람객 무인화 설정 (자동 리셋)")]
    public float idleTimeLimit = 30f;
    private float currentIdleTime = 0f;

    [Header("스탠바이 영상 연출")]
    public VideoPlayer standbyVideoPlayer;
    public string loopVideoFileName = "main.mp4";
    public string transitionVideoFileName = "transition.mov";
    public float transitionDuration = 2.0f;
    public TextMeshProUGUI blinkText;
    public float blinkSpeed = 0.8f;

    [Header("배경 선택 영상 (추가)")]
    public VideoPlayer selectVideoPlayer;
    public string selectVideoFileName = "select.mov";

    private bool _isTransitioning = false;
    private Coroutine _blinkCoroutine;
    private bool _isRecompositing = false;

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else Destroy(gameObject);
    }

    private void Start()
    {
        // 초기 상태를 강제 설정 (ChangeState 가드 우회)
        currentState = AppState.Processing; // 임시값 → 아래서 Standby로 전환 보장
        UpdateUIVisibility();
        ChangeState(AppState.Standby);

        if (sensitivitySlider != null) sensitivitySlider.onValueChanged.AddListener(OnSensitivityChanged);
        if (smoothnessSlider != null) smoothnessSlider.onValueChanged.AddListener(OnSmoothnessChanged);
        if (spillRemovalSlider != null) spillRemovalSlider.onValueChanged.AddListener(OnSpillRemovalChanged);
        if (brightnessSlider != null) brightnessSlider.onValueChanged.AddListener(OnBrightnessChanged);
        if (contrastSlider != null) contrastSlider.onValueChanged.AddListener(OnContrastChanged);
        if (saturationSlider != null) saturationSlider.onValueChanged.AddListener(OnSaturationChanged);
        if (hueSlider != null) hueSlider.onValueChanged.AddListener(OnHueChanged);
        if (useLocalChromaToggle != null) useLocalChromaToggle.onValueChanged.AddListener(OnUseLocalChromaToggled);
    }

    private void Update()
    {
        if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.S))
            ToggleAdminMode();

        if (Input.GetKeyDown(KeyCode.Escape)) ResetToStart();

        // 관리자(Calibration) 모드에서는 마우스/키 입력을 상태전환에 사용하지 않음
        // → ChromaKeyController 의 색상 추출이 정상 작동하도록 보호
        if (currentState == AppState.Calibration)
            return;

        if (currentState == AppState.SelectBG)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) SelectBackgroundAndGoNext(0);
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) SelectBackgroundAndGoNext(1);
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) SelectBackgroundAndGoNext(2);
            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) SelectBackgroundAndGoNext(3);
            if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5)) SelectBackgroundAndGoNext(4);
            if (Input.GetKeyDown(KeyCode.Alpha6) || Input.GetKeyDown(KeyCode.Keypad6)) SelectBackgroundAndGoNext(5);
        }

        if (Input.GetMouseButtonDown(0) || Input.anyKeyDown)
        {
            currentIdleTime = 0f;
            if (currentState == AppState.Standby) PlayStandbyTransition();
        }
        else
        {
            currentIdleTime += Time.deltaTime;
            if (currentState != AppState.Calibration && currentState != AppState.Standby
                && currentIdleTime >= idleTimeLimit)
                ResetToStart();
        }
    }

    public void ChangeState(AppState newState)
    {
        if (currentState == newState) return;

        // ── 이전 상태 정리 (비디오 정지 등) ──
        StopAllVideos();

        currentState = newState;
        currentIdleTime = 0f;
        UpdateUIVisibility();

        // ── 새 상태 진입 처리 ──
        switch (currentState)
        {
            case AppState.Standby:
                StartStandbyLoop();
                if (OverlayBGManager.Instance != null) OverlayBGManager.Instance.HideOverlay();
                break;

            case AppState.SelectBG:
                PlaySelectVideo();
                break;
        }
    }

    /// <summary>모든 비디오 플레이어를 정지시킨다.</summary>
    private void StopAllVideos()
    {
        if (standbyVideoPlayer != null && standbyVideoPlayer.isPlaying)
            standbyVideoPlayer.Stop();
        if (selectVideoPlayer != null && selectVideoPlayer.isPlaying)
            selectVideoPlayer.Stop();
    }

    /// <summary>배경 선택 화면 전용 비디오를 재생한다.</summary>
    private void PlaySelectVideo()
    {
        if (selectVideoPlayer == null) return;

        // RenderTexture 모드일 때 RawImage 동적 연결 (최초 1회)
        if (selectVideoPlayer.renderMode == VideoRenderMode.RenderTexture
            && selectVideoPlayer.targetTexture == null)
        {
            RenderTexture rt = new RenderTexture(1920, 1080, 0);
            selectVideoPlayer.targetTexture = rt;

            RawImage ri = selectVideoPlayer.GetComponent<RawImage>();
            if (ri == null)
            {
                ri = selectVideoPlayer.gameObject.AddComponent<RawImage>();
                ri.rectTransform.anchorMin = Vector2.zero;
                ri.rectTransform.anchorMax = Vector2.one;
                ri.rectTransform.sizeDelta = Vector2.zero;
                ri.rectTransform.anchoredPosition = Vector2.zero;
            }
            ri.texture = rt;
        }

        selectVideoPlayer.url = Path.Combine(Application.streamingAssetsPath, selectVideoFileName);
        selectVideoPlayer.isLooping = true;
        selectVideoPlayer.Play();
    }

    public bool isColorPickingMode = false;

    public void ToggleColorPickMode()
    {
        isColorPickingMode = !isColorPickingMode;
        Debug.Log($"[Admin] 💧 색상 추출(스포이드) 모드: {(isColorPickingMode ? "ON" : "OFF")}");
        
        // 텍스트 시각적 피드백 (찾아서 컬러/텍스트 변경)
        if (adminPanel != null)
        {
            var tmp = adminPanel.transform.Find("ColorPickBtn")?.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.text = isColorPickingMode ? "추출 중... (클릭)" : "스포이드 (색상 추출)";
                tmp.color = isColorPickingMode ? new Color(1f, 0.4f, 0.4f) : Color.white;
            }
        }
    }

    public void OpenStreamingAssetsFolder()
    {
        Application.OpenURL("file://" + Application.streamingAssetsPath);
        Debug.Log("[Admin] 📁 폴더 열기: " + Application.streamingAssetsPath);
    }

    private void ToggleAdminMode()
    {
        if (currentState != AppState.Calibration) 
        {
            ChangeState(AppState.Calibration);
            adminStep = AdminStep.GlobalChroma;
            isColorPickingMode = false; // 진입 시 해제 상태로 시작
            if (PhotoBoothConfigLoader.Instance != null && PhotoBoothConfigLoader.Instance.IsLoaded)
            {
                RefreshAdminUI();
            }
        }
        else 
        {
            ChangeState(AppState.Standby);
            if (OverlayBGManager.Instance != null) OverlayBGManager.Instance.HideOverlay();
        }
    }

    private void UpdateUIVisibility()
    {
        if (panelStandby != null) panelStandby.SetActive(currentState == AppState.Standby);
        if (panelSelectBG != null) panelSelectBG.SetActive(currentState == AppState.SelectBG);
        if (panelCapture != null) panelCapture.SetActive(currentState == AppState.Capture ||
                                                          currentState == AppState.Calibration ||
                                                          currentState == AppState.Processing);
        if (panelResult != null) panelResult.SetActive(currentState == AppState.Result);

        bool isAdmin = (currentState == AppState.Calibration);
        if (adminPanel != null)
        {
            adminPanel.SetActive(isAdmin);
            // 관리자 패널을 카메라 화면 위에 렌더링되도록 최상위로 올림
            if (isAdmin) adminPanel.transform.SetAsLastSibling();
        }
    }

    // ── 스탠바이 연출 ──
    private void StartStandbyLoop()
    {
        _isTransitioning = false;
        if (blinkText != null)
        {
            blinkText.gameObject.SetActive(true);
            blinkText.enabled = true;
            if (_blinkCoroutine != null) StopCoroutine(_blinkCoroutine);
            _blinkCoroutine = StartCoroutine(BlinkRoutine());
        }
        if (standbyVideoPlayer != null)
        {
            standbyVideoPlayer.url = Path.Combine(Application.streamingAssetsPath, loopVideoFileName);
            standbyVideoPlayer.isLooping = true;
            standbyVideoPlayer.Play();
        }
    }

    private void PlayStandbyTransition()
    {
        if (_isTransitioning) return;
        _isTransitioning = true;
        if (_blinkCoroutine != null) StopCoroutine(_blinkCoroutine);
        if (blinkText != null) blinkText.gameObject.SetActive(false);

        if (standbyVideoPlayer != null)
        {
            standbyVideoPlayer.url = Path.Combine(Application.streamingAssetsPath, transitionVideoFileName);
            standbyVideoPlayer.isLooping = false;
            standbyVideoPlayer.Play();
            StartCoroutine(WaitTransitionAndGoNext(transitionDuration));
        }
        else ChangeState(AppState.SelectBG);
    }

    private IEnumerator WaitTransitionAndGoNext(float duration)
    {
        yield return new WaitForSeconds(duration);
        ChangeState(AppState.SelectBG);
    }

    private IEnumerator BlinkRoutine()
    {
        while (!_isTransitioning && blinkText != null)
        {
            blinkText.enabled = !blinkText.enabled;
            yield return new WaitForSeconds(blinkSpeed);
        }
    }

    // ── 배경 선택 → OverlayBGManager 한 곳에서 모두 처리 ──
    public void SelectBackgroundAndGoNext(int index)
    {
        if (OverlayBGManager.Instance != null)
            OverlayBGManager.Instance.SetConfig(index);  // bgSprite + overlay + webcam 한번에

        if (_isRecompositing)
        {
            _isRecompositing = false;
            ChangeState(AppState.Processing);
            StartCoroutine(RecompositeAndReturn());
        }
        else ChangeState(AppState.Capture);
    }

    public void Button_ChangeBGFromResult() { _isRecompositing = true; ChangeState(AppState.SelectBG); }

    private IEnumerator RecompositeAndReturn()
    {
        yield return null;
        if (photoCaptureManager != null)
            yield return StartCoroutine(photoCaptureManager.RecompositeCapture());
        ChangeState(AppState.Result);
    }

    public void Button_RetakePhoto() => ChangeState(AppState.Capture);
    public void ResetToStart() { currentIdleTime = 0f; ChangeState(AppState.Standby); }

    // ── 관리자 패널(Admin UI) 통합 기능 ──
    public void NextAdminStep()
    {
        if (adminStep == AdminStep.GlobalChroma)
        {
            adminStep = AdminStep.LocalBackground;
            adminBgIndex = 0;
            ApplyAdminBackgroundOverlay(adminBgIndex);
        }
        else
        {
            int maxBg = PhotoBoothConfigLoader.Instance.Config.Backgrounds.Count;
            adminBgIndex++;
            if (adminBgIndex >= maxBg) adminBgIndex = 0;
            ApplyAdminBackgroundOverlay(adminBgIndex);
        }
        RefreshAdminUI();
    }

    public void PrevAdminStep()
    {
        if (adminStep == AdminStep.LocalBackground)
        {
            adminBgIndex--;
            if (adminBgIndex < 0)
            {
                adminStep = AdminStep.GlobalChroma;
                ApplyAdminBackgroundOverlay(-1);
            }
            else ApplyAdminBackgroundOverlay(adminBgIndex);
        }
        RefreshAdminUI();
    }

    public void ApplyAndSaveAdminConfig()
    {
        PhotoBoothConfigLoader.Instance?.SaveConfig();
    }

    private void ApplyAdminBackgroundOverlay(int index)
    {
        if (OverlayBGManager.Instance != null)
        {
            if (index < 0) OverlayBGManager.Instance.HideBackgroundOnly();
            else OverlayBGManager.Instance.SetConfig(index);
        }
    }

    private void RefreshAdminUI()
    {
        if (PhotoBoothConfigLoader.Instance == null || !PhotoBoothConfigLoader.Instance.IsLoaded) return;
        var config = PhotoBoothConfigLoader.Instance.Config;
        
        _isAdminUIUpdating = true;

        if (adminStep == AdminStep.GlobalChroma)
        {
            if (adminStepTitleText != null) adminStepTitleText.text = "[1] Global Chroma";
            if (adminTargetNameText != null) adminTargetNameText.text = "Global Master";
            if (useLocalChromaToggle != null) useLocalChromaToggle.gameObject.SetActive(false);

            if (sensitivitySlider != null) sensitivitySlider.value = config.Global.MasterSensitivity;
            if (smoothnessSlider != null) smoothnessSlider.value = config.Global.MasterSmoothness;
            if (spillRemovalSlider != null) spillRemovalSlider.value = config.Global.MasterSpillRemoval;

            // Global 페이지에서는 색상보정 비활성
            SetColorSlidersVisible(false);

            ApplyAdminBackgroundOverlay(-1);
        }
        else
        {
            if (config.Backgrounds.Count <= adminBgIndex) return;

            var bg = config.Backgrounds[adminBgIndex];
            if (adminStepTitleText != null) adminStepTitleText.text = $"[2] BG {adminBgIndex + 1}/{config.Backgrounds.Count}";
            if (adminTargetNameText != null) adminTargetNameText.text = bg.BgName;

            if (useLocalChromaToggle != null)
            {
                useLocalChromaToggle.gameObject.SetActive(true);
                useLocalChromaToggle.isOn = bg.Chroma.UseLocalChroma;
            }

            if (sensitivitySlider != null) sensitivitySlider.value = bg.Chroma.LocalSensitivity;
            if (smoothnessSlider != null) smoothnessSlider.value = bg.Chroma.LocalSmoothness;
            if (spillRemovalSlider != null) spillRemovalSlider.value = bg.Chroma.LocalSpillRemoval;

            // 배경별 색상보정
            SetColorSlidersVisible(true);
            if (brightnessSlider != null) brightnessSlider.value = bg.Color.Brightness;
            if (contrastSlider != null) contrastSlider.value = bg.Color.Contrast;
            if (saturationSlider != null) saturationSlider.value = bg.Color.Saturation;
            if (hueSlider != null) hueSlider.value = bg.Color.Hue;
        }

        _isAdminUIUpdating = false;
        ApplyAdminToPreview();
    }

    private void OnSensitivityChanged(float v)
    {
        if (_isAdminUIUpdating) return;
        var config = PhotoBoothConfigLoader.Instance.Config;
        if (adminStep == AdminStep.GlobalChroma) config.Global.MasterSensitivity = v;
        else config.Backgrounds[adminBgIndex].Chroma.LocalSensitivity = v;
        ApplyAdminToPreview();
    }

    private void OnSmoothnessChanged(float v)
    {
        if (_isAdminUIUpdating) return;
        var config = PhotoBoothConfigLoader.Instance.Config;
        if (adminStep == AdminStep.GlobalChroma) config.Global.MasterSmoothness = v;
        else config.Backgrounds[adminBgIndex].Chroma.LocalSmoothness = v;
        ApplyAdminToPreview();
    }

    private void OnSpillRemovalChanged(float v)
    {
        if (_isAdminUIUpdating) return;
        var config = PhotoBoothConfigLoader.Instance.Config;
        if (adminStep == AdminStep.GlobalChroma) config.Global.MasterSpillRemoval = v;
        else config.Backgrounds[adminBgIndex].Chroma.LocalSpillRemoval = v;
        ApplyAdminToPreview();
    }

    private void OnBrightnessChanged(float v)
    {
        if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground) return;
        PhotoBoothConfigLoader.Instance.Config.Backgrounds[adminBgIndex].Color.Brightness = v;
        ApplyAdminToPreview();
    }

    private void OnContrastChanged(float v)
    {
        if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground) return;
        PhotoBoothConfigLoader.Instance.Config.Backgrounds[adminBgIndex].Color.Contrast = v;
        ApplyAdminToPreview();
    }

    private void OnSaturationChanged(float v)
    {
        if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground) return;
        PhotoBoothConfigLoader.Instance.Config.Backgrounds[adminBgIndex].Color.Saturation = v;
        ApplyAdminToPreview();
    }

    private void OnHueChanged(float v)
    {
        if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground) return;
        PhotoBoothConfigLoader.Instance.Config.Backgrounds[adminBgIndex].Color.Hue = v;
        ApplyAdminToPreview();
    }

    private void OnUseLocalChromaToggled(bool b)
    {
        if (_isAdminUIUpdating) return;
        if (adminStep == AdminStep.LocalBackground)
        {
            PhotoBoothConfigLoader.Instance.Config.Backgrounds[adminBgIndex].Chroma.UseLocalChroma = b;
            ApplyAdminToPreview();
        }
    }

    private void SetColorSlidersVisible(bool visible)
    {
        if (brightnessSlider != null) brightnessSlider.transform.parent.gameObject.SetActive(visible);
        if (contrastSlider != null) contrastSlider.transform.parent.gameObject.SetActive(visible);
        if (saturationSlider != null) saturationSlider.transform.parent.gameObject.SetActive(visible);
        if (hueSlider != null) hueSlider.transform.parent.gameObject.SetActive(visible);
    }

    private void ApplyAdminToPreview()
    {
        ChromaKeyController controller = Object.FindObjectOfType<ChromaKeyController>();
        if (controller != null)
        {
            if (adminStep == AdminStep.GlobalChroma)
            {
                var dummyBg = new BackgroundConfig { BgName = "Preview" };
                controller.ApplyConfig(dummyBg);
            }
            else
            {
                var bg = PhotoBoothConfigLoader.Instance.Config.Backgrounds[adminBgIndex];
                controller.ApplyConfig(bg);
            }
        }
    }
}
