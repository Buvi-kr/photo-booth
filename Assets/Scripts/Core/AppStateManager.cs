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
    public AppState CurrentState => currentState;

    [Header("관리자 UI 연결")]
    public GameObject adminPanel;
    public AdminStep adminStep = AdminStep.GlobalChroma;
    public int adminBgIndex = 0;
    public Slider sensitivitySlider;
    public Slider smoothnessSlider;
    public Slider spillRemovalSlider;
    public Slider lumaWeightSlider;
    public Slider edgeChokeSlider;
    public Slider preBlurSlider;

    public GameObject magnifierPanel;
    public RawImage magnifierRawImage;

    public Slider brightnessSlider;
    public Slider contrastSlider;
    public Slider saturationSlider;
    public Slider hueSlider;
    
    [Header("관리자 UI - 웹캠 변환")]
    public Slider zoomSlider;
    public Slider moveXSlider;
    public Slider moveYSlider;
    public Slider rotationSlider;

    [Header("관리자 UI - 마스크 크롭/페이딩")]
    public Slider cropTopSlider;
    public Slider cropBottomSlider;
    public Slider cropLeftSlider;
    public Slider cropRightSlider;
    public Slider fadeXSlider;
    public Slider fadeYSlider;
    public TextMeshProUGUI adminStepTitleText;
    public TextMeshProUGUI adminTargetNameText;
    private bool _isAdminUIUpdating = false;

    [Header("화면 패널 연결")]
    public GameObject panelStandby;
    public GameObject panelSelectBG;
    public GameObject panelCapture;
    public GameObject panelResult;

    [Header("조이스틱 네비게이션 UI")]
    public RectTransform selectCursor;
    public RectTransform[] bgButtons;
    private int _currentJoystickIndex = 0;
    private float _joystickCooldown = 0f;

    [Header("결과 화면 네비게이션 UI")]
    public RectTransform resultCursor;
    public RectTransform[] resultButtons;
    private int _currentResultIndex = 0;

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
    private float _standbyCooldown = 0f;

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
        if (lumaWeightSlider != null) lumaWeightSlider.onValueChanged.AddListener(OnLumaWeightChanged);
        if (edgeChokeSlider != null) edgeChokeSlider.onValueChanged.AddListener(OnEdgeChokeChanged);
        if (preBlurSlider != null) preBlurSlider.onValueChanged.AddListener(OnPreBlurChanged);

        if (brightnessSlider != null) brightnessSlider.onValueChanged.AddListener(OnBrightnessChanged);
        if (contrastSlider != null) contrastSlider.onValueChanged.AddListener(OnContrastChanged);
        if (saturationSlider != null) saturationSlider.onValueChanged.AddListener(OnSaturationChanged);
        if (hueSlider != null) hueSlider.onValueChanged.AddListener(OnHueChanged);
        
        if (zoomSlider != null) zoomSlider.onValueChanged.AddListener(OnZoomChanged);
        if (moveXSlider != null) moveXSlider.onValueChanged.AddListener(OnMoveXChanged);
        if (moveYSlider != null) moveYSlider.onValueChanged.AddListener(OnMoveYChanged);
        if (rotationSlider != null) rotationSlider.onValueChanged.AddListener(OnRotationChanged);


        
        if (cropTopSlider != null) cropTopSlider.onValueChanged.AddListener(OnCropTopChanged);
        if (cropBottomSlider != null) cropBottomSlider.onValueChanged.AddListener(OnCropBottomChanged);
        if (cropLeftSlider != null) cropLeftSlider.onValueChanged.AddListener(OnCropLeftChanged);
        if (cropRightSlider != null) cropRightSlider.onValueChanged.AddListener(OnCropRightChanged);
        if (fadeXSlider != null) fadeXSlider.onValueChanged.AddListener(OnFadeXChanged);
        if (fadeYSlider != null) fadeYSlider.onValueChanged.AddListener(OnFadeYChanged);
    }

    private void Update()
    {
        if (_standbyCooldown > 0f) _standbyCooldown -= Time.deltaTime;
        if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.S))
            ToggleAdminMode();

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (currentState == AppState.Result) ChangeState(AppState.SelectBG);
            else if (currentState == AppState.Capture) ChangeState(AppState.SelectBG);
            else if (currentState == AppState.SelectBG) ResetToStart();
            else ResetToStart();
        }

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

            // ── 조이스틱 / 방향키 네비게이션 ──
            if (_joystickCooldown > 0f) _joystickCooldown -= Time.deltaTime;
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if ((Mathf.Abs(h) > 0.5f || Mathf.Abs(v) > 0.5f) && _joystickCooldown <= 0f)
            {
                MoveJoystickCursor((h > 0 || v < 0) ? 1 : -1);
                _joystickCooldown = 0.8f; // 0.8초 쿨다운으로 상향 (중복 입력 방지)
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) MoveJoystickCursor(1);
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) MoveJoystickCursor(-1);

            // 조이스틱 버튼 (Fire1, Submit, Enter)
            if (Input.GetButtonDown("Submit") || Input.GetKeyDown(KeyCode.Return))
            {
                SelectBackgroundAndGoNext(_currentJoystickIndex);
            }
        }

        // 셀렉트 커서(붉은 박스) 부드러운 이동 및 Pulse 애니메이션
        if (currentState == AppState.SelectBG && selectCursor != null && bgButtons != null && bgButtons.Length > _currentJoystickIndex)
        {
            if (bgButtons[_currentJoystickIndex] != null)
            {
                if (!selectCursor.gameObject.activeSelf) selectCursor.gameObject.SetActive(true);
                selectCursor.SetAsLastSibling();
                // 버튼과 물리적 상태를 100% 동일하게 맞춰서 밀림 원천 차단
                selectCursor.anchorMin = bgButtons[_currentJoystickIndex].anchorMin;
                selectCursor.anchorMax = bgButtons[_currentJoystickIndex].anchorMax;
                selectCursor.pivot = bgButtons[_currentJoystickIndex].pivot;
                selectCursor.position = Vector3.Lerp(selectCursor.position, bgButtons[_currentJoystickIndex].position, Time.deltaTime * 15f);
                selectCursor.sizeDelta = Vector2.Lerp(selectCursor.sizeDelta, bgButtons[_currentJoystickIndex].sizeDelta, Time.deltaTime * 15f);

                // Pulse 애니메이션 (0.4 ~ 1.0)
                CanvasGroup cg = selectCursor.GetComponent<CanvasGroup>();
                if (cg == null) cg = selectCursor.gameObject.AddComponent<CanvasGroup>();
                cg.alpha = Mathf.PingPong(Time.time * 2f, 0.6f) + 0.4f;
            }
        }
        else if (selectCursor != null && selectCursor.gameObject.activeSelf)
        {
            selectCursor.gameObject.SetActive(false);
        }

        // 결과 화면 커서 연출 및 로직
        if (currentState == AppState.Result)
        {
            if (_joystickCooldown > 0f) _joystickCooldown -= Time.deltaTime;
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            if ((Mathf.Abs(h) > 0.5f || Mathf.Abs(v) > 0.5f) && _joystickCooldown <= 0f)
            {
                MoveResultCursor((h > 0 || v < 0) ? 1 : -1);
                _joystickCooldown = 0.8f; // 0.8초 쿨다운
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) MoveResultCursor(1);
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) MoveResultCursor(-1);

            if (Input.GetButtonDown("Submit") || Input.GetKeyDown(KeyCode.Return))
            {
                ExecuteResultButton();
            }

            if (resultCursor != null && resultButtons != null && resultButtons.Length > _currentResultIndex)
            {
                if (resultButtons[_currentResultIndex] != null)
                {
                    if (!resultCursor.gameObject.activeSelf) resultCursor.gameObject.SetActive(true);
                    resultCursor.SetAsLastSibling();
                    // 버튼과 물리적 상태를 100% 동일하게 맞춰서 밀림 원천 차단
                    resultCursor.anchorMin = resultButtons[_currentResultIndex].anchorMin;
                    resultCursor.anchorMax = resultButtons[_currentResultIndex].anchorMax;
                    resultCursor.pivot = resultButtons[_currentResultIndex].pivot;
                    resultCursor.position = Vector3.Lerp(resultCursor.position, resultButtons[_currentResultIndex].position, Time.deltaTime * 15f);
                    resultCursor.sizeDelta = Vector2.Lerp(resultCursor.sizeDelta, resultButtons[_currentResultIndex].sizeDelta, Time.deltaTime * 15f);

                    // Pulse 애니메이션 (0.4 ~ 1.0)
                    CanvasGroup cg = resultCursor.GetComponent<CanvasGroup>();
                    if (cg == null) cg = resultCursor.gameObject.AddComponent<CanvasGroup>();
                    cg.alpha = Mathf.PingPong(Time.time * 2f, 0.6f) + 0.4f;
                }
            }
        }
        else if (resultCursor != null && resultCursor.gameObject.activeSelf)
        {
            resultCursor.gameObject.SetActive(false);
        }

        if (Input.GetMouseButtonDown(0) || Input.anyKeyDown)
        {
            currentIdleTime = 0f;
            if (currentState == AppState.Standby && _standbyCooldown <= 0f) 
            {
                // ESC나 관리자 모드 진입/해제에 사용된 키가 여전히 눌려있다면 무시
                if (!Input.GetKey(KeyCode.Escape) && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.LeftAlt))
                {
                    PlayStandbyTransition();
                }
            }
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
                _standbyCooldown = 0.5f; // 상태 진입 직후의 잔여 키입력 방지
                StartStandbyLoop();
                if (OverlayBGManager.Instance != null) OverlayBGManager.Instance.HideOverlay();
                break;

            case AppState.SelectBG:
                _currentJoystickIndex = 0; // 진입 시 항상 첫번째 배경에 포커스
                if (OverlayBGManager.Instance != null) OverlayBGManager.Instance.SetConfig(0); // 데이터 동기화
                
                if (selectCursor != null && bgButtons != null && bgButtons.Length > 0 && bgButtons[0] != null)
                {
                    // 애니메이션 없이 즉각적으로 이동 및 크기 동기화
                    selectCursor.position = bgButtons[0].position; 
                    selectCursor.sizeDelta = bgButtons[0].sizeDelta;
                }
                PlaySelectVideo();
                break;

            case AppState.Result:
                _currentResultIndex = 0; // 결과 화면 버튼 포커스 초기화
                if (resultCursor != null && resultButtons != null && resultButtons.Length > 0 && resultButtons[0] != null)
                {
                    resultCursor.position = resultButtons[0].position;
                }
                LoadResultBackground();
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
        if (panelSelectBG != null) 
        {
            panelSelectBG.SetActive(currentState == AppState.SelectBG);
            if (currentState == AppState.SelectBG)
            {
                // [Hierarchy 최적화] 렌더링 순서 조정 (아래쪽일수록 화면 앞쪽)
                // 1. 안내 영상(VideoPlayer)을 버튼 위로 올려서 버튼들을 가림
                if (selectVideoPlayer != null) selectVideoPlayer.transform.SetSiblingIndex(panelSelectBG.transform.childCount - 2);
                // 2. 셀렉트 커서를 가장 마지막(가장 앞)으로 보내서 영상 위에 보이게 함
                if (selectCursor != null) selectCursor.SetAsLastSibling();
                // 3. 자막(Subtitle) 하단 배치 안정화 (Bottom-Center 고정)
                Transform subtitle = panelSelectBG.transform.Find("SelectBG_Subtitle");
                if (subtitle != null)
                {
                    RectTransform sRT = subtitle.GetComponent<RectTransform>();
                    sRT.anchorMin = new Vector2(0.5f, 0f);
                    sRT.anchorMax = new Vector2(0.5f, 0f);
                    sRT.anchoredPosition = new Vector2(0, 150f);
                    sRT.SetAsLastSibling();
                }
                Transform subtitlePanel = panelSelectBG.transform.Find("SelectBG_Subtitle_Panel");
                if (subtitlePanel != null)
                {
                    RectTransform spRT = subtitlePanel.GetComponent<RectTransform>();
                    spRT.anchorMin = new Vector2(0.5f, 0f);
                    spRT.anchorMax = new Vector2(0.5f, 0f);
                    spRT.anchoredPosition = new Vector2(0, 150f);
                    spRT.SetAsLastSibling();
                }
                if (subtitle != null) subtitle.SetAsLastSibling(); // 패널보다 글자가 앞
            }
        }
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

    private float _lastSelectTime = 0f;

    private bool _isSelecting = false;

    // ── 배경 선택 → OverlayBGManager 한 곳에서 모두 처리 ──
    public void SelectBackgroundAndGoNext(int index)
    {
        if (Time.time - _lastSelectTime < 1.0f || _isSelecting) return; // 연속 클릭 방지
        _lastSelectTime = Time.time;

        if (OverlayBGManager.Instance != null)
            OverlayBGManager.Instance.SetConfig(index);  // bgSprite + overlay + webcam 한번에

        // 곧바로 화면을 넘기지 않고 선택 효과를 볼 수 있도록 딜레이 처리
        StartCoroutine(DelayAndGoCapture());
    }

    private System.Collections.IEnumerator DelayAndGoCapture()
    {
        _isSelecting = true;
        yield return new WaitForSeconds(0.8f); // 0.8초 대기 후 촬영 모드로
        ChangeState(AppState.Capture);
        _isSelecting = false;
    }

    public void Button_RetakePhoto() => ChangeState(AppState.Capture);
    public void ResetToStart() { currentIdleTime = 0f; ChangeState(AppState.Standby); }

    // ── 관리자 패널(Admin UI) 통합 기능 ──
    public void NextAdminStep()
    {
        var cfg = GetConfig();
        if (cfg == null) return;

        if (adminStep == AdminStep.GlobalChroma)
        {
            adminStep = AdminStep.LocalBackground;
            adminBgIndex = 0;
            ApplyAdminBackgroundOverlay(adminBgIndex);
        }
        else
        {
            adminBgIndex = (adminBgIndex + 1) % Mathf.Max(1, cfg.Backgrounds.Count);
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

    public void ResetAdminCurrentBackground()
    {
        if (_isAdminUIUpdating || PhotoBoothConfigLoader.Instance == null) return;
        var config = PhotoBoothConfigLoader.Instance.Config;
        
        if (adminStep == AdminStep.GlobalChroma)
        {
            config.Global.TargetColor = "#00B140";
            config.Global.MasterSensitivity = 0.0f;
            config.Global.MasterSmoothness = 0.0f;
            config.Global.MasterSpillRemoval = 0.0f;
            config.Global.MasterLumaWeight = 0.0f;
            config.Global.MasterEdgeChoke = 0.0f;
            config.Global.MasterPreBlur = 0.0f;
        }
        else if (adminStep == AdminStep.LocalBackground)
        {
            var bg = config.Backgrounds[adminBgIndex];
            
            // Chroma 초기화 (전부 0)
            bg.Chroma.UseLocalChroma = true; // 개별 초기화이므로 강제 적용 후 0으로 만듦
            bg.Chroma.LocalTargetColor = "#00B140";
            bg.Chroma.LocalSensitivity = 0.0f;
            bg.Chroma.LocalSmoothness = 0.0f;
            bg.Chroma.LocalSpillRemoval = 0.0f;
            bg.Chroma.LocalLumaWeight = 0.0f;
            bg.Chroma.LocalEdgeChoke = 0.0f;
            bg.Chroma.LocalPreBlur = 0.0f;

            // Transform 초기화
            bg.Transform.Zoom = 100.0f; // 기본 확대율 100%
            bg.Transform.MoveX = 0f;
            bg.Transform.MoveY = 0f;
            bg.Transform.Rotation = 0f;
            
            // Crop / Fade 초기화
            bg.Crop.Top = 0;
            bg.Crop.Bottom = 0;
            bg.Crop.Left = 0;
            bg.Crop.Right = 0;
            bg.Crop.FadeX = 0;
            bg.Crop.FadeY = 0;
            
            // Color 보정 초기화
            bg.Color.Brightness = 0.0f;
            bg.Color.Contrast = 100.0f;
            bg.Color.Saturation = 100.0f;
            bg.Color.Hue = 0f;
        }
        
        RefreshAdminUI();
        ApplyAdminToPreview();
        Debug.Log($"[Admin] {(adminStep == AdminStep.GlobalChroma ? "Global 마스터" : $"배경 {adminBgIndex}번")} 설정이 최초 상태(0)로 초기화되었습니다.");
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

    public void RefreshAdminUI()
    {
        if (PhotoBoothConfigLoader.Instance == null || !PhotoBoothConfigLoader.Instance.IsLoaded) return;
        var config = PhotoBoothConfigLoader.Instance.Config;
        
        _isAdminUIUpdating = true;

        Color targetC = Color.green;

        if (adminStep == AdminStep.GlobalChroma)
        {
            if (adminStepTitleText != null) adminStepTitleText.text = "[1] Global Chroma";
            if (adminTargetNameText != null) adminTargetNameText.text = "Global Master";

            if (sensitivitySlider != null) sensitivitySlider.value = config.Global.MasterSensitivity;
            if (smoothnessSlider != null) smoothnessSlider.value = config.Global.MasterSmoothness;
            if (spillRemovalSlider != null) spillRemovalSlider.value = config.Global.MasterSpillRemoval;
            if (lumaWeightSlider != null) lumaWeightSlider.value = config.Global.MasterLumaWeight;
            if (edgeChokeSlider != null) edgeChokeSlider.value = config.Global.MasterEdgeChoke;
            if (preBlurSlider != null) preBlurSlider.value = config.Global.MasterPreBlur;

            if (cropTopSlider != null) cropTopSlider.value = config.Global.MasterCrop.Top;
            if (cropBottomSlider != null) cropBottomSlider.value = config.Global.MasterCrop.Bottom;
            if (cropLeftSlider != null) cropLeftSlider.value = config.Global.MasterCrop.Left;
            if (cropRightSlider != null) cropRightSlider.value = config.Global.MasterCrop.Right;
            if (fadeXSlider != null) fadeXSlider.value = config.Global.MasterCrop.FadeX;
            if (fadeYSlider != null) fadeYSlider.value = config.Global.MasterCrop.FadeY;
            
            ColorUtility.TryParseHtmlString(config.Global.TargetColor, out targetC);

            // Global 페이지에서는 크로마와 마스크 활성, 색상보정과 웹캠 변환 비활성
            SetChromaSlidersVisible(true);
            SetMaskSlidersVisible(true);
            SetColorSlidersVisible(false);
            SetTransformSlidersVisible(false);

            ApplyAdminBackgroundOverlay(-1);
        }
        else
        {
            if (config.Backgrounds.Count <= adminBgIndex) return;

            var bg = config.Backgrounds[adminBgIndex];
            if (adminStepTitleText != null) adminStepTitleText.text = $"[2] BG {adminBgIndex + 1}/{config.Backgrounds.Count}";
            if (adminTargetNameText != null) adminTargetNameText.text = bg.BgName;

            ColorUtility.TryParseHtmlString(config.Global.TargetColor, out targetC);

            // 배경별 페이지에서도 색상보정, 웹캠 변환, 크로마키(Global 제어용) 활성
            SetChromaSlidersVisible(true);
            SetMaskSlidersVisible(false);
            SetColorSlidersVisible(true);
            SetTransformSlidersVisible(true);
            
            if (brightnessSlider != null) brightnessSlider.value = bg.Color.Brightness;
            if (contrastSlider != null) contrastSlider.value = bg.Color.Contrast;
            if (saturationSlider != null) saturationSlider.value = bg.Color.Saturation;
            if (hueSlider != null) hueSlider.value = bg.Color.Hue;

            if (zoomSlider != null) zoomSlider.value = bg.Transform.Zoom;
            if (moveXSlider != null) moveXSlider.value = bg.Transform.MoveX;
            if (moveYSlider != null) moveYSlider.value = bg.Transform.MoveY;
            if (rotationSlider != null) rotationSlider.value = bg.Transform.Rotation;
        }

        _isAdminUIUpdating = false;
        ApplyAdminToPreview();
    }

    // 슬라이더 콜백 공통 헬퍼
    private PhotoBoothConfig GetConfig()
    {
        var loader = PhotoBoothConfigLoader.Instance;
        return (loader != null && loader.IsLoaded) ? loader.Config : null;
    }

    private BackgroundConfig GetCurrentBg()
    {
        var cfg = GetConfig();
        if (cfg == null || cfg.Backgrounds == null) return null;
        if (adminBgIndex < 0 || adminBgIndex >= cfg.Backgrounds.Count) return null;
        return cfg.Backgrounds[adminBgIndex];
    }

    private void OnPreBlurChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterPreBlur = v; ApplyAdminToPreview();
    }

    private void OnSensitivityChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterSensitivity = v; ApplyAdminToPreview();
    }

    private void OnSmoothnessChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterSmoothness = v; ApplyAdminToPreview();
    }

    private void OnSpillRemovalChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterSpillRemoval = v; ApplyAdminToPreview();
    }

    private void OnLumaWeightChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterLumaWeight = v; ApplyAdminToPreview();
    }

    private void OnEdgeChokeChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterEdgeChoke = v; ApplyAdminToPreview();
    }

    private void OnBrightnessChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Color.Brightness = v; ApplyAdminToPreview();
    }

    private void OnContrastChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Color.Contrast = v; ApplyAdminToPreview();
    }

    private void OnSaturationChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Color.Saturation = v; ApplyAdminToPreview();
    }

    private void OnHueChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Color.Hue = v; ApplyAdminToPreview();
    }

    private void OnZoomChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Transform.Zoom = v; ApplyAdminToPreview();
    }

    private void OnMoveXChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Transform.MoveX = v; ApplyAdminToPreview();
    }

    private void OnMoveYChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Transform.MoveY = v; ApplyAdminToPreview();
    }

    private void OnRotationChanged(float v)
    {
        var bg = GetCurrentBg(); if (_isAdminUIUpdating || adminStep != AdminStep.LocalBackground || bg == null) return;
        bg.Transform.Rotation = v; ApplyAdminToPreview();
    }

    private void OnCropTopChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterCrop.Top = Mathf.RoundToInt(v); ApplyAdminToPreview();
    }

    private void OnCropBottomChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterCrop.Bottom = Mathf.RoundToInt(v); ApplyAdminToPreview();
    }

    private void OnCropLeftChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterCrop.Left = Mathf.RoundToInt(v); ApplyAdminToPreview();
    }

    private void OnCropRightChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterCrop.Right = Mathf.RoundToInt(v); ApplyAdminToPreview();
    }

    private void OnFadeXChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterCrop.FadeX = Mathf.RoundToInt(v); ApplyAdminToPreview();
    }

    private void OnFadeYChanged(float v)
    {
        var cfg = GetConfig(); if (_isAdminUIUpdating || cfg == null) return;
        cfg.Global.MasterCrop.FadeY = Mathf.RoundToInt(v); ApplyAdminToPreview();
    }


    private void SetColorSlidersVisible(bool visible)
    {
        if (brightnessSlider != null && brightnessSlider.transform.parent != null) brightnessSlider.transform.parent.gameObject.SetActive(visible);
        if (contrastSlider != null && contrastSlider.transform.parent != null) contrastSlider.transform.parent.gameObject.SetActive(visible);
        if (saturationSlider != null && saturationSlider.transform.parent != null) saturationSlider.transform.parent.gameObject.SetActive(visible);
        if (hueSlider != null && hueSlider.transform.parent != null) hueSlider.transform.parent.gameObject.SetActive(visible);
    }

    private void SetTransformSlidersVisible(bool visible)
    {
        if (zoomSlider != null && zoomSlider.transform.parent != null) zoomSlider.transform.parent.gameObject.SetActive(visible);
        if (moveXSlider != null && moveXSlider.transform.parent != null) moveXSlider.transform.parent.gameObject.SetActive(visible);
        if (moveYSlider != null && moveYSlider.transform.parent != null) moveYSlider.transform.parent.gameObject.SetActive(visible);
        if (rotationSlider != null && rotationSlider.transform.parent != null) rotationSlider.transform.parent.gameObject.SetActive(visible);
    }

    private void SetMaskSlidersVisible(bool visible)
    {
        if (cropTopSlider != null && cropTopSlider.transform.parent != null) cropTopSlider.transform.parent.gameObject.SetActive(visible);
        if (cropBottomSlider != null && cropBottomSlider.transform.parent != null) cropBottomSlider.transform.parent.gameObject.SetActive(visible);
        if (cropLeftSlider != null && cropLeftSlider.transform.parent != null) cropLeftSlider.transform.parent.gameObject.SetActive(visible);
        if (cropRightSlider != null && cropRightSlider.transform.parent != null) cropRightSlider.transform.parent.gameObject.SetActive(visible);
        if (fadeXSlider != null && fadeXSlider.transform.parent != null) fadeXSlider.transform.parent.gameObject.SetActive(visible);
        if (fadeYSlider != null && fadeYSlider.transform.parent != null) fadeYSlider.transform.parent.gameObject.SetActive(visible);
    }

    private void SetChromaSlidersVisible(bool visible)
    {
        if (sensitivitySlider != null && sensitivitySlider.transform.parent != null) sensitivitySlider.transform.parent.gameObject.SetActive(visible);
        if (smoothnessSlider != null && smoothnessSlider.transform.parent != null) smoothnessSlider.transform.parent.gameObject.SetActive(visible);
        if (spillRemovalSlider != null && spillRemovalSlider.transform.parent != null) spillRemovalSlider.transform.parent.gameObject.SetActive(visible);
        if (edgeChokeSlider != null && edgeChokeSlider.transform.parent != null) edgeChokeSlider.transform.parent.gameObject.SetActive(visible);
        if (lumaWeightSlider != null && lumaWeightSlider.transform.parent != null) lumaWeightSlider.transform.parent.gameObject.SetActive(visible);
        if (preBlurSlider != null && preBlurSlider.transform.parent != null) preBlurSlider.transform.parent.gameObject.SetActive(visible);
    }

    private void MoveJoystickCursor(int step)
    {
        if (bgButtons == null || bgButtons.Length == 0) return;
        
        _currentJoystickIndex += step;
        if (_currentJoystickIndex >= bgButtons.Length) _currentJoystickIndex = 0;
        else if (_currentJoystickIndex < 0) _currentJoystickIndex = bgButtons.Length - 1;
    }

    private void MoveResultCursor(int step)
    {
        if (resultButtons == null || resultButtons.Length == 0) return;
        
        _currentResultIndex += step;
        if (_currentResultIndex >= resultButtons.Length) _currentResultIndex = 0;
        else if (_currentResultIndex < 0) _currentResultIndex = resultButtons.Length - 1;
    }

    private float _lastResultButtonTime = 0f;

    private void ExecuteResultButton()
    {
        if (resultButtons == null || resultButtons.Length == 0) return;
        if (Time.time - _lastResultButtonTime < 1.0f) return; // 중복 클릭 방지
        _lastResultButtonTime = Time.time;

        if (resultButtons[_currentResultIndex] != null)
        {
            Button btn = resultButtons[_currentResultIndex].GetComponent<Button>();
            if (btn != null) btn.onClick.Invoke();
        }
    }

    private RawImage _resultBgImg;
    private Texture2D _cachedResultBgTex;

    private void LoadResultBackground()
    {
        if (panelResult == null) return;
        if (_resultBgImg == null)
        {
            Transform existingBg = panelResult.transform.Find("Result_DynamicBG");
            if (existingBg != null) 
            {
                _resultBgImg = existingBg.GetComponent<RawImage>();
            }
            else
            {
                GameObject bgObj = new GameObject("Result_DynamicBG");
                bgObj.transform.SetParent(panelResult.transform, false);
                bgObj.transform.SetAsFirstSibling();
                _resultBgImg = bgObj.AddComponent<RawImage>();
                RectTransform rt = _resultBgImg.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
            }
        }

        if (_cachedResultBgTex == null)
        {
            string jpgPath = Path.Combine(Application.streamingAssetsPath, "result_background.jpg");
            string pngPath = Path.Combine(Application.streamingAssetsPath, "result_background.png");
            string finalPath = File.Exists(jpgPath) ? jpgPath : (File.Exists(pngPath) ? pngPath : null);

            if (finalPath != null)
            {
                byte[] bytes = File.ReadAllBytes(finalPath);
                _cachedResultBgTex = new Texture2D(2, 2);
                _cachedResultBgTex.LoadImage(bytes);
            }
            else
            {
                Debug.LogWarning("⚠️ result_background.jpg 또는 .png를 StreamingAssets 폴더에서 찾을 수 없습니다.");
            }
        }

        if (_cachedResultBgTex != null && _resultBgImg.texture != _cachedResultBgTex)
        {
            _resultBgImg.texture = _cachedResultBgTex;
        }
    }

    private void ApplyAdminToPreview()
    {
        ChromaKeyController controller = Object.FindObjectOfType<ChromaKeyController>();
        if (controller == null) return;

        if (adminStep == AdminStep.GlobalChroma)
        {
            var dummyBg = new BackgroundConfig
            {
                BgName    = "Preview",
                Transform = new TransformConfig { Zoom = 100f, MoveX = 0f, MoveY = 0f, Rotation = 0f },
                Color     = new ColorGradingConfig { Brightness = 0f, Contrast = 100f, Saturation = 100f, Hue = 0f }
            };
            controller.ApplyConfig(dummyBg);
        }
        else
        {
            var bg = GetCurrentBg();
            if (bg != null) controller.ApplyConfig(bg);
        }
    }
}
