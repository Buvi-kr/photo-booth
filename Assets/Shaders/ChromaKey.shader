// =============================================================================
//  ChromaKey.shader
//  포천아트밸리 천문과학관 무인 포토부스 — 이원화 크로마키 셰이더
//
//  명세서 3.2 셰이더 파이프라인 이원화:
//  ├── 분기 A (Alpha Path):  원본 → 크로마키 연산 → Alpha Mask 추출
//  └── 분기 B (RGB Path):   원본 → 반사광 제거(Spill) → 색상 보정 → RGB 추출
//  최종 병합: 보정된 RGB + 추출된 Alpha Mask → 인물 보정이 알파에 영향 없음
//
//  명세서 4 셰이더 연산 우선순위:
//  [1] Sensitivity/Smoothness → 배경 투명도
//  [2] Spill Removal → 테두리 초록빛 제거
//  [3] Brightness/Contrast/Saturation/Hue → 톤앤매너
//  [4] 최종 병합 출력
// =============================================================================

Shader "PhotoBooth/ChromaKey"
{
    Properties
    {
        _MainTex        ("Webcam Texture",   2D)           = "white" {}

        // ── Branch A: Alpha Path ─────────────────────────────────────────────
        _TargetColor    ("Target Color",     Color)        = (0, 0.694, 0.251, 1)
        _Sensitivity    ("Sensitivity",      Range(0.01, 1.0)) = 0.35
        _Smoothness     ("Smoothness",       Range(0.0,  0.5)) = 0.08
        _LumaWeight     ("Luma Weight",      Range(0.0,  1.0)) = 0.0
        _EdgeChoke      ("Edge Choke",       Range(0.0,  0.5)) = 0.0
        _PreBlur        ("Pre Blur (px)",    Range(0.0,  5.0)) = 0.0

        // ── Branch B: Spill Removal ──────────────────────────────────────────
        _SpillRemoval   ("Spill Removal",    Range(0.0,  1.0)) = 0.15

        // ── Branch B: Color Grading ──────────────────────────────────────────
        _Brightness     ("Brightness",       Range(-1.0, 1.0)) = 0.0
        _Contrast       ("Contrast",         Range(0.0,  3.0)) = 1.0
        _Saturation     ("Saturation",       Range(0.0,  2.0)) = 1.0
        _Hue            ("Hue (degrees)",    Range(-180, 180)) = 0.0
        _CaptureRotation("Capture Rotation (rad)", Float)      = 0.0

        // ── 쳨치 크롭 (UV 기준): 기본값 = 전체 표시 (잘림 없음) ─────────────────────────────
        // (left_uv, bottom_uv, right_uv_end, top_uv_end)  기본=-0.1~1.1 (no-crop)
        _CropRect       ("Capture Crop Rect",  Vector)         = (-0.1, -0.1, 1.1, 1.1)
        // (fadeX_uv, fadeY_uv)  경계 페이드 폭
        _CropFade       ("Capture Crop Fade",  Vector)         = (0.001, 0.001, 0, 0)

        // ── UI 스텐실 (Unity UI 표준) ────────────────────────────────────────
        _StencilComp    ("Stencil Comparison", Float) = 8
        _Stencil        ("Stencil ID",         Float) = 0
        _StencilOp      ("Stencil Operation",  Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask  ("Stencil Read Mask",  Float) = 255
        _ColorMask      ("Color Mask",         Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType"      = "Transparent"
            "PreviewType"     = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref      [_Stencil]
            Comp     [_StencilComp]
            Pass     [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull     Off
        Lighting Off
        ZWrite   Off
        ZTest    [unity_GUIZTestMode]
        Blend    SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "ChromaKeyPass"

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #pragma multi_compile __ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            // ── 버텍스 입출력 ──────────────────────────────────────────────────
            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex      : SV_POSITION;
                fixed4 color       : COLOR;
                float2 texcoord    : TEXCOORD0;
                float4 worldPos    : TEXCOORD1;
                float2 screenUV    : TEXCOORD2; // raw 바르는 UV (crop 마스크용, TRANSFORM_TEX 미적용)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── 프로퍼티 바인딩 ────────────────────────────────────────────────
            sampler2D _MainTex;
            float4    _MainTex_ST;
            float4    _MainTex_TexelSize;

            fixed4  _TargetColor;
            float   _Sensitivity;
            float   _Smoothness;
            float   _LumaWeight;
            float   _EdgeChoke;
            float   _PreBlur;
            float   _SpillRemoval;
            float   _Brightness;
            float   _Contrast;
            float   _Saturation;
            float   _Hue;
            float   _CaptureRotation;
            float4  _CropRect;  // (leftUV, bottomUV, rightUV_end, topUV_end) 컴구로 = no-crop
            float4  _CropFade;  // (fadeX_uv, fadeY_uv, 0, 0)

            float4  _ClipRect;
            bool    _UseClipRect;

            // ── 헬퍼: RGB ↔ HSV 변환 ──────────────────────────────────────────
            float3 RGBtoHSV(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float  d = q.x - min(q.w, q.y);
                float  e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HSVtoRGB(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            // ── 헬퍼: Spill Removal ────────────────────────────────────────────
            // 인물 테두리에 번진 배경색(초록빛 등)을 중립 톤으로 교정
            float3 RemoveSpill(float3 rgb, float3 keyColor, float strength)
            {
                float keyMax = max(keyColor.r, max(keyColor.g, keyColor.b));
                float3 corrected = rgb;

                if (keyMax == keyColor.g) {
                    float excess = max(0.0, rgb.g - max(rgb.r, rgb.b));
                    corrected.g -= excess * strength;
                } else if (keyMax == keyColor.b) {
                    float excess = max(0.0, rgb.b - max(rgb.r, rgb.g));
                    corrected.b -= excess * strength;
                } else {
                    float excess = max(0.0, rgb.r - max(rgb.g, rgb.b));
                    corrected.r -= excess * strength;
                }

                return saturate(corrected);
            }

            // ── 버텍스 셰이더 ──────────────────────────────────────────────────
            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.worldPos = v.vertex;
                OUT.vertex   = UnityObjectToClipPos(OUT.worldPos);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.screenUV = v.texcoord; // TRANSFORM_TEX 전의 원본 UV → 쳨치 컨킬링용
                OUT.color    = v.color;
                return OUT;
            }

            // ── 프래그먼트 셰이더 ──────────────────────────────────────────────
            fixed4 frag(v2f IN) : SV_Target
            {
                // _CaptureRotation != 0 이면 UV를 중심 기준으로 회전 (캡처용)
                float2 uv = IN.texcoord;
                if (abs(_CaptureRotation) > 0.0001)
                {
                    float2 uv_c = uv - 0.5;
                    float  cosR = cos(_CaptureRotation);
                    float  sinR = sin(_CaptureRotation);
                    uv_c = float2(uv_c.x * cosR - uv_c.y * sinR,
                                  uv_c.x * sinR + uv_c.y * cosR);
                    uv = uv_c + 0.5;
                }
                half4 texColor = tex2D(_MainTex, uv);
                float3 keyColor = _TargetColor.rgb;

                // ==============================================================
                //  [단계 0] Screen Pre-blur (Chroma Blur)
                //  5-Tap Gaussian, 정규화 가중치 합 = 1.0
                //  가중치: 중심=0.4026, ±x/±y=0.1493 each → 합 0.4026+4*0.1493=1.0
                //  PreBlur=0 → t=(0,0) → 모든 샘플 동일 픽셀 → 결과 = 원본
                // ==============================================================
                float2 t = _MainTex_TexelSize.xy * _PreBlur;
                float3 g0 = texColor.rgb;
                float3 g1 = tex2D(_MainTex, IN.texcoord + float2( t.x,  0)).rgb;
                float3 g2 = tex2D(_MainTex, IN.texcoord + float2(-t.x,  0)).rgb;
                float3 g3 = tex2D(_MainTex, IN.texcoord + float2( 0,  t.y)).rgb;
                float3 g4 = tex2D(_MainTex, IN.texcoord + float2( 0, -t.y)).rgb;
                // 0.4026 + 4*0.1493 = 0.4026 + 0.5972 = 0.9998 ≈ 1.0
                float3 blurredColor = g0 * 0.4026 + (g1 + g2 + g3 + g4) * 0.1493;

                // ==============================================================
                //  [단계 1] Branch A — Alpha Path
                //  크로마키 연산 → Alpha Mask 추출
                //  인물(키 색상과 다른 영역)은 불투명, 배경은 투명
                // ==============================================================

                // 루미넌스 분리 후 크로마 거리 계산 (LumaWeight에 따라 명도 반영률 결정)
                // 블러 처리된 색상을 사용하여 마스크(알파)의 깍두기 현상을 원천 차단
                float3 diff      = blurredColor - keyColor;
                float  lumKey    = dot(keyColor, float3(0.2126, 0.7152, 0.0722));
                float  lumTex    = dot(blurredColor, float3(0.2126, 0.7152, 0.0722));
                float3 chromaDiff = diff - (lumTex - lumKey) * float3(0.2126, 0.7152, 0.0722) * (1.0 - _LumaWeight);
                float  chromaDist = length(chromaDiff);

                // ==============================================================
                //  [단계 1b] 알파 멀티탭 블러 (fwidth 대신)
                //  이웃 4곳의 알파를 raw 1-샘플로 독립 계산 후 Gaussian 평균
                //  ─ 진짜 윤곽선: 이웃이 0과 1을 교차 → 평균 = 부드러운 전환
                //  ─ 4:2:2 블록 경계: 색상 노이즈가 알파에 주는 영향을 평균이 상쇄
                //  중심=0.4026(5-tap PreBlur 결과), 이웃 4개=raw 1-샘플
                //  가중치 합: 0.4026 + 4×0.1493 ≈ 1.0
                // ==============================================================
                float edgeLow  = _Sensitivity + _EdgeChoke;
                float edgeHigh = _Sensitivity + _EdgeChoke + _Smoothness;
                float alpha0   = smoothstep(edgeLow, edgeHigh, chromaDist);

                // 2텍셀 고정 오프셋: 4:2:2 크로마 블록(가로 2px) 경계를 확실히 넘김
                float2 ao = _MainTex_TexelSize.xy * 2.0;

                // ± X 이웃
                float3 raw1  = tex2D(_MainTex, IN.texcoord + float2( ao.x, 0)).rgb;
                float3 cd1   = (raw1 - keyColor) - (dot(raw1, float3(0.2126,0.7152,0.0722)) - lumKey) * float3(0.2126,0.7152,0.0722) * (1.0 - _LumaWeight);
                float alpha1 = smoothstep(edgeLow, edgeHigh, length(cd1));

                float3 raw2  = tex2D(_MainTex, IN.texcoord + float2(-ao.x, 0)).rgb;
                float3 cd2   = (raw2 - keyColor) - (dot(raw2, float3(0.2126,0.7152,0.0722)) - lumKey) * float3(0.2126,0.7152,0.0722) * (1.0 - _LumaWeight);
                float alpha2 = smoothstep(edgeLow, edgeHigh, length(cd2));

                // ± Y 이웃
                float3 raw3  = tex2D(_MainTex, IN.texcoord + float2(0,  ao.y)).rgb;
                float3 cd3   = (raw3 - keyColor) - (dot(raw3, float3(0.2126,0.7152,0.0722)) - lumKey) * float3(0.2126,0.7152,0.0722) * (1.0 - _LumaWeight);
                float alpha3 = smoothstep(edgeLow, edgeHigh, length(cd3));

                float3 raw4  = tex2D(_MainTex, IN.texcoord + float2(0, -ao.y)).rgb;
                float3 cd4   = (raw4 - keyColor) - (dot(raw4, float3(0.2126,0.7152,0.0722)) - lumKey) * float3(0.2126,0.7152,0.0722) * (1.0 - _LumaWeight);
                float alpha4 = smoothstep(edgeLow, edgeHigh, length(cd4));

                // Gaussian 가중 평균
                float alpha = alpha0 * 0.4026 + (alpha1 + alpha2 + alpha3 + alpha4) * 0.1493;

                // ==============================================================
                //  [단계 1c] 캡처 크롭 알파 마스크
                //  screenUV (TRANSFORM_TEX 미적용, 0-1 화면 좌표)를 기준으로
                //  _CropRect 외부를 alpha=0, 경계를 smoothstep으로 페이드
                //  기본값 _CropRect=(-0.1,-0.1,1.1,1.1) → 전 영역 통과 (실시간 뷰 영향 없음)
                //  각 방향별: smoothstep(0, fade, UV_from_edge) - UV_from_edge<0이면 0
                // ==============================================================
                float2 cropFadeSafe = max(_CropFade.xy, 0.001);
                float cropMask =
                    smoothstep(0.0, cropFadeSafe.x, IN.screenUV.x - _CropRect.x) *  // Left
                    smoothstep(0.0, cropFadeSafe.x, _CropRect.z   - IN.screenUV.x) * // Right
                    smoothstep(0.0, cropFadeSafe.y, IN.screenUV.y - _CropRect.y) *  // Bottom
                    smoothstep(0.0, cropFadeSafe.y, _CropRect.w   - IN.screenUV.y); // Top
                alpha *= saturate(cropMask);

                // ==============================================================
                //  [단계 2] Branch B — Spill Removal (RGB Path)
                //  인물 테두리의 초록빛 반사광 제거
                // ==============================================================
                float3 rgb = RemoveSpill(texColor.rgb, keyColor, _SpillRemoval);

                // ==============================================================
                //  [단계 3] Branch B — Color Grading (RGB Path)
                //  배경별 톤앤매너 보정 (Alpha에 영향 없음)
                // ==============================================================

                // Brightness: 선형 가산
                rgb = saturate(rgb + _Brightness);

                // Contrast: 0.5 기준 스케일
                rgb = saturate((rgb - 0.5) * _Contrast + 0.5);

                // Saturation: 루미넌스로 보간
                float lum = dot(rgb, float3(0.2126, 0.7152, 0.0722));
                rgb = lerp(float3(lum, lum, lum), rgb, _Saturation);

                // Hue Rotation: HSV 공간에서 회전
                if (abs(_Hue) > 0.5)
                {
                    float3 hsv = RGBtoHSV(rgb);
                    hsv.x      = frac(hsv.x + _Hue / 360.0);
                    rgb        = HSVtoRGB(hsv);
                }

                // ==============================================================
                //  [단계 4] 최종 병합
                //  Branch B 의 RGB + Branch A 의 Alpha → 인물 보정이 알파 무영향
                // ==============================================================
                half4 color = half4(rgb, alpha) * IN.color;

                // Unity UI 클리핑 마스크 적용
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPos.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
            ENDHLSL
        }
    }

    FallBack "UI/Default"
}
