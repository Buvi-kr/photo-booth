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

        // ── 5-2단계: 결과 패널 버튼 연결 ──
        fixCount += SetupResultPanel(appState);

        // ── 5-3단계: 스탠바이 패널 텍스트 세팅 ──
        fixCount += SetupStandbyPanel(appState);

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

        // ★ 마스크 컨테이너 구성 (일반 Mask 지원 - 회전 크롭 완벽 대응)
        Transform parent = wcObj.transform.parent;
        GameObject maskObj = null;
        
        if (parent != null && parent.name == "WebCamMaskContainer")
        {
            maskObj = parent.gameObject;
        }
        else
        {
            maskObj = new GameObject("WebCamMaskContainer");
            Undo.RegisterCreatedObjectUndo(maskObj, "Create Mask Container");
            maskObj.transform.SetParent(parent, false);
            maskObj.transform.SetSiblingIndex(wcObj.transform.GetSiblingIndex());
            
            RectTransform maskRT = maskObj.AddComponent<RectTransform>();
            RectTransform wcRT = wcObj.GetComponent<RectTransform>();
            
            // 컨테이너는 기존 WebCamDisplay 위치/크기를 그대로 계승
            maskRT.anchorMin = wcRT.anchorMin;
            maskRT.anchorMax = wcRT.anchorMax;
            maskRT.pivot = wcRT.pivot;
            maskRT.anchoredPosition = wcRT.anchoredPosition;
            maskRT.sizeDelta = wcRT.sizeDelta;
            
            wcObj.transform.SetParent(maskObj.transform, false);
            wcRT.anchorMin = Vector2.zero;
            wcRT.anchorMax = Vector2.one;
            wcRT.sizeDelta = Vector2.zero;
            wcRT.anchoredPosition = Vector2.zero;
        }

        RectTransform finalMaskRT = maskObj.GetComponent<RectTransform>();
        if (finalMaskRT != null)
        {
            // 안정적인 회전/이동을 위해 중앙 피벗 강제 설정
            finalMaskRT.pivot = new Vector2(0.5f, 0.5f);
        }

        // 구형 RectMask2D 가 있다면 제거
        RectMask2D oldRM2D = maskObj.GetComponent<RectMask2D>();
        if (oldRM2D != null)
        {
            Undo.DestroyObjectImmediate(oldRM2D);
            Debug.Log("🗑️ [ChromaKey] 구형 RectMask2D 제거 완료");
        }

        // 일반 Mask 구성을 위해 Image 컴포넌트 필수 추가
        if (maskObj.GetComponent<Image>() == null)
        {
            Image img = Undo.AddComponent<Image>(maskObj);
            img.color = Color.white; // 마스크 기본 컬러
            Debug.Log("✅ [ChromaKey] MaskContainer에 Image 추가 완료");
        }

        if (maskObj.GetComponent<Mask>() == null)
        {
            Undo.AddComponent<Mask>(maskObj);
            Debug.Log("✅ [ChromaKey] MaskContainer에 Mask 추가 완료 (회전 크롭 지원)");
        }

        // ★ 컴포넌트 체크 및 추가
        if (wcObj.GetComponent<ChromaKeyController>() == null)
        {
            Undo.AddComponent<ChromaKeyController>(wcObj);
            Debug.Log($"✅ [ChromaKey] '{wcObj.name}'에 ChromaKeyController 추가 완료");
        }

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
            RawImage[] allRaw = Object.FindObjectsOfType<RawImage>(true);
            foreach (var ri in allRaw)
            {
                if (ri.GetComponent<ChromaKeyController>() != null) continue;
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

        // foregroundImageDisplay 자동 생성 및 연결
        if (overlayMgr.foregroundImageDisplay == null && appState.panelCapture != null)
        {
            RawImage[] allRaw = appState.panelCapture.GetComponentsInChildren<RawImage>(true);
            foreach (var ri in allRaw)
            {
                string n = ri.name.ToLower();
                if (n.Contains("foreground") || n.Contains("front"))
                {
                    overlayMgr.foregroundImageDisplay = ri;
                    Debug.Log($"✅ [Overlay] foregroundImageDisplay → '{ri.gameObject.name}' 기결정");
                    break;
                }
            }

            if (overlayMgr.foregroundImageDisplay == null)
            {
                GameObject fgObj = new GameObject("ForegroundFrame");
                fgObj.transform.SetParent(appState.panelCapture.transform, false);
                fgObj.transform.SetAsLastSibling(); // 제일 앞
                RectTransform rt = fgObj.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.sizeDelta = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;
                RawImage fgImg = fgObj.AddComponent<RawImage>();
                fgImg.raycastTarget = false; // 클릭 통과
                
                overlayMgr.foregroundImageDisplay = fgImg;
                Debug.Log("✅ [Overlay] ForegroundFrame 생성 및 연결 완료");
                count++;
            }
        }

        // bgThumbnailButtons 자동 연결 (항상 최신화하도록 조건 완화)
        if (appState.panelSelectBG != null)
        {
            Button[] allButtons = appState.panelSelectBG.GetComponentsInChildren<Button>(true);
            System.Collections.Generic.List<Button> thumbBtns = new System.Collections.Generic.List<Button>();
            foreach (var btn in allButtons)
            {
                string lowerName = btn.name.ToLower();
                if (lowerName.Contains("back") || lowerName.Contains("home") ||
                    lowerName.Contains("close") || lowerName.Contains("prev") ||
                    lowerName.Contains("next") || lowerName.Contains("admin"))
                    continue;
                thumbBtns.Add(btn);
            }

            if (thumbBtns.Count > 0)
            {
                overlayMgr.bgThumbnailButtons = thumbBtns.ToArray();
                Debug.Log($"✅ [Overlay] 썸네일 버튼 {thumbBtns.Count}개 자동 연결 완료");
                count++;
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
            Debug.LogWarning("⚠️ [Admin] adminPanel이 할당되지 않았습니다.");
            return 0;
        }

        Undo.RegisterFullObjectHierarchyUndo(adminPanel, "Rebuild Admin Panel");

        // ★ 기존 자식 전부 삭제
        for (int i = adminPanel.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(adminPanel.transform.GetChild(i).gameObject);

        // ── adminPanel 자체 설정 ──
        RectTransform panelRT = adminPanel.GetComponent<RectTransform>();
        panelRT.anchorMin = Vector2.zero;
        panelRT.anchorMax = Vector2.one;
        panelRT.sizeDelta = Vector2.zero;
        panelRT.anchoredPosition = Vector2.zero;

        Image panelBg = adminPanel.GetComponent<Image>();
        if (panelBg == null) panelBg = adminPanel.AddComponent<Image>();
        panelBg.color = new Color(0, 0, 0, 0.55f);
        panelBg.raycastTarget = false;  // ★ false: 색상 추출 클릭이 관통하도록!

        foreach (var lg in adminPanel.GetComponents<LayoutGroup>()) Object.DestroyImmediate(lg);
        foreach (var csf in adminPanel.GetComponents<ContentSizeFitter>()) Object.DestroyImmediate(csf);

        // ── 한글 폰트 로드 ──
        TMP_FontAsset koreanFont = FindKoreanFont();

        int count = 0;

        // ══════════════════════════════════════════════════════════════
        //  좌상단: 제목 + 대상 표시
        // ══════════════════════════════════════════════════════════════

        appState.adminStepTitleText = CreateTMP(adminPanel, "AdminStepTitleText",
            "[1] Global Chroma",
            26, FontStyle.Bold, Color.white, TextAlignmentOptions.TopLeft,
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(300, -15), new Vector2(500, 35), koreanFont);
        count++;

        appState.adminTargetNameText = CreateTMP(adminPanel, "AdminTargetNameText",
            "Global Master",
            18, FontStyle.Normal, new Color(0.7f, 0.9f, 1f), TextAlignmentOptions.TopLeft,
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(300, -48), new Vector2(500, 28), koreanFont);
        count++;

        // ══════════════════════════════════════════════════════════════
        //  좌측 슬라이더 7개 (크로마키 3 + 색상보정 4)
        //  우측 슬라이더 4개 (웹캠 변환)
        // ══════════════════════════════════════════════════════════════

        float startY = -85f;
        float gap = 50f;
        float leftX = 300f;
        float rightX = leftX + 500f; // 2단 레이아웃 우측
        int idx = 0;

        // --- 크로마키 슬라이더 (항상 표시) ---
        appState.sensitivitySlider = CreateSlider(adminPanel, "SensitivitySlider",
            "감도 (Clip Black)", 0f, 100f, 35f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.smoothnessSlider = CreateSlider(adminPanel, "SmoothnessSlider",
            "부드러움 (Clip White)", 0f, 50f, 8f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.spillRemovalSlider = CreateSlider(adminPanel, "SpillSlider",
            "스필 제거", 0f, 100f, 15f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.edgeChokeSlider = CreateSlider(adminPanel, "EdgeChokeSlider",
            "외곽선 축소 (Shrink)", 0f, 50f, 0f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.lumaWeightSlider = CreateSlider(adminPanel, "LumaWeightSlider",
            "명도 가중치 (Inside Mask)", 0f, 100f, 0f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.preBlurSlider = CreateSlider(adminPanel, "PreBlurSlider",
            "사전 블러 (Screen Pre-blur)", 0f, 100f, 0f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        // --- 구분선 텍스트 ---
        CreateTMP(adminPanel, "ColorGradingSeparator",
            "── 색상 보정 (배경별) ──",
            14, FontStyle.Normal, new Color(1f, 0.8f, 0.3f), TextAlignmentOptions.TopLeft,
            new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(leftX, startY - gap * idx++), new Vector2(300, 25), koreanFont);

        // --- 색상보정 슬라이더 (배경별 페이지에서만 활성) ---
        appState.brightnessSlider = CreateSlider(adminPanel, "BrightnessSlider",
            "밝기", -100f, 100f, 0f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.contrastSlider = CreateSlider(adminPanel, "ContrastSlider",
            "대비", 0f, 200f, 100f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.saturationSlider = CreateSlider(adminPanel, "SaturationSlider",
            "채도", 0f, 200f, 100f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        appState.hueSlider = CreateSlider(adminPanel, "HueSlider",
            "색조", -180f, 180f, 0f,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), koreanFont);
        count++;

        // --- 웹캠 변환 슬라이더 (우측 레이아웃) ---
        int rightIdx = 0; // 우측 상단부터 시작
        CreateTMP(adminPanel, "TransformSeparator",
            "── 웹캠 변환 (배경별) ──",
            14, FontStyle.Normal, new Color(1f, 0.4f, 0.4f), TextAlignmentOptions.TopLeft,
            new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(rightX, startY - gap * rightIdx++), new Vector2(300, 25), koreanFont);

        appState.zoomSlider = CreateSlider(adminPanel, "ZoomSlider",
            "크기 (Zoom %)", 50f, 300f, 100f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.moveXSlider = CreateSlider(adminPanel, "MoveXSlider",
            "수평 이동", -100f, 100f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.moveYSlider = CreateSlider(adminPanel, "MoveYSlider",
            "수직 이동", -100f, 100f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.rotationSlider = CreateSlider(adminPanel, "RotationSlider",
            "회전 (도)", -180f, 180f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        // --- 마스크 (크롭 및 페이딩) ---
        CreateTMP(adminPanel, "MaskSeparator",
            "── 마스크 (크롭 및 페이딩) ──",
            14, FontStyle.Normal, new Color(1f, 0.4f, 0.8f), TextAlignmentOptions.TopLeft,
            new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(rightX, startY - gap * rightIdx++), new Vector2(300, 25), koreanFont);

        appState.cropTopSlider = CreateSlider(adminPanel, "CropTopSlider",
            "위쪽 자르기", 0f, 1000f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.cropBottomSlider = CreateSlider(adminPanel, "CropBottomSlider",
            "아래쪽 자르기", 0f, 1000f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.cropLeftSlider = CreateSlider(adminPanel, "CropLeftSlider",
            "왼쪽 자르기", 0f, 1000f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.cropRightSlider = CreateSlider(adminPanel, "CropRightSlider",
            "오른쪽 자르기", 0f, 1000f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.fadeXSlider = CreateSlider(adminPanel, "FadeXSlider",
            "좌우 페이딩", 0f, 500f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        appState.fadeYSlider = CreateSlider(adminPanel, "FadeYSlider",
            "상하 페이딩", 0f, 500f, 0f,
            new Vector2(0, 1), new Vector2(rightX, startY - gap * rightIdx++), koreanFont);
        count++;

        // ══════════════════════════════════════════════════════════════
        //  상단 메뉴: 스포이드 버튼 / 탐색기 열기 버튼
        // ══════════════════════════════════════════════════════════════

        CreateButton(adminPanel, "ColorPickBtn", "스포이드 (색상 추출)",
            new Color(0.2f, 0.6f, 0.3f), Color.white,
            new Vector2(0, 1), new Vector2(leftX, startY - gap * idx++), new Vector2(250, 35),
            appState, "ToggleColorPickMode");
        count++;

        // --- 돋보기 패널 (Magnifier) 생성 ---
        GameObject magnifierPanel = new GameObject("MagnifierPanel", typeof(RectTransform));
        magnifierPanel.transform.SetParent(adminPanel.transform, false);
        var magRect = magnifierPanel.GetComponent<RectTransform>();
        magRect.sizeDelta = new Vector2(150, 150);
        magRect.pivot = new Vector2(0.5f, 0.5f);
        magRect.anchorMin = new Vector2(0, 0);
        magRect.anchorMax = new Vector2(0, 0);
        
        var magBg = magnifierPanel.AddComponent<Image>();
        magBg.color = Color.white; // 테두리 역할
        
        GameObject magView = new GameObject("View", typeof(RectTransform));
        magView.transform.SetParent(magnifierPanel.transform, false);
        var viewRect = magView.GetComponent<RectTransform>();
        viewRect.sizeDelta = new Vector2(140, 140);
        viewRect.anchoredPosition = Vector2.zero;
        var magRawImage = magView.AddComponent<RawImage>();
        
        GameObject magCross = new GameObject("Crosshair", typeof(RectTransform));
        magCross.transform.SetParent(magnifierPanel.transform, false);
        var crossRect = magCross.GetComponent<RectTransform>();
        crossRect.sizeDelta = new Vector2(10, 10);
        var crossImg = magCross.AddComponent<Image>();
        crossImg.color = Color.red;

        magnifierPanel.SetActive(false);
        appState.magnifierPanel = magnifierPanel;
        appState.magnifierRawImage = magRawImage;

        CreateButton(adminPanel, "OpenFolderBtn", "📁 폴더 열기",
            new Color(0.3f, 0.4f, 0.7f), Color.white,
            new Vector2(1, 1), new Vector2(-150, -40), new Vector2(120, 40),
            appState, "OpenStreamingAssetsFolder");
        count++;

        // ══════════════════════════════════════════════════════════════
        //  초기화 버튼 (슬라이더들 아래)
        // ══════════════════════════════════════════════════════════════
        CreateButton(adminPanel, "ResetLocalBtn", "🔃 0으로 초기화",
            new Color(0.8f, 0.3f, 0.3f), Color.white,
            new Vector2(0.5f, 0f), new Vector2(0, 100), new Vector2(200, 40),
            appState, "ResetAdminCurrentBackground");
        count++;

        // ══════════════════════════════════════════════════════════════
        //  하단: PREV / SAVE / NEXT 버튼
        // ══════════════════════════════════════════════════════════════

        CreateButton(adminPanel, "PrevAdminBtn", "< PREV",
            new Color(0.25f, 0.25f, 0.35f), Color.white,
            new Vector2(0f, 0f), new Vector2(110, 40), new Vector2(160, 50),
            appState, "PrevAdminStep");
        count++;

        CreateButton(adminPanel, "SaveAdminBtn", "SAVE",
            new Color(0.1f, 0.55f, 0.15f), Color.white,
            new Vector2(0.5f, 0f), new Vector2(0, 40), new Vector2(180, 50),
            appState, "ApplyAndSaveAdminConfig");
        count++;

        CreateButton(adminPanel, "NextAdminBtn", "NEXT >",
            new Color(0.25f, 0.25f, 0.35f), Color.white,
            new Vector2(1f, 0f), new Vector2(-110, 40), new Vector2(160, 50),
            appState, "NextAdminStep");
        count++;

        EditorUtility.SetDirty(appState);
        Debug.Log($"✅ [Admin] 관리자 패널 UI {count}개 항목 재구축 완료!");
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

            // ── 썸네일 버튼 OnClick 연결 및 배열 저장 ──
        Button[] allButtons = selectPanel.GetComponentsInChildren<Button>(true);
        System.Collections.Generic.List<RectTransform> validButtons = new System.Collections.Generic.List<RectTransform>();
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

            // ── 버튼 크기 조정 (배경 네모 뚫림 방지: 가로-20, 세로-40 -> 추가로 테두리 짤림 해결을 위해 아래에서 20 축소)
            // 즉 y에서 총 -60을 하고, 위쪽 기준을 유지하기 위해 Y좌표를 +10 올리면 맨 아래 공간 20px가 잘림.
            RectTransform rt = btn.GetComponent<RectTransform>();
            if (rt != null)
            {
                // 배경 선택 버튼 크기를 아래에서 20px 자르고 (-60 -> -80), 
                // 그만큼 전체적으로 위로 올림 (+10 -> +40)
                rt.sizeDelta = new Vector2(rt.sizeDelta.x - 20, rt.sizeDelta.y - 80);
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y + 40);
                
                // 아랫줄(4, 5, 6번째 즉 bgIndex 3, 4, 5)을 강제로 위로 5px 추가로 올림
                if (bgIndex >= 3)
                {
                    rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y + 5);
                }

                validButtons.Add(rt);
            }

            Debug.Log($"✅ [SelectBG] '{btn.name}' → 배경 {bgIndex}번 연결 & 크기 조정(X:-20, Y:-40)");
            bgIndex++;
            count++;
        }

        appState.bgButtons = validButtons.ToArray();

        // ── 셀렉트 박스 (조이스틱 포커스 커서) 자동 생성 ──
        Transform existingCursor = selectPanel.transform.Find("JoystickSelectCursor");
        if (existingCursor != null)
        {
            Undo.DestroyObjectImmediate(existingCursor.gameObject);
            Debug.Log("✅ [SelectBG] 기존 JoystickSelectCursor 삭제 완료");
        }
        
        if (validButtons.Count > 0)
        {
            GameObject cursorObj = new GameObject("JoystickSelectCursor");
            cursorObj.transform.SetParent(selectPanel.transform, false);
            cursorObj.transform.SetAsLastSibling(); // 제일 나중에 그려짐 (맨 앞)

            RectTransform cRT = cursorObj.AddComponent<RectTransform>();
            // 버튼과 물리적으로 100% 동일한 공간(스펙)을 가져야 어긋나지 않음
            cRT.anchorMin = validButtons[0].anchorMin;
            cRT.anchorMax = validButtons[0].anchorMax;
            cRT.pivot = validButtons[0].pivot;
            cRT.sizeDelta = validButtons[0].sizeDelta; // 버튼 사이즈와 동일하게 맞춤 (오프셋 팽창 X)

            Image cImg = cursorObj.AddComponent<Image>();
            cImg.color = new Color(0f, 0f, 0f, 0f); // 투명하게 속을 비움
            cImg.raycastTarget = false;
            
            // 테두리 오프셋을 0으로 설정하여 버튼 사각형에 정확히 꽉 채움
            CreateBorder(cursorObj, "TopBorder", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(40, 20), Vector2.zero);
            CreateBorder(cursorObj, "BottomBorder", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(40, 20), Vector2.zero);
            CreateBorder(cursorObj, "LeftBorder", new Vector2(0, 0), new Vector2(0, 1), new Vector2(0.0f, 0.5f), new Vector2(20, 0), Vector2.zero);
            CreateBorder(cursorObj, "RightBorder", new Vector2(1, 0), new Vector2(1, 1), new Vector2(1.0f, 0.5f), new Vector2(20, 0), Vector2.zero);

            appState.selectCursor = cRT;
            Debug.Log("✅ [SelectBG] JoystickSelectCursor 자동 생성 및 연결 완료");
            count++;
        }

        // ── 배경 선택 안내 반투명 패널 및 자막 텍스트 추가 ──
        Transform existingPanel = selectPanel.transform.Find("SelectBG_Subtitle_Panel");
        if (existingPanel != null) Undo.DestroyObjectImmediate(existingPanel.gameObject);
        
        Transform existingSubtitle = selectPanel.transform.Find("SelectBG_Subtitle");
        if (existingSubtitle != null) Undo.DestroyObjectImmediate(existingSubtitle.gameObject);

        GameObject subtitlePanelObj = new GameObject("SelectBG_Subtitle_Panel");
        subtitlePanelObj.transform.SetParent(selectPanel.transform, false);
        RectTransform panelRt = subtitlePanelObj.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.5f, 0f);
        panelRt.anchorMax = new Vector2(0.5f, 0f);
        panelRt.anchoredPosition = new Vector2(0, 180);
        panelRt.sizeDelta = new Vector2(1920, 150); // 화면 전체를 가로지르는 반투명 띠
        Image panelImg = subtitlePanelObj.AddComponent<Image>();
        panelImg.color = new Color(0, 0, 0, 0.7f); // 70% 불투명도의 검은색
        panelImg.raycastTarget = false;

        TMP_FontAsset koreanFont = FindKoreanFont();
        Color subtitleColor;
        ColorUtility.TryParseHtmlString("#00FFFF", out subtitleColor); // 사이버펑크 네온 청록색
        var subtitleTmp = CreateTMP(selectPanel, "SelectBG_Subtitle", "배경을 선택해주세요",
            90, FontStyle.Bold, subtitleColor, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 180), new Vector2(1200, 150), koreanFont);
            
        if (subtitleTmp.fontSharedMaterial != null)
        {
            Material themeMat = new Material(subtitleTmp.fontSharedMaterial);
            themeMat.DisableKeyword("OUTLINE_ON"); // 지저분했던 외곽선 완전 제거
            
            // 글자가 묻히지 않게 은은하고 부드러운 그림자만 살짝 추가
            themeMat.EnableKeyword("UNDERLAY_ON");
            themeMat.SetFloat("_UnderlayOffsetX", 0.5f);
            themeMat.SetFloat("_UnderlayOffsetY", -0.5f);
            themeMat.SetFloat("_UnderlayDilate", 0f);
            themeMat.SetFloat("_UnderlaySoftness", 0.5f);
            themeMat.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.8f));
            
            subtitleTmp.fontSharedMaterial = themeMat;
        }
        
        Debug.Log("✅ [SelectBG] 배경 선택 안내 자막(Subtitle) 네온 테마 적용 완료");
        count++;

        Debug.Log($"✅ [SelectBG] 배경 선택 패널 세팅 완료 (버튼 {bgIndex}개, VP 1개)");
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3.5단계: 결과 패널 버튼 및 커서 생성 (자동화)
    // ═══════════════════════════════════════════════════════════════════════════
    private static int SetupResultPanel(AppStateManager appState)
    {
        GameObject resultPanel = appState.panelResult;
        if (resultPanel == null)
        {
            Debug.LogWarning("⚠️ [Result] panelResult가 할당되지 않았습니다. 결과 패널 세팅을 건너뜁니다.");
            return 0;
        }

        Undo.RegisterFullObjectHierarchyUndo(resultPanel, "Setup Result Panel");
        int count = 0;

        Button[] allButtons = resultPanel.GetComponentsInChildren<Button>(true);
        System.Collections.Generic.List<RectTransform> validButtons = new System.Collections.Generic.List<RectTransform>();

        RectTransform completeBtn = null;
        RectTransform retakeBtn = null;

        foreach (Button btn in allButtons)
        {
            string lowerName = btn.name.ToLower();

            // 배경변경 버튼 강제 제거
            if (lowerName.Contains("changebg") || lowerName.Contains("배경") || lowerName.Contains("change"))
            {
                btn.gameObject.SetActive(false);
                EditorUtility.SetDirty(btn.gameObject);
                Debug.Log($"✅ [Result] 배경 변경 버튼('{btn.name}')을 비활성화했습니다.");
                continue;
            }

            // 홈/뒤로가기/불필요 파츠 패스
            if (lowerName.Contains("back") || lowerName.Contains("home") || lowerName.Contains("close") ||
                lowerName.Contains("bg") || lowerName.Contains("thumb") || lowerName.Contains("background") ||
                lowerName.Contains("admin"))
                continue;

            RectTransform rt = btn.GetComponent<RectTransform>();
            if (rt != null)
            {
                validButtons.Add(rt);

                // GameObject 이름뿐만 아니라 버튼 안에 들어있는 Text 글자까지 싹 다 뽑아서 식별률을 100%로 올림
                string btnText = "";
                var txt = btn.GetComponentInChildren<Text>(true);
                if (txt != null) btnText += txt.text;
                var tmp = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                if (tmp != null) btnText += tmp.text;
                
                string combinedSearch = lowerName + btnText.ToLower();

                if (combinedSearch.Contains("처음") || combinedSearch.Contains("완료") || combinedSearch.Contains("complete") || combinedSearch.Contains("submit"))
                    completeBtn = rt;
                else if (combinedSearch.Contains("다시") || combinedSearch.Contains("retake") || combinedSearch.Contains("retry"))
                    retakeBtn = rt;
            }
            count++;
        }

        // --- 디자인 강제 교정 (크기와 정렬 맞추기) ---
        if (completeBtn != null && retakeBtn != null)
        {
            Undo.RecordObject(completeBtn, "Resize Complete Button");
            Undo.RecordObject(retakeBtn, "Resize Retake Button");
            
            // 높이 100으로 둘 다 고정! 넓이는 '처음으로' 버튼 기준!
            Vector2 newSize = new Vector2(completeBtn.sizeDelta.x, 100f);
            completeBtn.sizeDelta = newSize;
            retakeBtn.sizeDelta = newSize;
            
            // 여러 번 스크립트를 실행해도 간격이 계속 변하는 걸 막기 위해(멱등성),
            // 다시찍기 버튼의 기준점(Anchor, Pivot)을 '처음으로'의 기준점과 완전 똑같이 복제합니다.
            retakeBtn.anchorMin = completeBtn.anchorMin;
            retakeBtn.anchorMax = completeBtn.anchorMax;
            retakeBtn.pivot = completeBtn.pivot;
            
            // [처음으로] 버튼의 Y좌표에서 정확히 120px 위쪽으로 띄워서 배치 (버튼간 20px 여백)
            retakeBtn.anchoredPosition = new Vector2(completeBtn.anchoredPosition.x, completeBtn.anchoredPosition.y + 120f);
            
            EditorUtility.SetDirty(completeBtn);
            EditorUtility.SetDirty(retakeBtn);
            Debug.Log("✅ [Result] 버튼 높이를 100으로 맞추고, 두 버튼간 간격을 20px로 예쁘게 쌓아(Stack) QR 침범을 해결했습니다.");
        }
        else
        {
            Debug.LogWarning($"⚠️ [Result] 버튼 식별 실패: 다시찍기={retakeBtn!=null}, 처음으로={completeBtn!=null}");
        }

        // --- 폰트 억제 기능(Auto Size) 해제 및 강제적용 ---
        System.Action<RectTransform> ApplyLargeBoldText = (btnRt) => {
            if (btnRt == null) return;
            
            Text txt = btnRt.GetComponentInChildren<Text>(true);
            if (txt != null) {
                Undo.RecordObject(txt, "Change Font");
                txt.resizeTextForBestFit = false; // 사이즈 무시되는 현상 방지
                txt.fontSize = 28;
                txt.fontStyle = FontStyle.Bold;
                EditorUtility.SetDirty(txt);
            }
            var tmp = btnRt.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (tmp != null) {
                Undo.RecordObject(tmp, "Change Font");
                tmp.enableAutoSizing = false; // 사이즈 무시되는 현상 방지
                tmp.fontSize = 28;
                tmp.fontStyle = TMPro.FontStyles.Bold;
                EditorUtility.SetDirty(tmp);
            }
        };

        ApplyLargeBoldText(completeBtn);
        ApplyLargeBoldText(retakeBtn);

        appState.resultButtons = validButtons.ToArray();

        Transform existingCursor = resultPanel.transform.Find("ResultSelectCursor");
        if (existingCursor != null)
        {
            Undo.DestroyObjectImmediate(existingCursor.gameObject);
            Debug.Log("✅ [Result] 기존 ResultSelectCursor 삭제 완료");
        }
        
        if (validButtons.Count > 0)
        {
            GameObject cursorObj = new GameObject("ResultSelectCursor");
            cursorObj.transform.SetParent(resultPanel.transform, false);
            cursorObj.transform.SetAsLastSibling();

            RectTransform cRT = cursorObj.AddComponent<RectTransform>();
            // 버튼과 100% 동일하게 겹쳐지도록 생성
            cRT.anchorMin = validButtons[0].anchorMin;
            cRT.anchorMax = validButtons[0].anchorMax;
            cRT.pivot = validButtons[0].pivot;
            cRT.sizeDelta = validButtons[0].sizeDelta; // 팽창 금지

            Image cImg = cursorObj.AddComponent<Image>();
            cImg.color = new Color(0f, 0f, 0f, 0f); // 투명하게
            cImg.raycastTarget = false;

            // 테두리 오프셋을 0으로 설정하여 버튼 사각형에 정확히 꽉 채움
            CreateBorder(cursorObj, "TopBorder", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(40, 20), Vector2.zero);
            CreateBorder(cursorObj, "BottomBorder", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(40, 20), Vector2.zero);
            CreateBorder(cursorObj, "LeftBorder", new Vector2(0, 0), new Vector2(0, 1), new Vector2(0.0f, 0.5f), new Vector2(20, 0), Vector2.zero);
            CreateBorder(cursorObj, "RightBorder", new Vector2(1, 0), new Vector2(1, 1), new Vector2(1.0f, 0.5f), new Vector2(20, 0), Vector2.zero);

            appState.resultCursor = cRT;
            Debug.Log("✅ [Result] ResultSelectCursor 자동 생성 및 연결 완료");
            count++;
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3.6단계: 스탠바이 패널 세팅 (글자 색상 동기화)
    // ═══════════════════════════════════════════════════════════════════════════
    private static int SetupStandbyPanel(AppStateManager appState)
    {
        if (appState.blinkText != null)
        {
            Undo.RecordObject(appState.blinkText, "Update Blink Text Color");
            ColorUtility.TryParseHtmlString("#00FFFF", out Color c);
            appState.blinkText.color = c;
            
            // 기존에 하드코딩된 스타일(아웃라인 등)을 통일감 있게 정리
            if (appState.blinkText.fontSharedMaterial != null)
            {
                Material themeMat = new Material(appState.blinkText.fontSharedMaterial);
                themeMat.DisableKeyword("OUTLINE_ON");
                themeMat.EnableKeyword("UNDERLAY_ON");
                themeMat.SetFloat("_UnderlayOffsetX", 0.5f);
                themeMat.SetFloat("_UnderlayOffsetY", -0.5f);
                themeMat.SetFloat("_UnderlayDilate", 0f);
                themeMat.SetFloat("_UnderlaySoftness", 0.5f);
                themeMat.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.8f));
                appState.blinkText.fontSharedMaterial = themeMat;
            }
            
            EditorUtility.SetDirty(appState.blinkText);
            Debug.Log("✅ [Standby] 대기 화면 깜빡임 텍스트에 네온 테마(#00FFFF) 색상 적용 완료");
            return 1;
        }
        return 0;
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

        // 활성화된 ChromaKeyController 확인
        ChromaKeyController ckc = null;
        foreach (var c in Object.FindObjectsOfType<ChromaKeyController>())
        {
            if (c.gameObject.activeInHierarchy) { ckc = c; break; }
        }

        if (ckc == null)
        {
            Debug.LogWarning("⚠️ 활성화된 ChromaKeyController 컴포넌트를 찾을 수 없습니다.");
            warnCount++;
        }

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

    // ---- CreateTMP without font ----
    private static TextMeshProUGUI CreateTMP(GameObject parent, string objName,
        string text, int fontSize, FontStyle style, Color color,
        TextAlignmentOptions alignment,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
    {
        return CreateTMP(parent, objName, text, fontSize, style, color, alignment,
            anchorMin, anchorMax, pos, size, null);
    }

    // ---- CreateTMP with font ----
    private static TextMeshProUGUI CreateTMP(GameObject parent, string objName,
        string text, int fontSize, FontStyle style, Color color,
        TextAlignmentOptions alignment,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size,
        TMP_FontAsset font)
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
        if (font != null) tmp.font = font;

        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        return tmp;
    }

    // ---- CreateSlider with min/max/default/font ----
    private static Slider CreateSlider(GameObject parent, string objName, string labelText,
        float minValue, float maxValue, float defaultValue,
        Vector2 anchor, Vector2 pos, TMP_FontAsset font)
    {
        GameObject container = new GameObject(objName);
        container.transform.SetParent(parent.transform, false);

        RectTransform crt = container.AddComponent<RectTransform>();
        crt.anchorMin = anchor;
        crt.anchorMax = anchor;
        crt.anchoredPosition = pos;
        crt.sizeDelta = new Vector2(456, 42); // 기존 대비 각 20% 증가

        // 라벨
        GameObject labelObj = new GameObject("Label");
        labelObj.transform.SetParent(container.transform, false);
        var labelTMP = labelObj.AddComponent<TextMeshProUGUI>();
        labelTMP.text = labelText;
        labelTMP.fontSize = 15;
        labelTMP.color = new Color(0.92f, 0.92f, 0.92f);
        labelTMP.alignment = TextAlignmentOptions.Left;
        labelTMP.raycastTarget = false;
        if (font != null) labelTMP.font = font;
        RectTransform lrt = labelObj.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 0);
        lrt.anchorMax = new Vector2(0.22f, 1);
        lrt.sizeDelta = Vector2.zero;
        lrt.anchoredPosition = Vector2.zero;

        // 슬라이더
        GameObject sliderObj = DefaultControls.CreateSlider(new DefaultControls.Resources());
        sliderObj.name = "Slider";
        sliderObj.transform.SetParent(container.transform, false);
        RectTransform srt = sliderObj.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0.24f, 0.15f);
        srt.anchorMax = new Vector2(1f, 0.85f);
        srt.sizeDelta = Vector2.zero;
        srt.anchoredPosition = Vector2.zero;

        Slider slider = sliderObj.GetComponent<Slider>();
        slider.minValue = minValue;
        slider.maxValue = maxValue;
        slider.value = defaultValue;

        return slider;
    }

    // ---- FindKoreanFont ----
    private static TMP_FontAsset FindKoreanFont()
    {
        string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fn = Path.GetFileNameWithoutExtension(path).ToLower();
            if (fn.Contains("noto") || fn.Contains("cjk") || fn.Contains("korean") ||
                fn.Contains("pretendard") || fn.Contains("kopub") || fn.Contains("nanum") ||
                fn.Contains("malgun") || fn.Contains("spoqa"))
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null)
                {
                    Debug.Log($"✅ [Font] 한글 폰트 발견: {path}");
                    return font;
                }
            }
        }
        Debug.LogWarning("⚠️ [Font] 한글 폰트를 찾지 못했습니다. 기본 폰트를 사용합니다.");
        return null;
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

    private static void CreateBorder(GameObject parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Vector2 anchoredPosOffset)
    {
        GameObject borderObj = new GameObject(name);
        borderObj.transform.SetParent(parent.transform, false);
        
        RectTransform rt = borderObj.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.sizeDelta = sizeDelta;
        rt.anchoredPosition = anchoredPosOffset;
        
        Image img = borderObj.AddComponent<Image>();
        img.color = new Color(0f, 1f, 1f, 1f); // 네온 청록색 (#00FFFF)
        img.raycastTarget = false;
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
