using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.IO;

public class PhotoCaptureManager : MonoBehaviour
{
    [Header("UI 연결")]
    public TextMeshProUGUI timerText;
    public Image flashScreen;

    [Header("결과 확인용 UI")]
    public RawImage resultPreview;

    [Header("촬영 중에만 숨길 UI (찍은 후 다시 보임)")]
    public GameObject[] uiToHide;

    [Header("영구 비활성 UI (촬영하기 버튼, 회색 레이어 등 → 자동 흐름이라 불필요)")]
    public GameObject[] uiToHidePermanently;

    [Header("타이머 설정")]
    [Tooltip("카운트다운 길이 (초). 권장 5~10")]
    [Range(3, 15)] public int countdownSeconds = 8;
    [Tooltip("Capture 화면 진입 후 자동 카운트다운 시작 (촬영하기 버튼 안 눌러도 됨)")]
    public bool autoStartOnEnter = true;
    [Tooltip("진입 후 카운트다운 시작까지 준비 시간 (초)")]
    public float autoStartDelay = 0.5f;

    [Header("저장 설정")]
    public string saveFolderName = "MyPhotoBooth";

    private bool isCapturing = false;
    private Coroutine _captureCoroutine;
    private bool _autoStartArmed = false;
    private float _autoStartTimer = 0f;

    private void Start()
    {
        if (timerText != null)
        {
            timerText.text = "";
            ConfigureTimerVisual();
        }

        // ① Inspector 명시 목록 비활성화
        if (uiToHidePermanently != null)
        {
            foreach (var go in uiToHidePermanently)
                if (go != null) go.SetActive(false);
        }

        // ② 자동 탐색: 흔한 촬영 버튼/레이어 이름 매칭 → 운영자가 인스펙터 빠뜨려도 안전
        AutoHideLegacyCaptureUI();
    }

