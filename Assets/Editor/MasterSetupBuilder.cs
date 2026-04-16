#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using System.IO;

/// <summary>
/// 포토부스 올인원 에디터 자동화 스크립트.
/// 메뉴 버튼 하나로 모든 인스펙터 슬롯, 비디오 플레이어, 관리자 UI,
/// 배경 선택 버튼을 자동으로 검색/생성/연결한다.
/// </summary>
public class MasterSetupBuilder
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  👑 올인원: 전체 시스템 자동 세팅
    // ═══════════════════════════════════════════════════════════════════════════

    [MenuItem("PhotoBooth/👑 올인원: 전체 시스템 자동 세팅")]
    public static void RunFullSetup()
    {
        AppStateManager appState = Object.FindObjectOfType<AppStateManager>();
        if (appState == null)
        {
            Debug.LogError("❌ 씬에 AppStateManager가 없습니다! 오브젝트에 먼저 붙여주세요.");
            return;
        }

        Undo.RecordObject(appState, "MasterSetup Full");

        int fixCount = 0;

        // ── 1단계: 비디오 파일명 교정 ──
        fixCount += FixVideoFileNames(appState);

        // ── 2단계: ChromaKeyController 자동 생성/연결 ──
        fixCount += EnsureChromaKeyController(appState);

        // ── 3단계: OverlayBGManager 자동 연결 ──
        fixCount += EnsureOverlayBGManager(appState);

        // ── 4단계: 관리자 패널 UI 완전 세팅 ──
        fixCount += SetupAdminPanel(appState);

        // ── 5단계: 배경 선택 패널 버튼 연결 ──
        fixCount += SetupSelectBGPanel(appState);

        // ── 6단계: 건강 체크 (Validation) ──
        RunValidation(appState);

        EditorUtility.SetDirty(appState);
        Debug.Log($"\n👑 [MasterSetup] 올인원 세팅 완료! 총 {fixCount}개 항목이 자동 처리되었습니다.");
        Debug.Log("💾 반드시 Ctrl+S 로 씬을 저장해주세요!\n");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  2단계: ChromaKeyController 자동 생성/연결
    //  panelCapture 안의 RawImage를 찾아 붙이거나, 없으면 새로 만든다.
    // ═══════════════════════════════════════════════════════════════════════════

    private static int EnsureChromaKeyController(AppStateManager appState)
    {
        ChromaKeyController existing = Object.FindObjectOfType<ChromaKeyController>();
        if (existing != null)
        {
            Debug.Log($"✅ [ChromaKey] 기존 ChromaKeyController 발견: '{existing.gameObject.name}'");
            return 0;
        }

        // panelCapture 하위에서 "WebCam", "Preview", "Camera" 등 이름이 있는 RawImage 탐색
        RawImage targetRawImage = null;

        if (appState.panelCapture != null)
        {
            RawImage[] rawImages = appState.panelCapture.GetComponentsInChildren<RawImage>(true);
            foreach (var ri in rawImages)
            {
                string n = ri.name.ToLower();
                if (n.Contains("webcam") || n.Contains("camera") || n.Contains("preview") || n.Contains("chroma"))
                {
                    targetRawImage = ri;
                    break;
                }
            }
            // 이름 매칭 실패 시 차선: panelCapture 안의 첫 번째 RawImage
            if (targetRawImage == null && rawImages.Length > 0)
                targetRawImage = rawImages[0];
        }

        // panelCapture에도 없으면 씬 전체에서 "WebCamDisplay" 이름 검색
        if (targetRawImage == null)
        {
            GameObject wcObj = GameObject.Find("WebCamDisplay");
            if (wcObj != null)
                targetRawImage = wcObj.GetComponent<RawImage>();
        }

        // 그래도 없으면 새로 생성 (Canvas 하위에)
        if (targetRawImage == null)
        {
            Canvas canvas = Object.FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                Debug.LogError("❌ 씬에 Canvas가 없어서 ChromaKeyController를 생성할 수 없습니다!");
                return 0;
            }

            GameObject chromaObj = new GameObject("WebCamDisplay");
            chromaObj.transform.SetParent(canvas.transform, false);

            targetRawImage = chromaObj.AddComponent<RawImage>();
            RectTransform rt = chromaObj.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;

            // panelCapture가 있으면 그 아래로 이동
            if (appState.panelCapture != null)
            {
                chromaObj.transform.SetParent(appState.panelCapture.transform, false);
                chromaObj.transform.SetAsFirstSibling();
            }

            Debug.Log("✅ [ChromaKey] WebCamDisplay 오브젝트를 새로 생성했습니다.");
        }

        // ChromaKeyController 컴포넌트 추가
        Undo.RecordObject(targetRawImage.gameObject, "Add ChromaKeyController");
        ChromaKeyController ckc = targetRawImage.gameObject.AddComponent<ChromaKeyController>();

        Debug.Log($"✅ [ChromaKey] ChromaKeyController를 '{targetRawImage.gameObject.name}'에 추가했습니다.");
        return 1;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3단계: OverlayBGManager 자동 연결
    //  chromaKeyController 슬롯이 비어있으면 자동으로 찾아 연결한다.
    // ═══════════════════════════════════════════════════════════════════════════

    private static int EnsureOverlayBGManager(AppStateManager appState)
    {
        OverlayBGManager overlayMgr = Object.FindObjectOfType<OverlayBGManager>();
        if (overlayMgr == null)
        {
            Debug.LogWarning("⚠️ [Overlay] 씬에 OverlayBGManager가 없습니다. 연결을 건너뜁니다.");
            return 0;
        }

        int count = 0;
        Undo.RecordObject(overlayMgr, "Wire OverlayBGManager");

        // chromaKeyController 슬롯 자동 연결
        if (overlayMgr.chromaKeyController == null)
        {
            ChromaKeyController ckc = Object.FindObjectOfType<ChromaKeyController>();
            if (ckc != null)
            {
                overlayMgr.chromaKeyController = ckc;
                Debug.Log($"✅ [Overlay] chromaKeyController → '{ckc.gameObject.name}' 자동 연결");
                count++;
            }
        }

        // backgroundImageDisplay 슬롯 자동 연결
        if (overlayMgr.backgroundImageDisplay == null)
        {
            // "Background", "BG", "Overlay" 이름이 있는 RawImage 검색
            RawImage[] allRaw = Object.FindObjectsOfType<RawImage>(true);
            foreach (var ri in allRaw)
            {
                if (ri.GetComponent<ChromaKeyController>() != null) continue; // 웹캠용은 스킵
                string n = ri.name.ToLower();
                if (n.Contains("background") || n.Contains("overlay") || n.Contains("bg"))
                {
                    overlayMgr.backgroundImageDisplay = ri;
                    Debug.Log($"✅ [Overlay] backgroundImageDisplay → '{ri.gameObject.name}' 자동 연결");
                    count++;
                    break;
                }
            }
        }

        EditorUtility.SetDirty(overlayMgr);
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1단계: 비디오 파일명 교정
    // ═══════════════════════════════════════════════════════════════════════════

    private static int FixVideoFileNames(AppStateManager appState)
    {
        int count = 0;

        if (appState.loopVideoFileName != "main.mp4")
        {
            string oldName = appState.loopVideoFileName;
            appState.loopVideoFileName = "main.mp4";
            Debug.Log($"✅ [비디오] loopVideoFileName: '{oldName}' → 'main.mp4'");
            count++;
        }

        if (appState.transitionVideoFileName != "transition.mov")
        {
            string oldName = appState.transitionVideoFileName;
            appState.transitionVideoFileName = "transition.mov";
            Debug.Log($"✅ [비디오] transitionVideoFileName: '{oldName}' → 'transition.mov'");
            count++;
        }

        if (appState.selectVideoFileName != "select.mov")
        {
            string oldName = appState.selectVideoFileName;
            appState.selectVideoFileName = "select.mov";
            Debug.Log($"✅ [비디오] selectVideoFileName: '{oldName}' → 'select.mov'");
            count++;
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  2단계: 관리자 패널 UI 세팅
    //  ★ 기존 LayoutGroup/ContentSizeFitter를 비활성화하고,
    //     모든 자식 요소를 절대 좌표로 직접 배치한다.
    // ═══════════════════════════════════════════════════════════════════════════

    private static int SetupAdminPanel(AppStateManager appState)
    {
        GameObject adminPanel = appState.adminPanel;
        if (adminPanel == null)
        {
            Debug.LogWarning("⚠️ [Admin] adminPanel이 할당되지 않았습니다. 관리자 UI 세팅을 건너뜁니다.");
            return 0;
        }

        Undo.RegisterFullObjectHierarchyUndo(adminPanel, "Setup Admin Panel");
        int count = 0;

        // ★ adminPanel 자체의 레이아웃 컴포넌트 비활성화 (자식 위치 강제 제어 방지)
        DisableLayoutComponents(adminPanel);

        // ── 슬라이더 연결 (이름 검색 → 실패 시 타입 순서 검색) ──
        count += AssignSliders(appState, adminPanel);

        // ── 한글 폰트 찾기 (NotoSansCJKkr, Pretendard 등 프로젝트 내 CJK폰트 검색) ──
        TMP_FontAsset koreanFont = FindKoreanFont();

        // ── 텍스트: 제목 (상단 중앙) ──
        appState.adminStepTitleText = EnsureTextMeshPro(adminPanel, "AdminStepTitleText",
            "Step 1: Master", 28, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -40), new Vector2(500, 50),
            koreanFont);
        count++;

        // ── 텍스트: 대상 이름 (상단 중앙, 제목 아래) ──
        appState.adminTargetNameText = EnsureTextMeshPro(adminPanel, "AdminTargetNameText",
            "Target: Global Preview", 22, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -80), new Vector2(500, 40),
            koreanFont);
        count++;

        // ── 토글: Use Local Chroma (슬라이더들 아래) ──
        appState.useLocalChromaToggle = EnsureToggle(adminPanel, "UseLocalChromaToggle",
            "Local Override", new Vector2(0.5f, 0.5f), new Vector2(0, -100), new Vector2(350, 30));
        count++;

        // ── 버튼: 이전단계 (좌하단) ──
        EnsureButton(adminPanel, "PrevAdminBtn", "< Prev",
            new Vector2(0f, 0f), new Vector2(100, 60), new Vector2(160, 50),
            appState, "PrevAdminStep");
        count++;

        // ── 버튼: 다음단계 (우하단) ──
        EnsureButton(adminPanel, "NextAdminBtn", "Next >",
            new Vector2(1f, 0f), new Vector2(-100, 60), new Vector2(160, 50),
            appState, "NextAdminStep");
        count++;

        // ── 버튼: 설정 저장 (하단 중앙) ──
        EnsureButton(adminPanel, "SaveAdminBtn", "SAVE",
            new Vector2(0.5f, 0f), new Vector2(0, 60), new Vector2(200, 60),
            appState, "ApplyAndSaveAdminConfig");
        count++;

        Debug.Log($"✅ [Admin] 관리자 패널 UI {count}개 항목 세팅 완료");
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3단계: 배경 선택 패널 버튼 연결
    // ═══════════════════════════════════════════════════════════════════════════

    private static int SetupSelectBGPanel(AppStateManager appState)
    {
        GameObject selectPanel = appState.panelSelectBG;
        if (selectPanel == null)
        {
            Debug.LogWarning("⚠️ [SelectBG] panelSelectBG가 할당되지 않았습니다. 배경 선택 세팅을 건너뜁니다.");
            return 0;
        }

        Undo.RegisterFullObjectHierarchyUndo(selectPanel, "Setup SelectBG Panel");
        int count = 0;

        // ── 비디오 플레이어 자동 연결 ──
        VideoPlayer vp = selectPanel.GetComponentInChildren<VideoPlayer>(true);
        if (vp == null)
        {
            GameObject vpObj = new GameObject("SelectBG_VideoPlayer");
            vpObj.transform.SetParent(selectPanel.transform, false);
            vpObj.transform.SetAsFirstSibling();

            RectTransform rt = vpObj.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;

            vp = vpObj.AddComponent<VideoPlayer>();
            vp.renderMode = VideoRenderMode.RenderTexture;

            Debug.Log("✅ [SelectBG] VideoPlayer 오브젝트 자동 생성");
        }

        appState.selectVideoPlayer = vp;
        count++;

        // ── 썸네일 버튼 OnClick 연결 ──
        Button[] allButtons = selectPanel.GetComponentsInChildren<Button>(true);
        int bgIndex = 0;
        foreach (Button btn in allButtons)
        {
            string lowerName = btn.name.ToLower();
            if (lowerName.Contains("back") || lowerName.Contains("home") ||
                lowerName.Contains("close") || lowerName.Contains("prev") ||
                lowerName.Contains("next") || lowerName.Contains("admin"))
                continue;

            while (btn.onClick.GetPersistentEventCount() > 0)
                UnityEditor.Events.UnityEventTools.RemovePersistentListener(btn.onClick, 0);

            var method = typeof(AppStateManager).GetMethod("SelectBackgroundAndGoNext",
                new System.Type[] { typeof(int) });
            var action = System.Delegate.CreateDelegate(
                typeof(UnityEngine.Events.UnityAction<int>), appState, method)
                as UnityEngine.Events.UnityAction<int>;
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(btn.onClick, action, bgIndex);

            Debug.Log($"✅ [SelectBG] '{btn.name}' → 배경 {bgIndex}번 연결");
            bgIndex++;
            count++;
        }

        Debug.Log($"✅ [SelectBG] 배경 선택 패널 세팅 완료 (버튼 {bgIndex}개, VP 1개)");
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  4단계: 건강 체크 (Validation)
    // ═══════════════════════════════════════════════════════════════════════════

    private static void RunValidation(AppStateManager appState)
    {
        Debug.Log("\n🔍 [Validation] 시스템 건강 체크 시작...");
        int warnCount = 0;

        if (appState.panelStandby == null) { Debug.LogWarning("⚠️ panelStandby 미연결"); warnCount++; }
        if (appState.panelSelectBG == null) { Debug.LogWarning("⚠️ panelSelectBG 미연결"); warnCount++; }
        if (appState.panelCapture == null) { Debug.LogWarning("⚠️ panelCapture 미연결"); warnCount++; }
        if (appState.panelResult == null) { Debug.LogWarning("⚠️ panelResult 미연결"); warnCount++; }
        if (appState.adminPanel == null) { Debug.LogWarning("⚠️ adminPanel 미연결"); warnCount++; }

        if (appState.standbyVideoPlayer == null) { Debug.LogWarning("⚠️ standbyVideoPlayer 미연결"); warnCount++; }
        if (appState.selectVideoPlayer == null) { Debug.LogWarning("⚠️ selectVideoPlayer 미연결"); warnCount++; }
        if (appState.photoCaptureManager == null) { Debug.LogWarning("⚠️ photoCaptureManager 미연결"); warnCount++; }

        if (Object.FindObjectOfType<OverlayBGManager>() == null) { Debug.LogWarning("⚠️ 씬에 OverlayBGManager가 없습니다"); warnCount++; }
        if (Object.FindObjectOfType<ChromaKeyController>() == null) { Debug.LogWarning("⚠️ 씬에 ChromaKeyController가 없습니다"); warnCount++; }
        if (Object.FindObjectOfType<PhotoBoothConfigLoader>() == null) { Debug.LogWarning("⚠️ 씬에 PhotoBoothConfigLoader가 없습니다"); warnCount++; }

        string saPath = Application.streamingAssetsPath;
        CheckFile(saPath, appState.loopVideoFileName, ref warnCount);
        CheckFile(saPath, appState.transitionVideoFileName, ref warnCount);
        CheckFile(saPath, appState.selectVideoFileName, ref warnCount);

        string configPath = Path.Combine(saPath, "config.json");
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            var matches = System.Text.RegularExpressions.Regex.Matches(json, "\"bgName\"\\s*:\\s*\"([^\"]+)\"");
            string[] exts = { ".jpg", ".jpeg", ".png" };
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string bgName = m.Groups[1].Value;
                bool found = false;
                foreach (string ext in exts)
                {
                    if (File.Exists(Path.Combine(saPath, bgName + ext)))
                    { found = true; break; }
                    if (File.Exists(Path.Combine(saPath, bgName)))
                    { found = true; break; }
                }
                if (!found)
                {
                    Debug.LogWarning($"⚠️ config.json 배경 '{bgName}' 에 대응하는 이미지가 StreamingAssets에 없습니다!");
                    warnCount++;
                }
            }
        }

        if (warnCount == 0)
            Debug.Log("✅ [Validation] 모든 항목 정상! 문제 없습니다.\n");
        else
            Debug.LogWarning($"⚠️ [Validation] {warnCount}개의 경고가 있습니다. 위 로그를 확인해주세요.\n");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  헬퍼 메서드들
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// adminPanel에 있는 VerticalLayoutGroup, HorizontalLayoutGroup,
    /// GridLayoutGroup, ContentSizeFitter 등을 비활성화하여
    /// 자식 요소가 강제 재배치되는 것을 방지한다.
    /// </summary>
    private static void DisableLayoutComponents(GameObject panel)
    {
        // 패널 자체
        foreach (var lg in panel.GetComponents<LayoutGroup>())
        {
            lg.enabled = false;
            Debug.Log($"✅ [Admin] LayoutGroup '{lg.GetType().Name}' 비활성화");
        }
        foreach (var csf in panel.GetComponents<ContentSizeFitter>())
        {
            csf.enabled = false;
            Debug.Log($"✅ [Admin] ContentSizeFitter 비활성화");
        }
    }

    /// <summary>
    /// 프로젝트 내에서 한글을 지원하는 TMP 폰트를 찾는다.
    /// NotoSansCJK, Pretendard, KoPub 등 이름에 CJK/Korean 등이 포함된 폰트를 우선 검색.
    /// 못 찾으면 null (기본 LiberationSans 사용 - 영문만 표시됨).
    /// </summary>
    private static TMP_FontAsset FindKoreanFont()
    {
        string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
        TMP_FontAsset fallback = null;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileNameWithoutExtension(path).ToLower();

            // 한글 폰트 우선순위 매칭
            if (fileName.Contains("noto") || fileName.Contains("cjk") ||
                fileName.Contains("korean") || fileName.Contains("pretendard") ||
                fileName.Contains("kopub") || fileName.Contains("spoqa") ||
                fileName.Contains("nanumgothic") || fileName.Contains("malgun"))
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null)
                {
                    Debug.Log($"✅ [Font] 한글 폰트 발견: {path}");
                    return font;
                }
            }

            // LiberationSans 가 아닌 다른 폰트가 있으면 후보로 저장
            if (!fileName.Contains("liberation") && fallback == null)
            {
                fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            }
        }

        if (fallback != null)
        {
            Debug.LogWarning($"⚠️ [Font] 한글 전용 폰트를 찾지 못했습니다. '{fallback.name}' 을 대체 사용합니다. " +
                             "한글이 □로 표시되면 NotoSansCJK SDF 폰트를 프로젝트에 추가해주세요.");
            return fallback;
        }

        Debug.LogWarning("⚠️ [Font] 프로젝트에 한글 폰트가 없습니다! " +
                         "Admin UI 텍스트가 □로 표시됩니다. " +
                         "NotoSansCJK SDF 폰트를 Assets/Fonts 에 추가해주세요.");
        return null;
    }

    private static int AssignSliders(AppStateManager appState, GameObject adminPanel)
    {
        int count = 0;

        appState.sensitivitySlider = FindSliderByName(adminPanel, "SensitivitySlider", "Sensitivity");
        appState.smoothnessSlider = FindSliderByName(adminPanel, "SmoothnessSlider", "Smoothness");
        appState.spillRemovalSlider = FindSliderByName(adminPanel, "SpillSlider", "Spill", "SpillRemoval");

        Slider[] allSliders = adminPanel.GetComponentsInChildren<Slider>(true);
        if (appState.sensitivitySlider == null && allSliders.Length >= 1)
        {
            appState.sensitivitySlider = allSliders[0];
            Debug.LogWarning($"⚠️ SensitivitySlider를 이름으로 못 찾아 {allSliders[0].name}(순서 1번)에 할당");
        }
        if (appState.smoothnessSlider == null && allSliders.Length >= 2)
        {
            appState.smoothnessSlider = allSliders[1];
            Debug.LogWarning($"⚠️ SmoothnessSlider를 이름으로 못 찾아 {allSliders[1].name}(순서 2번)에 할당");
        }
        if (appState.spillRemovalSlider == null && allSliders.Length >= 3)
        {
            appState.spillRemovalSlider = allSliders[2];
            Debug.LogWarning($"⚠️ SpillSlider를 이름으로 못 찾아 {allSliders[2].name}(순서 3번)에 할당");
        }

        if (appState.sensitivitySlider != null) count++;
        if (appState.smoothnessSlider != null) count++;
        if (appState.spillRemovalSlider != null) count++;

        return count;
    }

    private static Slider FindSliderByName(GameObject parent, params string[] names)
    {
        foreach (string name in names)
        {
            Transform found = parent.transform.Find(name);
            if (found != null)
            {
                Slider s = found.GetComponent<Slider>();
                if (s != null) return s;
            }
        }

        Slider[] allSliders = parent.GetComponentsInChildren<Slider>(true);
        foreach (Slider s in allSliders)
        {
            string lowerName = s.name.ToLower();
            foreach (string name in names)
            {
                if (lowerName.Contains(name.ToLower())) return s;
            }
        }

        return null;
    }

    private static TextMeshProUGUI EnsureTextMeshPro(GameObject parent, string objName,
        string defaultText, int fontSize, TextAlignmentOptions alignment,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size,
        TMP_FontAsset font)
    {
        // ★ 기존 오브젝트가 있으면 삭제 후 재생성 (레이아웃 오염 방지)
        Transform existing = parent.transform.Find(objName);
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        GameObject obj = new GameObject(objName);
        obj.transform.SetParent(parent.transform, false);

        var tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = defaultText;
        tmp.fontSize = fontSize;
        tmp.alignment = alignment;
        tmp.color = Color.white;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;

        // 한글 폰트 적용 (있으면)
        if (font != null)
            tmp.font = font;

        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        // 레이아웃 그룹 무시
        var le = obj.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        Debug.Log($"✅ [Admin] '{objName}' 텍스트 UI 생성 (font: {(font != null ? font.name : "default")})");
        return tmp;
    }

    private static Toggle EnsureToggle(GameObject parent, string objName,
        string labelText, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        // ★ 기존 오브젝트 삭제 후 재생성
        Transform existing = parent.transform.Find(objName);
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        GameObject toggleObj = DefaultControls.CreateToggle(new DefaultControls.Resources());
        toggleObj.name = objName;
        toggleObj.transform.SetParent(parent.transform, false);

        RectTransform rt = toggleObj.GetComponent<RectTransform>();
        rt.anchorMin = pivot;
        rt.anchorMax = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var le = toggleObj.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        Text legacyLabel = toggleObj.GetComponentInChildren<Text>();
        if (legacyLabel != null)
        {
            legacyLabel.text = labelText;
            legacyLabel.fontSize = 16;
            legacyLabel.color = Color.white;
            legacyLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        Debug.Log($"✅ [Admin] '{objName}' 토글 UI 생성");
        return toggleObj.GetComponent<Toggle>();
    }

    private static void EnsureButton(GameObject parent, string objName, string label,
        Vector2 anchor, Vector2 pos, Vector2 size,
        AppStateManager target, string methodName)
    {
        // ★ 기존 오브젝트 삭제 후 재생성 
        Transform existing = parent.transform.Find(objName);
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        GameObject btnObj = DefaultControls.CreateButton(new DefaultControls.Resources());
        btnObj.name = objName;
        btnObj.transform.SetParent(parent.transform, false);

        RectTransform rt = btnObj.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var le = btnObj.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        Text btnLabel = btnObj.GetComponentInChildren<Text>();
        if (btnLabel != null)
        {
            btnLabel.text = label;
            btnLabel.fontSize = 18;
            btnLabel.fontStyle = FontStyle.Bold;
            btnLabel.color = Color.black;
        }

        Button btn = btnObj.GetComponent<Button>();
        Debug.Log($"✅ [Admin] '{objName}' 버튼 UI 생성");

        // 이벤트 바인딩
        while (btn.onClick.GetPersistentEventCount() > 0)
            UnityEditor.Events.UnityEventTools.RemovePersistentListener(btn.onClick, 0);

        var method = typeof(AppStateManager).GetMethod(methodName);
        if (method != null)
        {
            var action = System.Delegate.CreateDelegate(
                typeof(UnityEngine.Events.UnityAction), target, method)
                as UnityEngine.Events.UnityAction;
            UnityEditor.Events.UnityEventTools.AddPersistentListener(btn.onClick, action);
        }
        else
        {
            Debug.LogError($"❌ [Admin] AppStateManager에 '{methodName}' 메서드를 찾을 수 없습니다!");
        }
    }

    private static void CheckFile(string basePath, string fileName, ref int warnCount)
    {
        string fullPath = Path.Combine(basePath, fileName);
        if (!File.Exists(fullPath))
        {
            Debug.LogWarning($"⚠️ StreamingAssets/{fileName} 파일이 존재하지 않습니다!");
            warnCount++;
        }
    }
}
#endif
