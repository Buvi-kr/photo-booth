// =============================================================================
//  OverlayBGManager.cs
//  포천아트밸리 천문과학관 무인 포토부스 — 배경 합성 오케스트레이터
//
//  배경 이미지 로드 방식:
//  ▪ StreamingAssets/ 폴더에서 bgName + 확장자로 직접 파일 로드 (배경)
//  ▪ bgName + "_front" 로드 (전경 프레임)
//  ▪ bgName + "_thumbnail" 로드 (썸네일 버튼용)
// =============================================================================
using UnityEngine;
using UnityEngine.UI;
using System.IO;

public class OverlayBGManager : MonoBehaviour
{
    // ── 싱글턴 ────────────────────────────────────────────────────────────────
    public static OverlayBGManager Instance { get; private set; }

    // ── Inspector 연결 ────────────────────────────────────────────────────────
    [Header("합성 표시 레이어")]
    [Tooltip("배경 이미지 표시 레이어 (가장 뒤)")]
    public RawImage backgroundImageDisplay;
    [Tooltip("전경 이미지 표시 레이어 (웹캠 위)")]
    public RawImage foregroundImageDisplay;

    [Header("크로마키 컨트롤러 (WebCamDisplay 의 ChromaKeyController)")]
    public ChromaKeyController chromaKeyController;

    [Header("배경 선택 UI 패널 (썸네일용)")]
    public Button[] bgThumbnailButtons;

    // ── 지원 확장자 (순서대로 검색) ───────────────────────────────────────────
    private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg" };

    // ── 내부 상태 ─────────────────────────────────────────────────────────────
    private int _currentIndex = -1;
    private Texture2D _loadedTexture;      // 배경 텍스처
    private Texture2D _loadedFrontTexture; // 전경 텍스처
    private Texture2D[] _thumbTextures;    // 썸네일 텍스처 배열

    // ── 라이프사이클 ──────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    private void Start()
    {
        ValidateConfigAlignment();
        HideOverlay();
        
        // Config가 로드될 때 썸네일 세팅하도록 구독
        if (PhotoBoothConfigLoader.Instance != null)
        {
            PhotoBoothConfigLoader.Instance.OnConfigReloaded += (config) => SetupThumbnails();
            if (PhotoBoothConfigLoader.Instance.IsLoaded) SetupThumbnails();
        }
    }

    private void OnDestroy()
    {
        if (PhotoBoothConfigLoader.Instance != null)
            PhotoBoothConfigLoader.Instance.OnConfigReloaded -= (config) => SetupThumbnails();

        if (_loadedTexture != null) { Destroy(_loadedTexture); _loadedTexture = null; }
        if (_loadedFrontTexture != null) { Destroy(_loadedFrontTexture); _loadedFrontTexture = null; }

        if (_thumbTextures != null)
        {
            foreach (var t in _thumbTextures)
                if (t != null) Destroy(t);
            _thumbTextures = null;
        }
    }

    // ── 공개 API (AppStateManager 에서 호출) ─────────────────────────────────

    public void SetConfig(int index)
    {
        var loader = PhotoBoothConfigLoader.Instance;
        if (loader == null || !loader.IsLoaded) return;

        BackgroundConfig bgConfig = loader.Config.GetByIndex(index);
        if (bgConfig == null) return;

        _currentIndex = index;

        // 배경 & 전경 로드
        LoadAndApplyBackground(bgConfig.BgName);

        // 크로마키 세팅
        if (chromaKeyController != null)
        {
            chromaKeyController.Show();
            chromaKeyController.ApplyConfig(bgConfig);
        }
    }

    public void HideOverlay()
    {
        if (backgroundImageDisplay != null) backgroundImageDisplay.gameObject.SetActive(false);
        if (foregroundImageDisplay != null) foregroundImageDisplay.gameObject.SetActive(false);
        if (chromaKeyController != null) chromaKeyController.Hide();
        _currentIndex = -1;
    }

    public void HideBackgroundOnly()
    {
        if (backgroundImageDisplay != null) backgroundImageDisplay.gameObject.SetActive(false);
        if (foregroundImageDisplay != null) foregroundImageDisplay.gameObject.SetActive(false);
        if (chromaKeyController != null) chromaKeyController.Show();
        _currentIndex = -1;
    }

    public int GetCurrentIndex() => _currentIndex;

