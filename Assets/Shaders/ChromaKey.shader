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

        // ── Branch B: Spill Removal ──────────────────────────────────────────
        _SpillRemoval   ("Spill Removal",    Range(0.0,  1.0)) = 0.15

        // ── Branch B: Color Grading ──────────────────────────────────────────
        _Brightness     ("Brightness",       Range(-1.0, 1.0)) = 0.0
        _Contrast       ("Contrast",         Range(0.0,  3.0)) = 1.0
        _Saturation     ("Saturation",       Range(0.0,  2.0)) = 1.0
        _Hue            ("Hue (degrees)",    Range(-180, 180)) = 0.0

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
            #pragma target 2.0

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── 프로퍼티 바인딩 ────────────────────────────────────────────────
            sampler2D _MainTex;
            float4    _MainTex_ST;

            fixed4  _TargetColor;
            float   _Sensitivity;
            float   _Smoothness;
            float   _SpillRemoval;
            float   _Brightness;
            float   _Contrast;
            float   _Saturation;
            float   _Hue;

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
            // 인물 테두리에 번진 배경색(초록빛)을 중립 톤으로 교정
            float3 RemoveSpill(float3 rgb, float3 keyColor, float strength)
            {
                // 키 색상의 주 채널 강도 계산
                float keyMax = max(keyColor.r, max(keyColor.g, keyColor.b));
                float keyMin = min(keyColor.r, min(keyColor.g, keyColor.b));

                // 픽셀에서 키 색상이 dominating 채널에 해당하는 부분만 제거
                float spillR = max(0.0, rgb.r - (rgb.r + rgb.b) * 0.5 * step(keyColor.r, keyColor.g));
                float spillG = max(0.0, rgb.g - max(rgb.r, rgb.b));
                float spillB = max(0.0, rgb.b - (rgb.r + rgb.g) * 0.5 * step(keyColor.b, keyColor.g));

                // 그린 키에 최적화: G 채널 억제
                float gSpill = max(0.0, rgb.g - max(rgb.r, rgb.b));
                float3 corrected = rgb;
                corrected.g = corrected.g - gSpill * strength;

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
                OUT.color    = v.color;
                return OUT;
            }

            // ── 프래그먼트 셰이더 ──────────────────────────────────────────────
            fixed4 frag(v2f IN) : SV_Target
            {
                half4 texColor = tex2D(_MainTex, IN.texcoord);
                float3 keyColor = _TargetColor.rgb;

                // ==============================================================
                //  [단계 1] Branch A — Alpha Path
                //  크로마키 연산 → Alpha Mask 추출
                //  인물(키 색상과 다른 영역)은 불투명, 배경은 투명
                // ==============================================================

                // 루미넌스 분리 후 크로마 거리 계산 (루미넌스 변화에 덜 민감)
                float3 diff      = texColor.rgb - keyColor;
                float  lumKey    = dot(keyColor, float3(0.2126, 0.7152, 0.0722));
                float  lumTex    = dot(texColor.rgb, float3(0.2126, 0.7152, 0.0722));
                float3 chromaDiff = diff - (lumTex - lumKey) * float3(0.2126, 0.7152, 0.0722);
                float  chromaDist = length(chromaDiff);

                float alpha = smoothstep(
                    _Sensitivity - _Smoothness,
                    _Sensitivity + _Smoothness,
                    chromaDist
                );

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
