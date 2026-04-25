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

    [Header("숨길 UI 모음")]
    public GameObject[] uiToHide;

    [Header("저장 설정")]
    public string saveFolderName = "MyPhotoBooth";

    private bool isCapturing = false;

    private void Start()
    {
        if (timerText != null) timerText.text = "";
    }

    private AppState _lastState = AppState.Standby;
    private float _captureCooldown = 0f;

    private void Update()
    {
        var appState = AppStateManager.Instance;
        if (appState == null) return;

        if (_lastState != appState.CurrentState)
        {
            _lastState = appState.CurrentState;
            if (_lastState == AppState.Capture)
            {
                _captureCooldown = 0.5f;
            }
        }

        if (_captureCooldown > 0f) _captureCooldown -= Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.Return) && appState.CurrentState == AppState.Capture)
        {
            if (_captureCooldown <= 0f)
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

        StartCoroutine(CaptureRoutine());
    }

    private IEnumerator CaptureRoutine()
    {
        isCapturing = true;

        AppStateManager.Instance.ChangeState(AppState.Processing);

        for (int i = 5; i > 0; i--)
        {
            if (timerText != null) timerText.text = i.ToString();
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