    /// <summary>
    /// 자동 흐름으로 바뀌면서 불필요해진 옛 UI(촬영하기 버튼, 그 부모 패널)을
    /// 이름 기반으로 찾아 비활성화. 부모만 꺼져도 자식(CaptureBtn 등) 같이 꺼짐.
    /// </summary>
    private void AutoHideLegacyCaptureUI()
    {
        string[] candidateNames = {
            "BottomPanel", "CaptureBottom", "Capture_Bottom",
            "CaptureBtn",  "TakePhotoBtn", "촬영하기",
            "CaptureButton", "TakeBtn"
        };

        // 비활성 오브젝트도 포함 검색
        var allRects = FindObjectsOfType<RectTransform>(true);
        foreach (var rt in allRects)
        {
            string n = rt.gameObject.name;
            for (int i = 0; i < candidateNames.Length; i++)
            {
                if (n.Equals(candidateNames[i], System.StringComparison.OrdinalIgnoreCase))
                {
                    if (rt.gameObject.activeSelf)
                    {
                        rt.gameObject.SetActive(false);
                        Debug.Log($"[PhotoCapture] 자동 흐름 전환 → '{n}' 영구 비활성화");
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 타이머 텍스트 시각 설정.
    /// 컨셉: 네온 사인 — 흰 글자 + 시안 글로우 + 검은 외곽선.
    /// 카운트다운 막바지에 색이 노랑→빨강으로 변하면서 긴장감 부여.
    /// </summary>
    private void ConfigureTimerVisual()
    {
        if (timerText == null) return;

        // 기본 스타일
        timerText.fontStyle  = TMPro.FontStyles.Bold;
        timerText.alignment  = TMPro.TextAlignmentOptions.Center;
        timerText.enableAutoSizing = false;
        if (timerText.fontSize < 200) timerText.fontSize = 300;

        // ① TMP 빌트인 외곽선 (어떤 셰이더에서도 안정적, 가장 우선)
        timerText.color        = Color.white;
        timerText.outlineColor = new Color32(0, 0, 0, 255);
        timerText.outlineWidth = 0.22f;

        // ② 글로우 효과 (Underlay) — 셰이더가 지원할 때만
        var sharedMat = timerText.fontSharedMaterial;
        if (sharedMat != null && sharedMat.HasProperty("_UnderlayColor"))
        {
            Material mat = new Material(sharedMat);
            mat.EnableKeyword("UNDERLAY_ON");
            mat.SetColor("_UnderlayColor", new Color(0f, 1f, 1f, 0.75f)); // 시안 글로우 (기본)
            mat.SetFloat("_UnderlayOffsetX", 0f);
            mat.SetFloat("_UnderlayOffsetY", 0f);
            mat.SetFloat("_UnderlayDilate",  1f);   // 외곽선 바깥으로 확장
            mat.SetFloat("_UnderlaySoftness", 0.9f); // 부드러운 가장자리
            timerText.fontSharedMaterial = mat;
        }
    }

    /// <summary>
    /// 남은 초에 따라 타이머 색상/글로우 변경.
    /// 4초 이상: 시안 (여유), 3-2초: 노랑 (준비), 1초: 빨강 (찰칵!)
    /// </summary>
    private void UpdateTimerColor(int remaining)
    {
        if (timerText == null) return;

        Color face;
        Color glow;
        if (remaining <= 1)
        {
            face = new Color(1f, 0.35f, 0.35f);                 // 빨강
            glow = new Color(1f, 0.2f, 0.2f, 0.85f);
        }
        else if (remaining <= 3)
        {
            face = new Color(1f, 0.95f, 0.3f);                  // 노랑
            glow = new Color(1f, 0.8f, 0.1f, 0.75f);
        }
        else
        {
            face = Color.white;                                 // 흰색
            glow = new Color(0f, 1f, 1f, 0.75f);                // 시안
        }

        timerText.color = face;

        // fontMaterial 은 인스턴스 (수정해도 다른 텍스트 영향 없음)
        var instMat = timerText.fontMaterial;
        if (instMat != null && instMat.HasProperty("_UnderlayColor"))
        {
            instMat.SetColor("_UnderlayColor", glow);
        }
    }

    private AppState _lastState = AppState.Standby;
    private float _captureCooldown = 0f;

    private void Update()
    {
        var appState = AppStateManager.Instance;
        if (appState == null) return;

        if (_lastState != appState.CurrentState)
        {
            AppState prev = _lastState;
            _lastState = appState.CurrentState;

            // Capture 진입 시 0.5초 잔여 입력 차단 쿨다운 + 자동 시작 무장
            if (_lastState == AppState.Capture)
            {
                _captureCooldown = 0.5f;
                if (autoStartOnEnter)
                {
                    _autoStartArmed = true;
                    _autoStartTimer = autoStartDelay;
                }
            }

            // Capture/Processing → 그 외 상태로 빠져나갈 때(ESC, 관리자모드 등) 진행 중 촬영 강제 취소
            bool wasCaptureFlow = (prev == AppState.Capture || prev == AppState.Processing);
            bool nowOutOfFlow   = (_lastState != AppState.Capture && _lastState != AppState.Processing);
            if (wasCaptureFlow && nowOutOfFlow)
            {
                _autoStartArmed = false; // 이탈 시 자동 시작도 취소
                CancelCapture();
            }
        }

        if (_captureCooldown > 0f) _captureCooldown -= Time.deltaTime;

        // 자동 시작: Capture 상태이고 쿨다운 끝났으면 delay 후 한 번만 발동
        if (_autoStartArmed && appState.CurrentState == AppState.Capture && _captureCooldown <= 0f)
        {
            _autoStartTimer -= Time.deltaTime;
            if (_autoStartTimer <= 0f && !isCapturing)
            {
                _autoStartArmed = false;
                TakePhoto();
            }
        }

        // (기존) Enter 키 백업 — autoStart 비활성 환경 또는 디버그용
        if (Input.GetKeyDown(KeyCode.Return) && appState.CurrentState == AppState.Capture)
        {
            if (_captureCooldown <= 0f)
            {
                _autoStartArmed = false;
                TakePhoto();
            }
        }
    }

    public void TakePhoto()
    {
        if (isCapturing) return;

        if (QRServerManager.Instance != null && !QRServerManager.Instance.isServerReady)
        {
            Debug.LogWarning("⏳ 서버 부팅 중입니다. 잠시 후 다시 시도해주세요!");
            return;
        }

        _captureCoroutine = StartCoroutine(CaptureRoutine());
    }

    /// <summary>
    /// 진행 중인 촬영 코루틴을 즉시 중단하고 UI/플래그를 원상복구.
    /// ESC/관리자모드 진입 등 Capture 흐름에서 이탈할 때 자동 호출.
    /// </summary>
    public void CancelCapture()
    {
        if (_captureCoroutine != null)
        {
            StopCoroutine(_captureCoroutine);
            _captureCoroutine = null;
            Debug.Log("[PhotoCapture] 진행 중인 촬영 취소됨.");
        }

        if (timerText != null)
        {
            timerText.text = "";
            timerText.rectTransform.localScale = Vector3.one; // 펄스 도중 취소돼도 스케일 복구
        }
        if (flashScreen != null)
        {
            flashScreen.color = new Color(1, 1, 1, 0);
            flashScreen.gameObject.SetActive(false);
        }
        if (uiToHide != null)
        {
            foreach (GameObject ui in uiToHide)
                if (ui != null) ui.SetActive(true);
        }
        isCapturing = false;
        _autoStartArmed = false;
    }

    private IEnumerator CaptureRoutine()
    {
        isCapturing = true;

        AppStateManager.Instance.ChangeState(AppState.Processing);

        for (int i = countdownSeconds; i > 0; i--)
        {
            if (timerText != null)
            {
                timerText.text = i.ToString();
                UpdateTimerColor(i);              // 단계별 색상 (시안→노랑→빨강)
                StartCoroutine(TimerTickPulse()); // 1.5→1.0 스케일 펄스
            }
            yield return new WaitForSeconds(1f);
        }

        if (timerText != null) timerText.text = "";

        foreach (GameObject ui in uiToHide)
            if (ui != null) ui.SetActive(false);

        // 미리보기에서 보이는 화면 그대로 캡처 (셰이더/크롭/회전이 이미 적용된 상태)
        yield return new WaitForEndOfFrame();

        Texture2D finalPhoto = CaptureScreen(out string savedFileName);

        yield return StartCoroutine(FlashEffect());

        if (resultPreview != null && finalPhoto != null)
        {
            if (resultPreview.texture != null)
                Destroy(resultPreview.texture);
            resultPreview.texture = finalPhoto;
        }

        if (QRServerManager.Instance != null)
            QRServerManager.Instance.GenerateQRCodeForFile(savedFileName);

        foreach (GameObject ui in uiToHide)
            if (ui != null) ui.SetActive(true);

        isCapturing = false;
        _captureCoroutine = null;

        AppStateManager.Instance.ChangeState(AppState.Result);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 화면 캡처: 미리보기에 렌더링된 결과를 그대로 사용
    // 2x 오버샘플링으로 캡처 후 원본 해상도로 다운샘플 → 크로마키 경계 AA 확보
    // ─────────────────────────────────────────────────────────────────────
    private Texture2D CaptureScreen(out string fileName)
    {
        fileName = "";

        // WaitForEndOfFrame 이후 프레임버퍼를 직접 읽음 → 감마 변환 없이 화면 그대로
        Texture2D result = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        result.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        result.Apply();

        string folderPath = Path.Combine(Application.dataPath, saveFolderName);
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        fileName = "Photo_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
        string fullPath = Path.Combine(folderPath, fileName);
        File.WriteAllBytes(fullPath, result.EncodeToJPG(95));
        Debug.Log($"[PhotoCapture] 저장 완료 ({Screen.width}x{Screen.height}): {fullPath}");

        return result;
    }

    /// <summary>매 초마다 타이머 텍스트를 1.5배 → 1.0배로 빠르게 줄어들게 하는 펄스. 시각적 강조용.</summary>
    private IEnumerator TimerTickPulse()
    {
        if (timerText == null) yield break;
        var rt = timerText.rectTransform;
        float duration = 0.4f;
        float elapsed  = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / duration);
            // EaseOutCubic — 빠르게 줄어들었다가 안정
            float eased = 1f - Mathf.Pow(1f - k, 3f);
            float scale = Mathf.Lerp(1.5f, 1.0f, eased);
            rt.localScale = new Vector3(scale, scale, 1f);
            yield return null;
        }
        rt.localScale = Vector3.one;
    }

    private IEnumerator FlashEffect()
    {
        if (flashScreen == null) yield break;
        flashScreen.gameObject.SetActive(true);
        flashScreen.transform.SetAsLastSibling();
        flashScreen.color = new Color(1, 1, 1, 1);
        float duration = 0.5f;
        float elapsed  = 0;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = Mathf.Lerp(1, 0, elapsed / duration);
            flashScreen.color = new Color(1, 1, 1, alpha);
            yield return null;
        }
        flashScreen.color = new Color(1, 1, 1, 0);
        flashScreen.gameObject.SetActive(false);
    }
}
