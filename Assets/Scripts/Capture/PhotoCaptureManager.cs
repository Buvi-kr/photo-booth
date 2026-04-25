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

    [Header("캡처할 영역 (투명 박스) - 폴백용")]
    public RectTransform captureArea;

    [Header("숨길 UI 모음")]
    public GameObject[] uiToHide;

    [Header("저장 설정")]
    public string saveFolderName = "MyPhotoBooth";

    [Header("재합성용 참조")]
    public RawImage webcamDisplay;

    [Header("고품질 캡처 해상도")]
    public int captureWidth  = 1920;
    public int captureHeight = 1080;

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
                _captureCooldown = 0.5f; // 상태 진입 시 0.5초 대기 (이전 엔터 입력 무시)
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

        yield return new WaitForEndOfFrame();

        Texture2D finalPhoto = HighQualityCapture(out string savedFileName);

        yield return StartCoroutine(FlashEffect());

        if (resultPreview != null && finalPhoto != null)
        {
            // [Kiosk Optimization] 기존 텍스처 메모리 수동 해제 (OOM 방지)
            if (resultPreview.texture != null)
            {
                Destroy(resultPreview.texture);
            }
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
    // 고품질 캡처: Graphics.Blit 로 배경 → 크로마키 → 전경을 GPU에서 합성
    // 화면 스크린샷 대신 셰이더 결과를 직접 RenderTexture 에 뽑아냄
    // ─────────────────────────────────────────────────────────────────────
    private Texture2D HighQualityCapture(out string fileName)
    {
        fileName = "";

        int w = captureWidth;
        int h = captureHeight;

        // ── 참조 수집 ────────────────────────────────────────────────────
        var chromaCtrl = FindObjectOfType<ChromaKeyController>();
        var bgMgr      = OverlayBGManager.Instance;

        WebCamTexture webcamTex = chromaCtrl != null ? chromaCtrl.WebcamTexture : null;
        Material      chromaMat = chromaCtrl != null ? chromaCtrl.ChromaMaterial : null;
        Texture       bgTex     = bgMgr != null ? bgMgr.GetBackgroundTexture() : null;
        Texture       fgTex     = bgMgr != null ? bgMgr.GetForegroundTexture() : null;

        // 웹캠이나 머티리얼을 못 가져오면 기존 스크린샷으로 폴백
        if (webcamTex == null || !webcamTex.isPlaying || chromaMat == null)
        {
            Debug.LogWarning("[PhotoCapture] 고품질 캡처 불가 — 스크린샷으로 폴백");
            return FallbackScreenshot(out fileName);
        }

        // ── 캡처 전용 머티리얼 복제 ──────────────────────────────────────
        // UI 클립키워드 제거 + RectTransform의 Zoom/Move/Rotation을 UV로 인코딩
        Material captureMat = new Material(chromaMat);
        captureMat.DisableKeyword("UNITY_UI_CLIP_RECT");
        captureMat.DisableKeyword("UNITY_UI_ALPHACLIP");
        captureMat.SetFloat("_CaptureRotation", 0f); // 기본값 초기화

        // ── UI Transform → UV Transform 변환 ────────────────────────────
        // localScale(s), anchoredPosition(px), localEulerAngles.z(deg) → mainTexture ST
        RectTransform rt = chromaCtrl.WebcamRectTransform;
        if (rt != null)
        {
            float s     = Mathf.Max(rt.localScale.x, 0.001f);
            float mx    = rt.anchoredPosition.x;          // 픽셀 단위 이동
            float my    = rt.anchoredPosition.y;
            float angle = rt.localEulerAngles.z;          // 0~360

            // 컨테이너(부모) 크기
            float cW = rt.parent != null
                ? rt.parent.GetComponent<RectTransform>()?.rect.width  ?? w
                : w;
            float cH = rt.parent != null
                ? rt.parent.GetComponent<RectTransform>()?.rect.height ?? h
                : h;

            // Zoom → UV scale 역수: 2x 줌이면 UV 범위 0.5 (중심 50%만 샘플)
            float uvScaleX = 1f / s;
            float uvScaleY = 1f / s;

            // Move → UV 오프셋: anchoredPosition을 컨테이너 크기 기준으로 정규화
            // 오른쪽 이동 = 텍스처 왼쪽 당김 → UV offset 감산
            float uvOffX = 0.5f * (1f - uvScaleX) - mx / (cW * s);
            float uvOffY = 0.5f * (1f - uvScaleY) - my / (cH * s);

            // Graphics.Blit은 _MainTex_ST를 강제로 덮어쓰므로 커스텀 변수(_CaptureST)를 사용
            captureMat.SetVector("_CaptureST", new Vector4(uvScaleX, uvScaleY, uvOffX, uvOffY));

            // Rotation → 셰이더 _CaptureRotation (라디안, 반시계 = UI와 같은 방향)
            if (Mathf.Abs(angle) > 0.01f)
            {
                // Unity UI는 Z 오일러 반시계가 양수 → 셰이더도 동일 부호
                float rad = -angle * Mathf.Deg2Rad;
                captureMat.SetFloat("_CaptureRotation", rad);
            }

            // ── Crop → _CropRect / _CropFade (UV 정규화) ─────────────────
            // RectMask2D.padding (L,B,R,T) = canvas 픽셀
            // → UV 정규화: leftUV = L/cW, bottomUV = B/cH, rightEnd = 1-R/cW, topEnd = 1-T/cH
            // RectMask2D.softness (px) → UV 페이드 폭
            Vector4 pad  = chromaCtrl.CropPadding;
            Vector2 fade = chromaCtrl.CropFade;

            float leftUV   =       pad.x / Mathf.Max(cW, 1f);
            float bottomUV =       pad.y / Mathf.Max(cH, 1f);
            float rightUV  = 1f - (pad.z / Mathf.Max(cW, 1f));
            float topUV    = 1f - (pad.w / Mathf.Max(cH, 1f));
            float fadeXUV  = fade.x / Mathf.Max(cW, 1f);
            float fadeYUV  = fade.y / Mathf.Max(cH, 1f);

            captureMat.SetVector("_CropRect", new Vector4(leftUV, bottomUV, rightUV, topUV));
            captureMat.SetVector("_CropFade", new Vector4(
                Mathf.Max(fadeXUV, 0.001f), Mathf.Max(fadeYUV, 0.001f), 0f, 0f));
        }

        // ── RenderTexture 생성 ───────────────────────────────────────────
        // RT_A: 배경 (RGB)
        // RT_B: 크로마키 결과 (RGBA — 인물 알파 포함)
        // RT_C: 최종 합성 (RGB)
        RenderTexture rtBg     = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture rtChroma = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture rtFinal  = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

        rtBg.filterMode     = FilterMode.Bilinear;
        rtChroma.filterMode = FilterMode.Bilinear;
        rtFinal.filterMode  = FilterMode.Bilinear;

        // ── Pass 1: 배경을 RT 에 그리기 ─────────────────────────────────
        if (bgTex != null)
            Graphics.Blit(bgTex, rtBg);
        else
            Graphics.Blit(Texture2D.blackTexture, rtBg);

        // ── Pass 2: 웹캠 → ChromaKey 셰이더 → rtChroma (RGBA, 2x SSAA) ──
        // 2x 초과샘플링으로 렌더링 후 1080p로 다운샘플 → 경계선 AA 향상
        // captureMat: UI 클립키워드 비활성화된 복제본 사용
        RenderTexture rtChromaSSAA = RenderTexture.GetTemporary(w * 2, h * 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rtChromaSSAA.filterMode = FilterMode.Bilinear;
        Graphics.Blit(webcamTex, rtChromaSSAA, captureMat);   // 2x 해상도로 크로마키 연산
        Graphics.Blit(rtChromaSSAA, rtChroma);                 // 1x로 다운샘플 (bilinear AA)
        RenderTexture.ReleaseTemporary(rtChromaSSAA);

        // 캡처 전용 머티리얼 즉시 해제
        Object.Destroy(captureMat);

        // ── Pass 3: 배경 위에 크로마 결과 Alpha-Blend 합성 ─────────────
        Graphics.Blit(rtBg, rtFinal);
        Graphics.Blit(rtChroma, rtFinal, GetAlphaBlendMat());

        // ── Pass 4: 전경(프레임) 합성 ────────────────────────────────────
        if (fgTex != null)
            Graphics.Blit(fgTex, rtFinal, GetAlphaBlendMat());

        // ── RT → Texture2D 변환 (크롭 적용) ─────────────────────────────
        // RectMask2D.padding(L,B,R,T)을 픽셀로 변환하여 ReadPixels 영역 결정
        // ── RT → Texture2D 변환 (항상 원본 해상도 1920×1080) ────────────
        // 크롭은 셰이더 _CropRect 알파 마스크로 처리하므로 ReadPixels는 전체 영역 고정
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = rtFinal;
        Texture2D result = new Texture2D(w, h, TextureFormat.RGB24, false);
        result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        result.Apply();
        RenderTexture.active = prevActive;

        // ── 저장 ─────────────────────────────────────────────────────────
        string folderPath = Path.Combine(Application.dataPath, saveFolderName);
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        fileName = "Photo_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
        string fullPath = Path.Combine(folderPath, fileName);
        File.WriteAllBytes(fullPath, result.EncodeToJPG(95));
        Debug.Log($"[PhotoCapture] 고품질 저장 완료 ({w}x{h} JPG 95%): {fullPath}");

        // ── 정리 ─────────────────────────────────────────────────────────
        RenderTexture.ReleaseTemporary(rtBg);
        RenderTexture.ReleaseTemporary(rtChroma);
        RenderTexture.ReleaseTemporary(rtFinal);

        return result;
    }

    // Alpha-Blend 전용 재사용 머티리얼
    private static Material _alphaBlendMat;
    private static Material GetAlphaBlendMat()
    {
        if (_alphaBlendMat == null)
        {
            _alphaBlendMat = new Material(Shader.Find("UI/Default"));
            _alphaBlendMat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _alphaBlendMat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _alphaBlendMat.SetInt("_ZWrite",    0);
            _alphaBlendMat.DisableKeyword("_ALPHATEST_ON");
            _alphaBlendMat.EnableKeyword("_ALPHABLEND_ON");
        }
        return _alphaBlendMat;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 폴백: 기존 스크린샷 방식 (고품질 캡처 실패 시)
    // ─────────────────────────────────────────────────────────────────────
    private Texture2D FallbackScreenshot(out string fileName)
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

        float canvasWidth  = canvasCorners[2].x - canvasCorners[0].x;
        float canvasHeight = canvasCorners[2].y - canvasCorners[0].y;

        float xRatio = (captureCorners[0].x - canvasCorners[0].x) / canvasWidth;
        float yRatio = (captureCorners[0].y - canvasCorners[0].y) / canvasHeight;
        float wRatio = (captureCorners[2].x - captureCorners[0].x) / canvasWidth;
        float hRatio = (captureCorners[2].y - captureCorners[0].y) / canvasHeight;

        int startX    = Mathf.RoundToInt(Screen.width  * xRatio);
        int startY    = Mathf.RoundToInt(Screen.height * yRatio);
        int realWidth = Mathf.RoundToInt(Screen.width  * wRatio);
        int realHeight = Mathf.RoundToInt(Screen.height * hRatio);

        if (realWidth <= 0 || realHeight <= 0) return null;

        Texture2D tex = new Texture2D(realWidth, realHeight, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(startX, startY, realWidth, realHeight), 0, 0);
        tex.Apply();

        string folderPath = Path.Combine(Application.dataPath, saveFolderName);
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        fileName = "Photo_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
        string fullPath = Path.Combine(folderPath, fileName);
        File.WriteAllBytes(fullPath, tex.EncodeToJPG(95));
        Debug.Log($"[PhotoCapture] 폴백 저장 완료: {fullPath}");
        return tex;
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