// =============================================================================
//  PhotoBoothData.cs
//  포천아트밸리 천문과학관 무인 포토부스 — 데이터 드리븐 설정 모델
//
//  ▪ config.json 과 1:1 직렬화 매핑 (Newtonsoft.Json)
//  ▪ 에디터 재빌드 없이 config.json 수정만으로 신규 배경 대응 가능
//  ▪ [Serializable] → Unity Inspector 디버그 뷰 지원
//
//  [구조 요약]
//  PhotoBoothConfig (Root)
//  ├── GlobalChromaConfig       (Global) 물리 환경 마스터 설정 — 모든 배경의 Fallback
//  └── List<BackgroundConfig>   (Local)  배경별 개별 설정 — 마스터 상속 또는 Override
//       ├── CropConfig          왜곡 없는 진짜 자르기 (Pixel 단위)
//       ├── TransformConfig     Zoom / Move XY
//       ├── ColorGradingConfig  Brightness / Contrast / Saturation / Hue
//       └── LocalChromaConfig   Override 스위치 + 전용 크로마 3종 세트
// =============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  Root — config.json 최상위
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// config.json 루트 객체.
/// PhotoBoothConfigLoader 가 이 클래스로 역직렬화한다.
/// </summary>
[Serializable]
public class PhotoBoothConfig
{
    /// <summary>
    /// [Global] 물리 환경(포천아트밸리 현장) 크로마키 마스터 설정.
    /// 모든 배경의 기본값(Fallback)으로 동작.
    /// </summary>
    [JsonProperty("global")]
    public GlobalChromaConfig Global { get; set; } = new GlobalChromaConfig();

    /// <summary>
    /// 웹캠 입력 해상도 및 장치 설정.
    /// </summary>
    [JsonProperty("camera")]
    public CameraConfig Camera { get; set; } = new CameraConfig();

    /// <summary>
    /// [Local] 배경별 개별 설정 배열.
    /// 각 항목의 bgName 을 ID 로 사용.
    /// </summary>
    [JsonProperty("backgrounds")]
    public List<BackgroundConfig> Backgrounds { get; set; } = new List<BackgroundConfig>();

    // ── 편의 메서드 ───────────────────────────────────────────────────────────

    /// <summary>bgName 으로 BackgroundConfig 를 검색한다. 없으면 null 반환.</summary>
    public BackgroundConfig FindByName(string bgName)
    {
        if (string.IsNullOrEmpty(bgName)) return null;
        return Backgrounds?.Find(b => b.BgName == bgName);
    }

    /// <summary>인덱스로 BackgroundConfig 를 안전하게 가져온다. 범위 초과 시 null 반환.</summary>
    public BackgroundConfig GetByIndex(int index)
    {
        if (Backgrounds == null || index < 0 || index >= Backgrounds.Count) return null;
        return Backgrounds[index];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Global] 웹캠 시스템 설정
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 웹캠 해상도 및 장치 관련 설정.
/// 4K 다이렉트 신호를 받을지 등의 최적화 스펙을 JSON에서 명시적으로 제어.
/// </summary>
[Serializable]
public class CameraConfig
{
    /// <summary>true면 기본 카메라 사용, false면 DeviceName의 카메라를 찾음.</summary>
    [JsonProperty("useDefaultDevice")]
    public bool UseDefaultDevice { get; set; } = true;

    /// <summary>연결할 웹캠의 이름(포함된 문자열). 기본값은 자동(빈 문자열).</summary>
    [JsonProperty("deviceName")]
    public string DeviceName { get; set; } = "";

    /// <summary>요청 가로 해상도 (권장: 4K=3840, FHD=1920).</summary>
    [JsonProperty("requestedWidth")]
    public int RequestedWidth { get; set; } = 3840;

    /// <summary>요청 세로 해상도 (권장: 4K=2160, FHD=1080).</summary>
    [JsonProperty("requestedHeight")]
    public int RequestedHeight { get; set; } = 2160;

