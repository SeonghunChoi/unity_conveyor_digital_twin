// 트윈 설비용 URP 셰이더: 기본 조명 + 상태 하이라이트 + 벨트 표면 흐름.
//
// - 상태 하이라이트: 스크립트(TwinVisuals)가 노드별 머티리얼 인스턴스의 _Highlight* 값을 바꾼다.
//   맥동 발광 + 가장자리 림으로 "어느 부품이 문제인지"를 멀리서도 보이게 한다.
// - 오프라인: _Desaturate로 회색조로 바꿔 "지금 보이는 값을 믿을 수 없음"을 표현한다.
// - 벨트 흐름 (_BELT_FLOW): CAD 메시에 UV가 없으므로, 오브젝트 공간 위치에서 벨트 루프를 따라 잰 거리 s를 계산해
//   줄무늬를 그린다. 윗면(입구→출구) → 구동 드럼 → 아랫면(출구→입구) → 아이들 드럼 순으로 s가 이어진다.
//   _BeltOffset은 재생 시계로 적분한 벨트 이동 거리(m)라서 박스와 벨트 표면이 같은 속도로 움직인다.
Shader "ConveyorTwin/StatusLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.7, 0.7, 0.7, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.4
        _Metallic ("Metallic", Range(0, 1)) = 0.0

        [Header(Status Highlight)]
        [HDR] _HighlightColor ("Highlight Color", Color) = (1, 0.2, 0.1, 1)
        _HighlightStrength ("Highlight Strength", Range(0, 1)) = 0
        _PulseHz ("Pulse Hz (0 = steady)", Float) = 0
        _Desaturate ("Desaturate (offline)", Range(0, 1)) = 0

        [Header(Belt Flow)]
        [Toggle(_BELT_FLOW)] _BeltFlow ("Belt Flow", Float) = 0
        _StripeColor ("Stripe Color", Color) = (0.05, 0.05, 0.05, 1)
        _StripeSpacing ("Stripe Spacing (m)", Float) = 0.12
        _StripeWidth ("Stripe Width (0-1)", Range(0.02, 0.9)) = 0.2
        _BeltOffset ("Belt Offset (m)", Float) = 0
        _BeltCenterY ("Drum Axis Y (object)", Float) = 0
        _BeltTailZ ("Tail Drum Z (object)", Float) = -1
        _BeltHeadZ ("Head Drum Z (object)", Float) = 1
        _BeltRadius ("Belt Outer Radius (m)", Float) = 0.0275
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // SRP Batcher 호환: 머티리얼 속성은 모두 UnityPerMaterial에
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness;
            half _Metallic;
            half4 _HighlightColor;
            half _HighlightStrength;
            float _PulseHz;
            half _Desaturate;
            half4 _StripeColor;
            float _StripeSpacing;
            half _StripeWidth;
            float _BeltOffset;
            float _BeltCenterY;
            float _BeltTailZ;
            float _BeltHeadZ;
            float _BeltRadius;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            // multi_compile: 벨트 머티리얼은 런타임(TwinVisuals)에 키워드를 켜므로, shader_feature로 두면
            // 빌드에 그 키워드를 쓰는 머티리얼 에셋이 없어 변형이 스트리핑되고 빌드에서만 줄무늬가 사라진다
            #pragma multi_compile_local _ _BELT_FLOW
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionOS = input.positionOS.xyz;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // 벨트 루프를 따라 잰 거리 (m). 입구 쪽 드럼 위 꼭대기 = 0
            float BeltPathDistance(float3 posOS)
            {
                float dir = _BeltHeadZ >= _BeltTailZ ? 1.0 : -1.0;
                float len = abs(_BeltHeadZ - _BeltTailZ);
                float r = _BeltRadius;
                float a = (posOS.z - _BeltTailZ) * dir;   // 0 = 아이들 드럼 축, len = 구동 드럼 축
                float h = posOS.y - _BeltCenterY;         // 드럼 축 기준 높이

                if (a > len)                                       // 구동 드럼: 위 → 아래로 돌아 내려감
                    return len + r * (HALF_PI - atan2(h, a - len));
                if (a < 0.0)                                       // 아이들 드럼: 아래 → 위로 돌아 올라감
                    return 2.0 * len + PI * r + r * (atan2(h, -a) + HALF_PI);
                if (h >= 0.0)                                      // 윗면: 입구 → 출구
                    return a;
                return len + PI * r + (len - a);                   // 아랫면: 출구 → 입구
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half3 albedo = _BaseColor.rgb;

            #if defined(_BELT_FLOW)
                // 벨트 표면 줄무늬: 화면 미분으로 경계를 부드럽게 (먼 거리 앨리어싱 방지)
                float s = (BeltPathDistance(i.positionOS) - _BeltOffset) / _StripeSpacing;
                float f = frac(s);
                float w = max(fwidth(s), 1e-4);
                float stripe = smoothstep(_StripeWidth + w, _StripeWidth - w, f) * smoothstep(-w, w, f);
                albedo = lerp(albedo, _StripeColor.rgb, stripe);
            #endif

                float3 normalWS = normalize(i.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(i.positionWS);

                // 주광원 (그림자 포함) + 구면 조화 환경광 + Blinn-Phong 반사
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half ndotl = saturate(dot(normalWS, light.direction));
                half3 radiance = light.color * (light.shadowAttenuation * light.distanceAttenuation);
                half3 diffuseColor = albedo * (1.0 - _Metallic * 0.8);
                half3 specColor = lerp(half3(0.04, 0.04, 0.04), albedo, _Metallic);
                float3 halfDir = normalize(light.direction + viewWS);
                half specPower = exp2(10.0 * _Smoothness + 1.0);
                half spec = pow(saturate(dot(normalWS, halfDir)), specPower) * _Smoothness;

                half3 color = diffuseColor * (radiance * ndotl + SampleSH(normalWS))
                            + specColor * spec * radiance * ndotl;

                // 오프라인: 회색조 + 약간 어둡게
                half luma = dot(color, half3(0.299, 0.587, 0.114));
                color = lerp(color, luma.xxx * 0.7, _Desaturate);

                // 상태 하이라이트: 맥동 발광 + 가장자리 림
                half pulse = _PulseHz > 0.0 ? 0.55 + 0.45 * sin(_Time.y * TWO_PI * _PulseHz) : 1.0;
                half rim = pow(1.0 - saturate(dot(normalWS, viewWS)), 3.0);
                color += _HighlightColor.rgb * _HighlightStrength * pulse * (0.45 + 1.2 * rim);

                color = MixFog(color, i.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 ShadowVert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                return ApplyShadowClamping(positionCS);
            }

            half4 ShadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 DepthVert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half DepthFrag(float4 positionCS : SV_POSITION) : SV_Target { return positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            half4 DepthNormalsFrag(Varyings i) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(i.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
