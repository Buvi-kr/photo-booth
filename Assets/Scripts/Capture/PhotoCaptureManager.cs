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

    [Header("캡처할 영역 (투명 박스)")]
    public RectTransform captureArea;

    [Header("숨길 UI 모음")]
    public GameObject[] uiToHide;

    [Header("저장 설정")]
    public string saveFolderName = "MyPhotoBooth";

    [Header("재합성용 참조")]
    public RawImage webcamDisplay;

    private bool isCapturing = false;
    private Texture2D _lastRawFrame;

    private void Start()
    {
        if (timerText != null) timerText.text = "";
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space) && AppStateManager.Instance != null)
        {
            TakePhoto();
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

        for (int i = 3; i > 0; i--)
        {
            if (timerText != null) timerText.text = i.ToString();
            yield return new WaitForSeconds(1f);
        }

        if (timerText != null) timerText.text = "";

        foreach (GameObject ui in uiToHide)
            if (ui != null) ui.SetActive(false);

        yield return new WaitForEndOfFrame();

        // 배경 재합성용 원본 웹캠 프레임 저장
        if (webcamDisplay != null && webcamDisplay.texture is WebCamTexture camTex)
        {
            if (_lastRawFrame != null) Destroy(_lastRawFrame);
            _lastRawFrame = new Texture2D(camTex.width, camTex.height, TextureFormat.RGB24, false);
            _lastRawFrame.SetPixels(camTex.GetPixels());
            _lastRawFrame.Apply();
        }

        Texture2D finalPhoto = SaveScreenshot(out string savedFileName);

        yield return StartCoroutine(FlashEffect());

        if (resultPreview != null && finalPhoto != null)
            resultPreview.texture = finalPhoto;

        if (QRServerManager.Instance != null)
            QRServerManager.Instance.GenerateQRCodeForFile(savedFileName);

        foreach (GameObject ui in uiToHide)
            if (ui != null) ui.SetActive(true);

        isCapturing = false;

        AppStateManager.Instance.ChangeState(AppState.Result);
    }

    // 배경 변경 후 동일 포즈로 재합성 + QR 새로 생성
    public IEnumerator RecompositeCapture()
    {
        if (_lastRawFrame == null)
        {
            Debug.LogWarning("[PhotoCapture] 저장된 원본 프레임이 없습니다. 먼저 촬영하세요.");
            yield break;
        }

        Texture originalTexture = webcamDisplay != null ? webcamDisplay.texture : null;
        if (webcamDisplay != null) webcamDisplay.texture = _lastRawFrame;

        foreach (GameObject ui in uiToHide)
            if (ui != null) ui.SetActive(false);

        yield return new WaitForEndOfFrame();

        // 새 배경으로 저장 → 새 파일명 받기
        Texture2D recomposited = SaveScreenshot(out string newFileName);

        if (webcamDisplay != null) webcamDisplay.texture = originalTexture;

        foreach (GameObject ui in uiToHide)
            if (ui != null) ui.SetActive(true);

        if (resultPreview != null && recomposited != null)
            resultPreview.texture = recomposited;

        // 새 파일명으로 QR 재생성
        if (QRServerManager.Instance != null && !string.IsNullOrEmpty(newFileName))
            QRServerManager.Instance.GenerateQRCodeForFile(newFileName);

        Debug.Log($"[PhotoCapture] 배경 재합성 완료! 파일: {newFileName}");
    }

    // PNG → JPG(품질 90)로 변경
    private Texture2D SaveScreenshot(out string fileName)
    {
        fileName = "";

        if (captureArea == null)
        {
            Debug.LogError("⚠️ 캡처 영역이 연결되지 않았습니다!");
            return null;
        }

        Canvas canvas = captureArea.GetComponentInParent<Canvas>();
        RectTransform canvasRect = canvas.GetComponent<RectTransform>();

        Vector3[] captureCorners = new Vector3[4];
        captureArea.GetWorldCorners(captureCorners);

        Vector3[] canvasCorners = new Vector3[4];
        canvasRect.GetWorldCorners(canvasCorners);

        float canvasWidth = canvasCorners[2].x - canvasCorners[0].x;
        float canvasHeight = canvasCorners[2].y - canvasCorners[0].y;

        float xRatio = (captureCorners[0].x - canvasCorners[0].x) / canvasWidth;
        float yRatio = (captureCorners[0].y - canvasCorners[0].y) / canvasHeight;
        float wRatio = (captureCorners[2].x - captureCorners[0].x) / canvasWidth;
        float hRatio = (captureCorners[2].y - captureCorners[0].y) / canvasHeight;

        int startX = Mathf.RoundToInt(Screen.width * xRatio);
        int startY = Mathf.RoundToInt(Screen.height * yRatio);
        int realWidth = Mathf.RoundToInt(Screen.width * wRatio);
        int realHeight = Mathf.RoundToInt(Screen.height * hRatio);

        if (realWidth <= 0 || realHeight <= 0) return null;

        Texture2D tex = new Texture2D(realWidth, realHeight, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(startX, startY, realWidth, realHeight), 0, 0);
        tex.Apply();

        string folderPath = Path.Combine(Application.dataPath, saveFolderName);
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

        // JPG로 저장 (품질 90 — PNG 대비 파일 크기 약 1/5)
        fileName = "Photo_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
        string fullPath = Path.Combine(folderPath, fileName);

        byte[] bytes = tex.EncodeToJPG(90);
        File.WriteAllBytes(fullPath, bytes);

        Debug.Log($"[PhotoCapture] 저장 완료: {fullPath}");
        return tex;
    }

    private IEnumerator FlashEffect()
    {
        if (flashScreen == null) yield break;
        flashScreen.color = new Color(1, 1, 1, 1);
        float duration = 0.5f;
        float elapsed = 0;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = Mathf.Lerp(1, 0, elapsed / duration);
            flashScreen.color = new Color(1, 1, 1, alpha);
            yield return null;
        }
        flashScreen.color = new Color(1, 1, 1, 0);
    }
}