    /// <summary>요청 FPS (권장: 30 또는 60).</summary>
    [JsonProperty("requestedFPS")]
    public int RequestedFPS { get; set; } = 30;
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Global] 물리 환경 마스터 설정
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 현장에 설치된 크로마키 부스의 고정 환경값.
/// useLocalChroma = false 인 모든 배경에 공통 적용되는 Fallback 값이다.
/// </summary>
[Serializable]
public class GlobalChromaConfig
{
    /// <summary>
    /// 크로마키 배경 천의 기준 색상. Hex 문자열로 저장 (예: "#00B140").
    /// 런타임에서는 GetTargetColor() 로 Unity Color 로 변환해 사용.
    /// </summary>
    [JsonProperty("targetColor")]
    public string TargetColor { get; set; } = "#00B140";

    /// <summary>전역 배경 제거 민감도. 값이 클수록 더 넓은 색상 범위를 제거한다. (권장: 0.1~1.0)</summary>
    [JsonProperty("masterSensitivity")]
    public float MasterSensitivity { get; set; } = 0.35f;

    /// <summary>전역 경계선 안티앨리어싱 강도. 값이 클수록 가장자리가 부드러워진다. (권장: 0.0~0.5)</summary>
    [JsonProperty("masterSmoothness")]
    public float MasterSmoothness { get; set; } = 0.08f;

    /// <summary>전역 반사광 제거 강도. 인물 테두리의 초록빛(Spill)을 제거한다. (권장: 0.0~1.0)</summary>
    [JsonProperty("masterSpillRemoval")]
    public float MasterSpillRemoval { get; set; } = 0.15f;

    // ── 변환 헬퍼 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TargetColor 문자열(Hex)을 Unity Color 로 변환.
    /// 파싱 실패 시 경고 로그를 출력하고 Color.green 을 반환한다.
    /// </summary>
    public Color GetTargetColor()
    {
        if (ColorUtility.TryParseHtmlString(TargetColor, out Color parsed))
            return parsed;

        Debug.LogWarning($"[PhotoBoothConfig] targetColor 파싱 실패: '{TargetColor}'. 기본값 Color.green 적용.");
        return Color.green;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Local] 배경별 개별 설정 (ID: bgName)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 배경 하나에 대한 모든 설정을 담는 컨테이너.
/// bgName 이 식별자(ID) 역할을 한다.
/// </summary>
[Serializable]
public class BackgroundConfig
{
    /// <summary>배경 고유 식별자. 파일명 기반 권장 (예: "space_nebula", "aurora_01").</summary>
    [JsonProperty("bgName")]
    public string BgName { get; set; } = "";

    /// <summary>[Crop] 왜곡 없는 픽셀 단위 자르기 설정.</summary>
    [JsonProperty("crop")]
    public CropConfig Crop { get; set; } = new CropConfig();

    /// <summary>[Transform] 화면 내 Zoom 및 위치 이동 설정.</summary>
    [JsonProperty("transform")]
    public TransformConfig Transform { get; set; } = new TransformConfig();

    /// <summary>[Color] 인물 톤앤매너 보정 설정 (셰이더 분기 B — RGB Path, 단계 3).</summary>
    [JsonProperty("color")]
    public ColorGradingConfig Color { get; set; } = new ColorGradingConfig();

    /// <summary>[Chroma Override] 마스터 값 상속 여부 및 배경 전용 크로마 3종 세트.</summary>
    [JsonProperty("chroma")]
    public LocalChromaConfig Chroma { get; set; } = new LocalChromaConfig();

    // ── Override Logic 헬퍼 (명세서 2.2 핵심 로직) ───────────────────────────

    /// <summary>
    /// 실제 적용할 Sensitivity 를 반환한다.
    /// useLocalChroma=false → Global.MasterSensitivity 상속 (Fallback)
    /// useLocalChroma=true  → Chroma.LocalSensitivity 강제 적용 (Override)
    /// </summary>
    public float GetEffectiveSensitivity(GlobalChromaConfig global) =>
        Chroma.UseLocalChroma ? Chroma.LocalSensitivity : global.MasterSensitivity;

