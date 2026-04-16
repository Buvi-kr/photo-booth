// =============================================================================
//  OverlayBGManager.cs
//  포천아트밸리 천문과학관 무인 포토부스 — 배경 합성 오케스트레이터
//
//  배경 이미지 로드 방식:
//  ▪ StreamingAssets/ 폴더에서 bgName + 확장자로 직접 파일 로드
//  ▪ config.json 의 bgName 값과 파일명이 일치해야 함 (확장자 제외)
//  ▪ 새 배경 추가 = StreamingAssets에 이미지 복사 + config.json 수정만으로 완료 (재빌드 불필요)
// =============================================================================
using UnityEngine;
using UnityEngine.UI;
using System.IO;

public class OverlayBGManager : MonoBehaviour
{
    // ── 싱글턴 ────────────────────────────────────────────────────────────────
    public static OverlayBGManager Instance { get; private set; }

    // ── Inspector 연결 ────────────────────────────────────────────────────────
    [Header("배경 이미지 표시 레이어 (가장 뒤)")]
    public RawImage backgroundImageDisplay;

    [Header("크로마키 컨트롤러 (WebCamDisplay 의 ChromaKeyController)")]
    public ChromaKeyController chromaKeyController;

    [Header("배경 선택 UI 패널 (썸네일용, 선택사항)")]
    public GameObject[] bgThumbnailButtons;

    // ── 지원 확장자 (순서대로 검색) ───────────────────────────────────────────
    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png" };

    // ── 내부 상태 ─────────────────────────────────────────────────────────────
    private int _currentIndex = -1;
    private Texture2D _loadedTexture; // 메모리 누수 방지용

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
    }

    private void OnDestroy()
    {
        // 로드한 텍스처 메모리 해제
        if (_loadedTexture != null)
        {
            Destroy(_loadedTexture);
            _loadedTexture = null;
        }
    }

    // ── 공개 API (AppStateManager 에서 호출) ─────────────────────────────────

    /// <summary>
    /// 인덱스에 해당하는 배경을 로드하고 크로마키를 적용한다.
    /// AppStateManager.SelectBackgroundAndGoNext(index) 에서 호출.
    /// </summary>
    public void SetConfig(int index)
    {
        var loader = PhotoBoothConfigLoader.Instance;
        if (loader == null || !loader.IsLoaded)
        {
            Debug.LogError("[OverlayBGManager] PhotoBoothConfigLoader 가 준비되지 않았습니다!");
            return;
        }

        BackgroundConfig bgConfig = loader.Config.GetByIndex(index);
        if (bgConfig == null)
        {
            Debug.LogError("[OverlayBGManager] 인덱스 " + index + " 에 해당하는 배경이 없습니다." +
                           " config.json 의 backgrounds 항목을 확인하세요.");
            return;
        }

        _currentIndex = index;

        // ① 배경 이미지 로드 (StreamingAssets/bgName.jpg)
        LoadAndApplyBackground(bgConfig.BgName);

        // ② 크로마키 컨트롤러에 설정 전달 (Crop + Transform + Chroma + Color)
        if (chromaKeyController != null)
        {
            chromaKeyController.Show();
            chromaKeyController.ApplyConfig(bgConfig);
        }
        else
        {
            Debug.LogWarning("[OverlayBGManager] chromaKeyController 가 연결되지 않았습니다.");
        }

        Debug.Log("[OverlayBGManager] 배경 적용: [" + index + "] \"" + bgConfig.BgName + "\"");
    }

    /// <summary>
    /// 배경 오버레이를 숨긴다 (스탠바이 복귀 시 AppStateManager 에서 호출).
    /// </summary>
    public void HideOverlay()
    {
        if (backgroundImageDisplay != null)
            backgroundImageDisplay.gameObject.SetActive(false);

        if (chromaKeyController != null)
            chromaKeyController.Hide();

        _currentIndex = -1;
    }

    /// <summary>
    /// 배경 이미지만 숨기고 웹캠(크로마키)은 유지한다.
    /// (1단계 마스터 캘리브레이션 시 순수 카메라 피드 확인용)
    /// </summary>
    public void HideBackgroundOnly()
    {
        if (backgroundImageDisplay != null)
            backgroundImageDisplay.gameObject.SetActive(false);
            
        if (chromaKeyController != null)
            chromaKeyController.Show(); // 강제로 웹캠은 켜줌

        _currentIndex = -1;
    }

    public int GetCurrentIndex() => _currentIndex;

    public BackgroundConfig GetCurrentConfig()
    {
        var loader = PhotoBoothConfigLoader.Instance;
        return loader?.Config?.GetByIndex(_currentIndex);
    }

    // ── 내부 구현 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// StreamingAssets/{bgName}.jpg 에서 텍스처를 로드해 RawImage 에 적용.
    /// bgName에 확장자가 포함되어 있으면 그대로 사용하고,
    /// 없으면 .jpg → .jpeg → .png 순서로 탐색한다.
    /// </summary>
    private void LoadAndApplyBackground(string bgName)
    {
        if (backgroundImageDisplay == null) return;

        string filePath = FindBackgroundFile(bgName);

        if (string.IsNullOrEmpty(filePath))
        {
            Debug.LogWarning("[OverlayBGManager] StreamingAssets/" + bgName +
                             " 이미지를 찾을 수 없습니다. 파일명과 bgName 이 일치하는지 확인하세요.");
            backgroundImageDisplay.gameObject.SetActive(false);
            return;
        }

        try
        {
            byte[] fileData = File.ReadAllBytes(filePath);

            // 이전에 로드한 텍스처 메모리 해제
            if (_loadedTexture != null) Destroy(_loadedTexture);

            _loadedTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _loadedTexture.LoadImage(fileData);

            backgroundImageDisplay.texture = _loadedTexture;
            backgroundImageDisplay.gameObject.SetActive(true);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[OverlayBGManager] 이미지 로드 실패: " + ex.Message);
            backgroundImageDisplay.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// bgName으로 StreamingAssets 내 이미지 파일 경로를 찾는다.
    /// 확장자가 이미 포함되어 있으면 그대로, 없으면 여러 확장자를 시도한다.
    /// </summary>
    private string FindBackgroundFile(string bgName)
    {
        string baseName = Path.GetFileNameWithoutExtension(bgName);
        string existingExt = Path.GetExtension(bgName);

        // bgName에 확장자가 이미 포함된 경우 직접 시도
        if (!string.IsNullOrEmpty(existingExt))
        {
            string directPath = Path.Combine(Application.streamingAssetsPath, bgName);
            if (File.Exists(directPath)) return directPath;
        }

        // 확장자 없이 들어온 경우 지원 확장자 순서대로 탐색
        foreach (string ext in SupportedExtensions)
        {
            string tryPath = Path.Combine(Application.streamingAssetsPath, baseName + ext);
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
            Debug.LogWarning("[OverlayBGManager] 썸네일 버튼 수(" + btnCount +
                             ")와 config.json 배경 수(" + configCount + ")가 다릅니다!");
        }
    }
}