    public BackgroundConfig GetCurrentConfig()
    {
        var loader = PhotoBoothConfigLoader.Instance;
        return loader?.Config?.GetByIndex(_currentIndex);
    }

    // ── 내부 구현 ─────────────────────────────────────────────────────────────

    private void SetupThumbnails()
    {
        var loader = PhotoBoothConfigLoader.Instance;
        if (loader == null || !loader.IsLoaded) return;

        int count = loader.Config.Backgrounds?.Count ?? 0;
        if (bgThumbnailButtons == null || count == 0) return;

        if (_thumbTextures != null)
        {
            foreach (var t in _thumbTextures) if (t != null) Destroy(t);
        }
        int btnCount = bgThumbnailButtons.Length;
        _thumbTextures = new Texture2D[btnCount];

        for (int i = 0; i < btnCount; i++)
        {
            if (i >= count) break;
            
            string bgName = loader.Config.Backgrounds[i].BgName;
            
            // 1순위: _thumbnail 파일, 2순위: 원본 파일
            string thumbPath = FindBackgroundFile(bgName + "_thumbnail");
            if (string.IsNullOrEmpty(thumbPath)) thumbPath = FindBackgroundFile(bgName);

            if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
            {
                byte[] fileData = File.ReadAllBytes(thumbPath);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                tex.LoadImage(fileData);
                _thumbTextures[i] = tex;

                // 버튼 이미지 설정
                Image btnImg = bgThumbnailButtons[i].GetComponent<Image>();
                if (btnImg != null)
                {
                    btnImg.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
            }
        }
        Debug.Log($"[OverlayBGManager] 썸네일 {count}개 로드 완료.");
    }

    private void LoadAndApplyBackground(string bgName)
    {
        // 1. 뒤쪽 배경 로드
        if (backgroundImageDisplay != null)
        {
            string bgPath = FindBackgroundFile(bgName);
            if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
            {
                byte[] bgData = File.ReadAllBytes(bgPath);
                if (_loadedTexture != null) Destroy(_loadedTexture);
                _loadedTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                _loadedTexture.LoadImage(bgData);

                backgroundImageDisplay.texture = _loadedTexture;
                backgroundImageDisplay.gameObject.SetActive(true);
            }
            else
            {
                backgroundImageDisplay.gameObject.SetActive(false);
            }
        }

        // 2. 앞쪽 전경(프레임) 로드
        if (foregroundImageDisplay != null)
        {
            string fgPath = FindBackgroundFile(bgName + "_front");
            if (!string.IsNullOrEmpty(fgPath) && File.Exists(fgPath))
            {
                byte[] fgData = File.ReadAllBytes(fgPath);
                if (_loadedFrontTexture != null) Destroy(_loadedFrontTexture);
                // 투명도가 지원되어야 하므로 RGBA32
                _loadedFrontTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                _loadedFrontTexture.LoadImage(fgData);

                foregroundImageDisplay.texture = _loadedFrontTexture;
                foregroundImageDisplay.gameObject.SetActive(true);
            }
            else
            {
                foregroundImageDisplay.gameObject.SetActive(false);
            }
        }
    }

    private string FindBackgroundFile(string baseName)
    {
        string existingExt = Path.GetExtension(baseName);

        if (!string.IsNullOrEmpty(existingExt))
        {
            string directPath = Path.Combine(Application.streamingAssetsPath, baseName);
            if (File.Exists(directPath)) return directPath;
        }

        string nameNoExt = Path.GetFileNameWithoutExtension(baseName);
        foreach (string ext in SupportedExtensions)
        {
            string tryPath = Path.Combine(Application.streamingAssetsPath, nameNoExt + ext);
            if (File.Exists(tryPath)) return tryPath;
        }

        return null;
    }

    private void ValidateConfigAlignment()
    {
        var loader = PhotoBoothConfigLoader.Instance;
        if (loader == null || !loader.IsLoaded) return;

        int configCount = loader.Config.Backgrounds?.Count ?? 0;
        int btnCount = bgThumbnailButtons?.Length ?? 0;

        if (btnCount > 0 && btnCount != configCount)
        {
            Debug.LogWarning($"[OverlayBGManager] 썸네일 버튼 수({btnCount})와 config 배경 수({configCount})가 다릅니다!");
        }
    }
}