    /// <summary>
    /// 실제 적용할 Smoothness 를 반환한다.
    /// useLocalChroma=false → Global.MasterSmoothness 상속 (Fallback)
    /// useLocalChroma=true  → Chroma.LocalSmoothness 강제 적용 (Override)
    /// </summary>
    public float GetEffectiveSmoothness(GlobalChromaConfig global) =>
        Chroma.UseLocalChroma ? Chroma.LocalSmoothness : global.MasterSmoothness;

    /// <summary>
    /// 실제 적용할 SpillRemoval 을 반환한다.
    /// useLocalChroma=false → Global.MasterSpillRemoval 상속 (Fallback)
    /// useLocalChroma=true  → Chroma.LocalSpillRemoval 강제 적용 (Override)
    /// </summary>
    public float GetEffectiveSpillRemoval(GlobalChromaConfig global) =>
        Chroma.UseLocalChroma ? Chroma.LocalSpillRemoval : global.MasterSpillRemoval;
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Crop] 왜곡 없는 진짜 자르기 — Pixel 단위
//  명세서 3.1: Pixel → 비율(0~1) 정규화, uvRect + sizeDelta 1:1 동기화
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 웹캠 영상의 상/하/좌/우 픽셀 단위 크롭 설정.
/// 런타임에서는 현재 웹캠 해상도 대비 비율(0~1)로 정규화 변환 후
/// RawImage.uvRect 와 RectTransform.sizeDelta 에 1:1 동기화하여 찌그러짐을 차단한다.
/// </summary>
[Serializable]
public class CropConfig
{
    /// <summary>위쪽 자르기 (px). 양수일수록 아래 영역만 표시.</summary>
    [JsonProperty("top")]
    public int Top { get; set; } = 0;

    /// <summary>아래쪽 자르기 (px). 양수일수록 위 영역만 표시.</summary>
    [JsonProperty("bottom")]
    public int Bottom { get; set; } = 0;

    /// <summary>왼쪽 자르기 (px). 양수일수록 오른쪽 영역만 표시.</summary>
    [JsonProperty("left")]
    public int Left { get; set; } = 0;

    /// <summary>오른쪽 자르기 (px). 양수일수록 왼쪽 영역만 표시.</summary>
    [JsonProperty("right")]
    public int Right { get; set; } = 0;

    /// <summary>모든 크롭 값이 0인지 확인. true 이면 크롭 연산 스킵 가능.</summary>
    [JsonIgnore]
    public bool IsIdentity => Top == 0 && Bottom == 0 && Left == 0 && Right == 0;
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Transform] 화면 내 인물 배치 제어
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 크롭 후 인물의 확대/축소 및 위치 이동 설정.
/// uvRect 의 width/height/x/y 조절과 연동된다.
/// </summary>
[Serializable]
public class TransformConfig
{
    /// <summary>확대 배율. 1.0 = 원본 크기. 값이 클수록 인물이 확대된다. (권장: 0.5~3.0)</summary>
    [JsonProperty("zoom")]
    public float Zoom { get; set; } = 1.0f;

    /// <summary>수평 위치 이동. 양수 = 오른쪽. 정규화 단위(0~1 기준). (권장: -0.5~0.5)</summary>
    [JsonProperty("moveX")]
    public float MoveX { get; set; } = 0.0f;

    /// <summary>수직 위치 이동. 양수 = 위쪽. 정규화 단위(0~1 기준). (권장: -0.5~0.5)</summary>
    [JsonProperty("moveY")]
    public float MoveY { get; set; } = 0.0f;

    /// <summary>회전 각도. 도(degree) 단위. 양수 = 시계 방향. (권장: -180~180)</summary>
    [JsonProperty("rotation")]
    public float Rotation { get; set; } = 0.0f;

