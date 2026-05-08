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

    [Header("저장 설정")]
    public string saveFolderName = "MyPhotoBooth";

    [Header("자동 정리 설정 (무인 키오스크 장기 운영용)")]
    [Tooltip("이 일수보다 오래된 사진을 자동 삭제. 0=비활성")]
    [Range(0, 365)] public int autoCleanupAfterDays = 30;
    [Tooltip("아무리 오래돼도 최근 N장은 무조건 보관 (QR 스캔 늦게 한 사용자 보호)")]
    [Range(0, 1000)] public int minKeepCount = 50;
    [Tooltip("앱 시작 시 자동 정리 코루틴 실행 여부")]
    public bool runCleanupOnStart = true;

    private bool isCapturing = false;
    private Coroutine _captureCoroutine;

    private void Start()
    {
        if (timerText != null)
        {
            timerText.text = "";
            ConfigureTimerVisual();
        }

        // Inspector 명시 목록만 비활성화 (자동 탐색은 화면 레이아웃 충돌 위험으로 제거됨)
        if (uiToHidePermanently != null)
        {
            foreach (var go in uiToHidePermanently)
                if (go != null) go.SetActive(false);
        }

        // 무인 키오스크 장기 운영 → 오래된 사진 자동 정리 (디스크 보호)
        if (runCleanupOnStart && autoCleanupAfterDays > 0)
        {
            StartCoroutine(AutoCleanupRoutine());
        }
    }

    /// <summary>
    /// 저장 폴더의 오래된 사진을 자동 삭제하는 코루틴.
    /// 이중 안전장치:
    ///   ① autoCleanupAfterDays 일수 초과한 파일만 대상
    ///   ② 가장 최근 minKeepCount 장은 무조건 보호 (QR 늦게 스캔한 사용자 보호)
    /// 매 20개 파일마다 yield로 프레임 양보 → 시작 시 끊김 방지.
    /// </summary>
    private IEnumerator AutoCleanupRoutine()
    {
        // 시작 직후 다른 초기화에 양보
        yield return null;
        yield return null;

        string folderPath = Path.Combine(Application.dataPath, saveFolderName);
        if (!Directory.Exists(folderPath))
        {
            Debug.Log("[PhotoCapture/Cleanup] 저장 폴더 없음 → 정리 건너뜀.");
            yield break;
        }

        DirectoryInfo dir = null;
        FileInfo[] files = null;
        try
        {
            dir   = new DirectoryInfo(folderPath);
            files = dir.GetFiles("Photo_*.jpg");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[PhotoCapture/Cleanup] 폴더 읽기 실패: {e.Message}");
            yield break;
        }

        if (files == null || files.Length == 0) yield break;
        if (files.Length <= minKeepCount)
        {
            Debug.Log($"[PhotoCapture/Cleanup] 파일 {files.Length}장 ≤ 최소 보관 {minKeepCount}장 → 정리 건너뜀.");
            yield break;
        }

        // 최신순 정렬 (앞쪽 = 최신, 뒤쪽 = 오래됨)
        System.Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

        System.DateTime cutoff = System.DateTime.Now.AddDays(-autoCleanupAfterDays);
        int deleted   = 0;
        int processed = 0;

        // 최근 minKeepCount 장은 건너뛰고, 그 뒤부터 cutoff 검사
        for (int i = minKeepCount; i < files.Length; i++)
        {
            if (files[i].LastWriteTime < cutoff)
            {
                try
                {
                    files[i].Delete();
                    deleted++;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[PhotoCapture/Cleanup] 삭제 실패 ({files[i].Name}): {e.Message}");
                }
            }

            // 매 20개마다 프레임 양보 (대량 정리 시 끊김 방지)
            processed++;
            if (processed % 20 == 0) yield return null;
        }

        if (deleted > 0)
            Debug.Log($"[PhotoCapture/Cleanup] ✅ {deleted}장 삭제 완료 " +
                      $"(보관 기간 {autoCleanupAfterDays}일, 최소 유지 {minKeepCount}장, 전체 {files.Length}장 중)");
        else
            Debug.Log($"[PhotoCapture/Cleanup] 정리 대상 없음 (전체 {files.Length}장, 모두 {autoCleanupAfterDays}일 이내).");
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

            // Capture 진입 시 잔여 입력 차단 쿨다운 (SelectBG에서 누른 Enter/Space가
            // 그대로 Capture에 새는 것 방지. SelectBG→Capture 전환은 0.8초 후이지만
            // 사용자가 키를 길게 누르거나 빠르게 두 번 눌렀을 때를 대비해 1.0초 보장.)
            if (_lastState == AppState.Capture)
            {
                _captureCooldown = 1.0f;
            }

            // Capture/Processing → 그 외 상태로 빠져나갈 때(ESC, 관리자모드 등) 진행 중 촬영 강제 취소
            bool wasCaptureFlow = (prev == AppState.Capture || prev == AppState.Processing);
            bool nowOutOfFlow   = (_lastState != AppState.Capture && _lastState != AppState.Processing);
            if (wasCaptureFlow && nowOutOfFlow)
            {
                CancelCapture();
            }
        }

        if (_captureCooldown > 0f) _captureCooldown -= Time.deltaTime;

        // Enter / Space 키로 촬영 시작 (수동 트리거)
        bool pressedTrigger = Input.GetKeyDown(KeyCode.Return)
                            || Input.GetKeyDown(KeyCode.KeypadEnter)
                            || Input.GetKeyDown(KeyCode.Space);
        if (pressedTrigger && appState.CurrentState == AppState.Capture)
        {
            if (_captureCooldown <= 0f && !isCapturing)
            {
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
    }

    private IEnumerator CaptureRoutine()
    {
        isCapturing = true;

        AppStateManager.Instance.ChangeState(AppState.Processing);

        // 촬영 시작 즉시 UI 숨김 (촬영하기 버튼/하단 패널 등)
        // → 카운트다운 중 깨끗한 미리보기 + 촬영 시 어차피 숨겨야 할 UI
        if (uiToHide != null)
        {
            foreach (GameObject ui in uiToHide)
                if (ui != null) ui.SetActive(false);
        }

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

        // Result 진입 직전 UI 복원 (촬영하기 버튼/하단 패널 등 다시 표시)
        if (uiToHide != null)
        {
            foreach (GameObject ui in uiToHide)
                if (ui != null) ui.SetActive(true);
        }

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
