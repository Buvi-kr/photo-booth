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

        // ── 2단계: 관리자 패널 UI 완전 세팅 ──
        fixCount += SetupAdminPanel(appState);

        // ── 3단계: 배경 선택 패널 버튼 연결 ──
        fixCount += SetupSelectBGPanel(appState);

        // ── 4단계: 건강 체크 (Validation) ──
        RunValidation(appState);

        EditorUtility.SetDirty(appState);
        Debug.Log($"\n👑 [MasterSetup] 올인원 세팅 완료! 총 {fixCount}개 항목이 자동 처리되었습니다.");
        Debug.Log("💾 반드시 Ctrl+S 로 씬을 저장해주세요!\n");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1단계: 비디오 파일명 교정
    // ═══════════════════════════════════════════════════════════════════════════

    private static int FixVideoFileNames(AppStateManager appState)
    {
        int count = 0;

        // loopVideoFileName: 실제 파일 main.mp4에 맞춤
        if (appState.loopVideoFileName != "main.mp4")
        {
            string oldName = appState.loopVideoFileName;
            appState.loopVideoFileName = "main.mp4";
            Debug.Log($"✅ [비디오] loopVideoFileName: '{oldName}' → 'main.mp4'");
            count++;
        }

        // transitionVideoFileName: 실제 파일 transition.mov에 맞춤
        if (appState.transitionVideoFileName != "transition.mov")
        {
            string oldName = appState.transitionVideoFileName;
            appState.transitionVideoFileName = "transition.mov";
            Debug.Log($"✅ [비디오] transitionVideoFileName: '{oldName}' → 'transition.mov'");
            count++;
        }

        // selectVideoFileName: 실제 파일 select.mov에 맞춤
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

        // ── 슬라이더 연결 (이름 검색 → 실패 시 타입 순서 검색) ──
        count += AssignSliders(appState, adminPanel);

        // ── 텍스트 생성/연결: 제목 ──
        appState.adminStepTitleText = EnsureTextMeshPro(adminPanel, "AdminStepTitleText",
            "1단계: 마스터 설정", 24, new Vector2(0.5f, 1), new Vector2(0, -50), new Vector2(400, 50));
        count++;

        // ── 텍스트 생성/연결: 대상 이름 ──
        appState.adminTargetNameText = EnsureTextMeshPro(adminPanel, "AdminTargetNameText",
            "대상: 공통 프리뷰", 20, new Vector2(0.5f, 1), new Vector2(0, -90), new Vector2(400, 50));
        count++;

        // ── 토글 생성/연결: Use Local Chroma ──
        appState.useLocalChromaToggle = EnsureToggle(adminPanel, "UseLocalChromaToggle",
            "이 배경만 별도 크로마키 수치 적용", new Vector2(0, 150), new Vector2(400, 30));
        count++;

        // ── 버튼 생성/연결: 이전, 다음, 저장 ──
        EnsureButton(adminPanel, "PrevAdminBtn", "◀ 이전단계",
            new Vector2(0, 0.5f), new Vector2(120, 0), new Vector2(140, 50),
            appState, "PrevAdminStep");
        count++;

        EnsureButton(adminPanel, "NextAdminBtn", "다음단계 ▶",
            new Vector2(1, 0.5f), new Vector2(-120, 0), new Vector2(140, 50),
            appState, "NextAdminStep");
        count++;

        EnsureButton(adminPanel, "SaveAdminBtn", "💾 설정 저장",
            new Vector2(0.5f, 0), new Vector2(0, 80), new Vector2(200, 60),
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
            // 빈 오브젝트 생성 (UI 캔버스 하위)
            GameObject vpObj = new GameObject("SelectBG_VideoPlayer");
            vpObj.transform.SetParent(selectPanel.transform, false);
            vpObj.transform.SetAsFirstSibling(); // 배경으로 (맨 뒤에 렌더링)

            // RectTransform 추가 (UI 내 배치용)
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
            // 목적이 다른 특수 버튼 스킵
            if (lowerName.Contains("back") || lowerName.Contains("home") ||
                lowerName.Contains("close") || lowerName.Contains("prev") ||
                lowerName.Contains("next") || lowerName.Contains("admin"))
                continue;

            // 기존 리스너 초기화
            while (btn.onClick.GetPersistentEventCount() > 0)
                UnityEditor.Events.UnityEventTools.RemovePersistentListener(btn.onClick, 0);

            // SelectBackgroundAndGoNext(int) 바인딩
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

        // 패널 연결 확인
        if (appState.panelStandby == null) { Debug.LogWarning("⚠️ panelStandby 미연결"); warnCount++; }
        if (appState.panelSelectBG == null) { Debug.LogWarning("⚠️ panelSelectBG 미연결"); warnCount++; }
        if (appState.panelCapture == null) { Debug.LogWarning("⚠️ panelCapture 미연결"); warnCount++; }
        if (appState.panelResult == null) { Debug.LogWarning("⚠️ panelResult 미연결"); warnCount++; }
        if (appState.adminPanel == null) { Debug.LogWarning("⚠️ adminPanel 미연결"); warnCount++; }

        // 비디오 플레이어 확인
        if (appState.standbyVideoPlayer == null) { Debug.LogWarning("⚠️ standbyVideoPlayer 미연결"); warnCount++; }
        if (appState.selectVideoPlayer == null) { Debug.LogWarning("⚠️ selectVideoPlayer 미연결"); warnCount++; }

        // PhotoCaptureManager 확인
        if (appState.photoCaptureManager == null) { Debug.LogWarning("⚠️ photoCaptureManager 미연결"); warnCount++; }

        // OverlayBGManager 존재 확인
        if (Object.FindObjectOfType<OverlayBGManager>() == null) { Debug.LogWarning("⚠️ 씬에 OverlayBGManager가 없습니다"); warnCount++; }
        if (Object.FindObjectOfType<ChromaKeyController>() == null) { Debug.LogWarning("⚠️ 씬에 ChromaKeyController가 없습니다"); warnCount++; }
        if (Object.FindObjectOfType<PhotoBoothConfigLoader>() == null) { Debug.LogWarning("⚠️ 씬에 PhotoBoothConfigLoader가 없습니다"); warnCount++; }

        // StreamingAssets 비디오 파일 존재 확인
        string saPath = Application.streamingAssetsPath;
        CheckFile(saPath, appState.loopVideoFileName, ref warnCount);
        CheckFile(saPath, appState.transitionVideoFileName, ref warnCount);
        CheckFile(saPath, appState.selectVideoFileName, ref warnCount);

        // 배경 이미지 파일 존재 확인 (config.json 매칭)
        string configPath = Path.Combine(saPath, "config.json");
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            // 간이 파싱: bgName 찾기
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
                    // 확장자가 이미 붙어있는 경우도 체크
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

    private static int AssignSliders(AppStateManager appState, GameObject adminPanel)
    {
        int count = 0;

        // 이름으로 먼저 검색
        appState.sensitivitySlider = FindSliderByName(adminPanel, "SensitivitySlider", "Sensitivity");
        appState.smoothnessSlider = FindSliderByName(adminPanel, "SmoothnessSlider", "Smoothness");
        appState.spillRemovalSlider = FindSliderByName(adminPanel, "SpillSlider", "Spill", "SpillRemoval");

        // 이름 검색 실패 시 전수 검색 폴백
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

        // 하위 전체에서 이름 부분 매칭
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
        string defaultText, int fontSize, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        Transform existing = parent.transform.Find(objName);
        if (existing != null)
            return existing.GetComponent<TextMeshProUGUI>();

        GameObject obj = new GameObject(objName);
        obj.transform.SetParent(parent.transform, false);

        var tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = defaultText;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;

        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        Debug.Log($"✅ [Admin] '{objName}' 텍스트 UI 생성");
        return tmp;
    }

    private static Toggle EnsureToggle(GameObject parent, string objName,
        string labelText, Vector2 pos, Vector2 size)
    {
        Transform existing = parent.transform.Find(objName);
        if (existing != null)
            return existing.GetComponent<Toggle>();

        GameObject toggleObj = DefaultControls.CreateToggle(new DefaultControls.Resources());
        toggleObj.name = objName;
        toggleObj.transform.SetParent(parent.transform, false);

        RectTransform rt = toggleObj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
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

        Debug.Log($"✅ [Admin] '{objName}' 토글 UI 생성");
        return toggleObj.GetComponent<Toggle>();
    }

    private static void EnsureButton(GameObject parent, string objName, string label,
        Vector2 anchor, Vector2 pos, Vector2 size,
        AppStateManager target, string methodName)
    {
        Transform existing = parent.transform.Find(objName);

        Button btn;
        if (existing != null)
        {
            btn = existing.GetComponent<Button>();
        }
        else
        {
            GameObject btnObj = DefaultControls.CreateButton(new DefaultControls.Resources());
            btnObj.name = objName;
            btnObj.transform.SetParent(parent.transform, false);

            RectTransform rt = btnObj.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            Text btnLabel = btnObj.GetComponentInChildren<Text>();
            if (btnLabel != null)
            {
                btnLabel.text = label;
                btnLabel.fontSize = 16;
                btnLabel.color = Color.black;
            }

            btn = btnObj.GetComponent<Button>();
            Debug.Log($"✅ [Admin] '{objName}' 버튼 UI 생성");
        }

        // 기존 리스너 초기화 후 재연결
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