    /// <summary>모든 값이 기본값인지 확인. true 이면 Transform 연산 스킵 가능.</summary>
    [JsonIgnore]
    public bool IsIdentity => Mathf.Approximately(Zoom, 1.0f) &&
                              Mathf.Approximately(MoveX, 0.0f) &&
                              Mathf.Approximately(MoveY, 0.0f) &&
                              Mathf.Approximately(Rotation, 0.0f);
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Color] 배경별 인물 톤앤매너 보정
//  셰이더 파이프라인 분기 B (RGB Path) — 단계 3 적용
//  Alpha 마스크(분기 A)에는 절대 영향을 주지 않음
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 인물의 밝기/대비/채도/색조를 배경에 맞게 보정하는 컬러 그레이딩 설정.
/// 셰이더 분기 B (RGB Path) 의 단계 3에서만 동작 — Alpha Mask에 영향 없음.
/// </summary>
[Serializable]
public class ColorGradingConfig
{
    /// <summary>밝기 보정. 0.0 = 원본. 양수 = 밝게, 음수 = 어둡게. (권장: -1.0~1.0)</summary>
    [JsonProperty("brightness")]
    public float Brightness { get; set; } = 0.0f;

    /// <summary>대비 보정. 1.0 = 원본. 낮으면 플랫, 높으면 강한 대비. (권장: 0.0~3.0)</summary>
    [JsonProperty("contrast")]
    public float Contrast { get; set; } = 1.0f;

    /// <summary>채도 보정. 1.0 = 원본. 0.0 = 흑백, 2.0 = 과채도. (권장: 0.0~2.0)</summary>
    [JsonProperty("saturation")]
    public float Saturation { get; set; } = 1.0f;

    /// <summary>색조 회전 (도, degree). 0.0 = 변화 없음. (범위: -180~180)</summary>
    [JsonProperty("hue")]
    public float Hue { get; set; } = 0.0f;

    /// <summary>모든 값이 기본값인지 확인. true 이면 ColorGrading 연산 스킵 가능.</summary>
    [JsonIgnore]
    public bool IsIdentity => Mathf.Approximately(Brightness, 0.0f) &&
                              Mathf.Approximately(Contrast,   1.0f) &&
                              Mathf.Approximately(Saturation, 1.0f) &&
                              Mathf.Approximately(Hue,        0.0f);
}

// ─────────────────────────────────────────────────────────────────────────────
//  [Chroma Override] 배경별 크로마키 개별 설정
//  useLocalChroma = false (기본) → Global 마스터 값 상속
//  useLocalChroma = true         → 아래 Local 3종 세트 강제 적용
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 배경별 크로마키 Override 설정.
/// <br/>false(기본): GlobalChromaConfig 의 Master 3종 값을 그대로 상속.
/// <br/>true(활성화): Master 값을 완전히 무시하고 Local 3종 세트를 강제 적용.
/// 특수 조명/색상 배경 등 Global Fallback 이 맞지 않는 경우에 사용.
/// </summary>
[Serializable]
public class LocalChromaConfig
{
    /// <summary>
    /// true  = Master 설정 무시, Local 3종 세트 강제 적용.
    /// false = Global 마스터 상속 (기본값 Fallback).
    /// </summary>
    [JsonProperty("useLocalChroma")]
    public bool UseLocalChroma { get; set; } = false;

    /// <summary>배경 전용 민감도. UseLocalChroma=true 일 때만 유효. (권장: 0.1~1.0)</summary>
    [JsonProperty("localSensitivity")]
    public float LocalSensitivity { get; set; } = 0.35f;

    /// <summary>배경 전용 경계선 부드러움. UseLocalChroma=true 일 때만 유효. (권장: 0.0~0.5)</summary>
    [JsonProperty("localSmoothness")]
    public float LocalSmoothness { get; set; } = 0.08f;

    /// <summary>배경 전용 반사광 제거. UseLocalChroma=true 일 때만 유효. (권장: 0.0~1.0)</summary>
    [JsonProperty("localSpillRemoval")]
    public float LocalSpillRemoval { get; set; } = 0.15f;
}
