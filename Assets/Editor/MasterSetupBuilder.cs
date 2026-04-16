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
        // ★ 방법 1: 이름으로 직접 찾기 (가장 확실)
        GameObject wcObj = GameObject.Find("WebCamDisplay");

        if (wcObj == null)
        {
            // panelCapture 하위에서 RawImage 검색
            if (appState.panelCapture != null)
            {
                RawImage[] rawImages = appState.panelCapture.GetComponentsInChildren<RawImage>(true);
                foreach (var ri in rawImages)
                {
                    string n = ri.name.ToLower();
                    if (n.Contains("webcam") || n.Contains("camera") || n.Contains("preview"))
                    {
                        wcObj = ri.gameObject;
                        break;
                    }
                }
                if (wcObj == null && rawImages.Length > 0)
                    wcObj = rawImages[0].gameObject;
            }
        }

        if (wcObj == null)
        {
            Debug.LogError("❌ 'WebCamDisplay' 오브젝트를 찾을 수 없습니다!");
            return 0;
        }

        // ★ 중복 체크: 같은 오브젝트에 여러 개 붙어있는지 확인
        ChromaKeyController[] controllers = wcObj.GetComponents<ChromaKeyController>();

        if (controllers.Length > 1)
        {
            Debug.LogWarning($"⚠️ [ChromaKey] '{wcObj.name}'에 ChromaKeyController가 {controllers.Length}개 있습니다! " +
                             "셰이더 없는 중복 컴포넌트를 제거합니다...");

            // 셰이더가 있는 것만 유지, 나머지 마킹
            for (int i = controllers.Length - 1; i >= 1; i--)
            {
                // 뒤에서부터 삭제 (첫 번째는 무조건 유지)
                Undo.DestroyObjectImmediate(controllers[i]);
                Debug.Log($"✅ [ChromaKey] 중복 컴포넌트 #{i} 제거 완료");
            }

            EditorUtility.SetDirty(wcObj);
            return 1;
        }

        if (controllers.Length == 1)
        {
            Debug.Log($"✅ [ChromaKey] '{wcObj.name}'에 ChromaKeyController 정상 존재");
            return 0;
        }

        // ★ 없으면 추가
        Undo.AddComponent<ChromaKeyController>(wcObj);
        Debug.Log($"✅ [ChromaKey] '{wcObj.name}'에 ChromaKeyController 추가 완료");
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

        Undo.RegisterFullObjectHierarchyUndo(adminPanel, "Rebuild Admin Panel");

        // ★★★ 핵심: 기존 자식 오브젝트를 전부 삭제하고 깨끗하게 시작 ★★★
        for (int i = adminPanel.transform.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(adminPanel.transform.GetChild(i).gameObject);
        }
        Debug.Log("🗑️ [Admin] 기존 AdminPanel 자식 요소 전부 삭제 완료");

        // ── adminPanel 자체 설정: 전체 화면, 반투명 배경, Raycast ON ──
        RectTransform panelRT = adminPanel.GetComponent<RectTransform>();
        panelRT.anchorMin = Vector2.zero;
        panelRT.anchorMax = Vector2.one;
        panelRT.sizeDelta = Vector2.zero;
        panelRT.anchoredPosition = Vector2.zero;

        Image panelBg = adminPanel.GetComponent<Image>();
        if (panelBg == null) panelBg = adminPanel.AddComponent<Image>();
        panelBg.color = new Color(0, 0, 0, 0.7f);      // 반투명 검정
        panelBg.raycastTarget = true;                     // ★ 뒤쪽 클릭 차단!

        // 레이아웃 컴포넌트 전부 제거
        foreach (var lg in adminPanel.GetComponents<LayoutGroup>()) Object.DestroyImmediate(lg);
        foreach (var csf in adminPanel.GetComponents<ContentSizeFitter>()) Object.DestroyImmediate(csf);

        int count = 0;

        // ══════════════════════════════════════════════════════════════════
        //  상단 영역: 제목 + 대상 이름
        // ══════════════════════════════════════════════════════════════════

        appState.adminStepTitleText = CreateTMP(adminPanel, "AdminStepTitleText",
            "Step 1: Master Chroma",
            32, FontStyle.Bold, Color.white, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -30), new Vector2(600, 50));
        count++;

        appState.adminTargetNameText = CreateTMP(adminPanel, "AdminTargetNameText",
            "Target: Global",
            22, FontStyle.Normal, new Color(0.8f, 0.9f, 1f), TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -70), new Vector2(600, 40));
        count++;

        // ══════════════════════════════════════════════════════════════════
        //  중앙 좌측: 슬라이더 3개 + 라벨
        // ══════════════════════════════════════════════════════════════════

        float sliderX = -300f;   // 중앙에서 왼쪽으로 300
        float sliderStartY = 60f;
        float sliderGap = 60f;

        appState.sensitivitySlider = CreateSlider(adminPanel, "SensitivitySlider", "Sensitivity",
            new Vector2(0.5f, 0.5f), new Vector2(sliderX, sliderStartY));
        count++;

        appState.smoothnessSlider = CreateSlider(adminPanel, "SmoothnessSlider", "Smoothness",
            new Vector2(0.5f, 0.5f), new Vector2(sliderX, sliderStartY - sliderGap));
        count++;

        appState.spillRemovalSlider = CreateSlider(adminPanel, "SpillSlider", "Spill Removal",
            new Vector2(0.5f, 0.5f), new Vector2(sliderX, sliderStartY - sliderGap * 2));
        count++;

        // ══════════════════════════════════════════════════════════════════
        //  중앙 우측: 색상 추출 안내 텍스트
        // ══════════════════════════════════════════════════════════════════

        CreateTMP(adminPanel, "ColorPickHint",
            "Click webcam\nto pick color",
            20, FontStyle.Italic, new Color(0.5f, 1f, 0.5f), TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(300, 30), new Vector2(250, 80));

        // ══════════════════════════════════════════════════════════════════
        //  중앙: 로컬 크로마 토글
        // ══════════════════════════════════════════════════════════════════

        appState.useLocalChromaToggle = CreateToggle(adminPanel, "UseLocalChromaToggle",
            "Use Local Override",
            new Vector2(0.5f, 0.5f), new Vector2(0, -130), new Vector2(300, 35));
        count++;

        // ══════════════════════════════════════════════════════════════════
        //  하단: Prev / Save / Next 버튼
        // ══════════════════════════════════════════════════════════════════

        CreateButton(adminPanel, "PrevAdminBtn", "< PREV",
            new Color(0.3f, 0.3f, 0.3f), Color.white,
            new Vector2(0f, 0f), new Vector2(120, 50), new Vector2(180, 60),
            appState, "PrevAdminStep");
        count++;

        CreateButton(adminPanel, "SaveAdminBtn", "SAVE CONFIG",
            new Color(0.1f, 0.5f, 0.1f), Color.white,
            new Vector2(0.5f, 0f), new Vector2(0, 50), new Vector2(220, 60),
            appState, "ApplyAndSaveAdminConfig");
        count++;

        CreateButton(adminPanel, "NextAdminBtn", "NEXT >",
            new Color(0.3f, 0.3f, 0.3f), Color.white,
            new Vector2(1f, 0f), new Vector2(-120, 50), new Vector2(180, 60),
            appState, "NextAdminStep");
        count++;

        Debug.Log($"✅ [Admin] 관리자 패널 UI {count}개 항목 완전 재구축 완료!");
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
    //  헬퍼: Create 계열 (처음부터 생성, 중복 걱정 없음)
    // ═══════════════════════════════════════════════════════════════════════════

    private static TextMeshProUGUI CreateTMP(GameObject parent, string objName,
        string text, int fontSize, FontStyle style, Color color,
        TextAlignmentOptions alignment,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
    {
        GameObject obj = new GameObject(objName);
        obj.transform.SetParent(parent.transform, false);

        var tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = (TMPro.FontStyles)style;
        tmp.alignment = alignment;
        tmp.color = color;
        tmp.enableWordWrapping = true;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.raycastTarget = false;

        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        return tmp;
    }

    private static Slider CreateSlider(GameObject parent, string objName, string labelText,
        Vector2 anchor, Vector2 pos)
    {
        // 컨테이너
        GameObject container = new GameObject(objName);
        container.transform.SetParent(parent.transform, false);

        RectTransform crt = container.AddComponent<RectTransform>();
        crt.anchorMin = anchor;
        crt.anchorMax = anchor;
        crt.anchoredPosition = pos;
        crt.sizeDelta = new Vector2(400, 40);

        // 라벨
        GameObject labelObj = new GameObject("Label");
        labelObj.transform.SetParent(container.transform, false);
        var labelTMP = labelObj.AddComponent<TextMeshProUGUI>();
        labelTMP.text = labelText;
        labelTMP.fontSize = 16;
        labelTMP.color = new Color(0.9f, 0.9f, 0.9f);
        labelTMP.alignment = TextAlignmentOptions.Left;
        labelTMP.raycastTarget = false;
        RectTransform lrt = labelObj.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 0.5f);
        lrt.anchorMax = new Vector2(0, 0.5f);
        lrt.anchoredPosition = new Vector2(60, 0);
        lrt.sizeDelta = new Vector2(120, 30);

        // 슬라이더 (Unity 기본 슬라이더 사용)
        GameObject sliderObj = DefaultControls.CreateSlider(new DefaultControls.Resources());
        sliderObj.name = "Slider";
        sliderObj.transform.SetParent(container.transform, false);
        RectTransform srt = sliderObj.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0.3f, 0f);
        srt.anchorMax = new Vector2(1f, 1f);
        srt.sizeDelta = Vector2.zero;
        srt.anchoredPosition = Vector2.zero;

        Slider slider = sliderObj.GetComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = 0.5f;

        Debug.Log($"✅ [Admin] '{objName}' 슬라이더 생성");
        return slider;
    }

    private static Toggle CreateToggle(GameObject parent, string objName, string labelText,
        Vector2 anchor, Vector2 pos, Vector2 size)
    {
        GameObject toggleObj = DefaultControls.CreateToggle(new DefaultControls.Resources());
        toggleObj.name = objName;
        toggleObj.transform.SetParent(parent.transform, false);

        RectTransform rt = toggleObj.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        Text legacyLabel = toggleObj.GetComponentInChildren<Text>();
        if (legacyLabel != null)
        {
            legacyLabel.text = labelText;
            legacyLabel.fontSize = 16;
            legacyLabel.color = Color.white;
            legacyLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        Debug.Log($"✅ [Admin] '{objName}' 토글 생성");
        return toggleObj.GetComponent<Toggle>();
    }

    private static void CreateButton(GameObject parent, string objName, string label,
        Color bgColor, Color textColor,
        Vector2 anchor, Vector2 pos, Vector2 size,
        AppStateManager target, string methodName)
    {
        GameObject btnObj = DefaultControls.CreateButton(new DefaultControls.Resources());
        btnObj.name = objName;
        btnObj.transform.SetParent(parent.transform, false);

        RectTransform rt = btnObj.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        // 배경색 변경
        Image btnImage = btnObj.GetComponent<Image>();
        if (btnImage != null) btnImage.color = bgColor;

        // 라벨 변경
        Text btnLabel = btnObj.GetComponentInChildren<Text>();
        if (btnLabel != null)
        {
            btnLabel.text = label;
            btnLabel.fontSize = 18;
            btnLabel.fontStyle = FontStyle.Bold;
            btnLabel.color = textColor;
        }

        // 이벤트 바인딩
        Button btn = btnObj.GetComponent<Button>();
        while (btn.onClick.GetPersistentEventCount() > 0)
            UnityEditor.Events.UnityEventTools.RemovePersistentListener(btn.onClick, 0);

        var method = typeof(AppStateManager).GetMethod(methodName);
        if (method != null)
        {
            var action = System.Delegate.CreateDelegate(
                typeof(UnityEngine.Events.UnityAction), target, method)
                as UnityEngine.Events.UnityAction;
            UnityEditor.Events.UnityEventTools.AddPersistentListener(btn.onClick, action);
            Debug.Log($"✅ [Admin] '{objName}' 버튼 생성 → {methodName}");
        }
        else
        {
            Debug.LogError($"❌ AppStateManager에 '{methodName}' 메서드가 없습니다!");
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
