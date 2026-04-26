Shader "atmospheric/shoeboxsky"
{
    Properties
    {
        _BakedTexture ("_BakedTexture", 2D) = "white" {}
        _Exposure ("Exposure", Range(0, 8)) = 1
        _Rotation ("Rotation", Range(0, 360)) = 0

        [Toggle] _GroundEnabled ("_GroundEnabled", Float) = 1
        _GroundTexture ("_GroundTexture", 2D) = "white" {}
        _GroundScollX ("_GroundScollX", Range(-1, 1)) = 0
        _GroundScollY ("_GroundScollY", Range(-1, 1)) = 0
        _Altitude ("Altitude (m)", Float) = 3000

        _SunDiscRadius ("_SunDiscRadius", Range(0, 0.03)) = 0.03
        _SunDiscBrightness ("_SunDiscBrightness", Range(0, 100)) = 10

        _Plane0Texture ("_Plane0Texture", 2D) = "transparent" {}
        _Plane0Scroll ("_Plane0Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane0Pos ("_Plane0Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane0Tangent ("_Plane0Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane0Bitangent ("_Plane0Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane0Size ("_Plane0Size", Float) = 0

        _Plane1Texture ("_Plane1Texture", 2D) = "transparent" {}
        _Plane1Scroll ("_Plane1Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane1Pos ("_Plane1Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane1Tangent ("_Plane1Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane1Bitangent ("_Plane1Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane1Size ("_Plane1Size", Float) = 0

        _Plane2Texture ("_Plane2Texture", 2D) = "transparent" {}
        _Plane2Scroll ("_Plane2Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane2Pos ("_Plane2Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane2Tangent ("_Plane2Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane2Bitangent ("_Plane2Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane2Size ("_Plane2Size", Float) = 0

        _Plane3Texture ("_Plane3Texture", 2D) = "transparent" {}
        _Plane3Scroll ("_Plane3Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane3Pos ("_Plane3Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane3Tangent ("_Plane3Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane3Bitangent ("_Plane3Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane3Size ("_Plane3Size", Float) = 0

        _Plane4Texture ("_Plane4Texture", 2D) = "transparent" {}
        _Plane4Scroll ("_Plane4Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane4Pos ("_Plane4Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane4Tangent ("_Plane4Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane4Bitangent ("_Plane4Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane4Size ("_Plane4Size", Float) = 0

        _Plane5Texture ("_Plane5Texture", 2D) = "transparent" {}
        _Plane5Scroll ("_Plane5Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane5Pos ("_Plane5Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane5Tangent ("_Plane5Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane5Bitangent ("_Plane5Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane5Size ("_Plane5Size", Float) = 0

        _Plane6Texture ("_Plane6Texture", 2D) = "transparent" {}
        _Plane6Scroll ("_Plane6Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane6Pos ("_Plane6Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane6Tangent ("_Plane6Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane6Bitangent ("_Plane6Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane6Size ("_Plane6Size", Float) = 0

        _Plane7Texture ("_Plane7Texture", 2D) = "transparent" {}
        _Plane7Scroll ("_Plane7Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane7Pos ("_Plane7Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane7Tangent ("_Plane7Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane7Bitangent ("_Plane7Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane7Size ("_Plane7Size", Float) = 0

        _Plane8Texture ("_Plane8Texture", 2D) = "transparent" {}
        _Plane8Scroll ("_Plane8Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane8Pos ("_Plane8Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane8Tangent ("_Plane8Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane8Bitangent ("_Plane8Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane8Size ("_Plane8Size", Float) = 0

        _Plane9Texture ("_Plane9Texture", 2D) = "transparent" {}
        _Plane9Scroll ("_Plane9Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane9Pos ("_Plane9Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane9Tangent ("_Plane9Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane9Bitangent ("_Plane9Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane9Size ("_Plane9Size", Float) = 0

        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("Cull Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Cull [_CullMode]
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            #define PI 3.14159265

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD1;
                float3 hemisphereAverage : TEXCOORD2;
            };

            sampler2D _BakedTexture;

            sampler2D _GroundTexture;

            float _GroundScollX;
            float _GroundScollY;
            float _SunDiscRadius;
            float _SunDiscBrightness;
            bool _GroundEnabled;
            float _Altitude;
            float _Exposure;
            float _Rotation;

            v2f vert (appdata v)
            {
                v2f input;
                input.vertex = UnityObjectToClipPos(v.vertex);
                input.worldPos = mul(unity_ObjectToWorld, float4(v.vertex.rgb, 1));
                input.uv = v.uv;

                input.hemisphereAverage = 0;
                for (int x = 0; x < 10; x++)
                {
                    for (int y = 0; y < 5; y++)
                    {
                        float2 uv = float2(x / 10.0f, (y / 10.0f)+0.5);
                        input.hemisphereAverage += tex2Dlod(_BakedTexture, float4(uv,0,0));
                    }
                }
                input.hemisphereAverage /= 10*5;

                return input;
            }

            bool RayPlaneIntersect(float3 rayOrigin, float3 rayDir, float3 planePos, float3 tangent, float3 bitangent, out float2 uv)
            {
                uv = 0.0;

                float3 normal = cross(tangent, bitangent);

                float denom = dot(rayDir, normal);
                if (abs(denom) < 0.00000000001)
                    return false;

                float t = dot(planePos - rayOrigin, normal) / denom;
                if (t < 0.0)
                    return false;

                float3 local = (rayOrigin + rayDir * t) - planePos;

                uv = float2(dot(local, tangent), dot(local, bitangent)) + 0.5;

                return all(uv >= 0.0) && all(uv <= 1.0);
            }

            float2 RaySphereUV(float3 rayOrigin, float3 rayDir, float3 sphereCenter, float sphereRadius, float uvTiling, inout float3 planetNormal)
            {
                float3 oc = rayOrigin - sphereCenter;
                float b = dot(oc, rayDir);
                float c = dot(oc, oc) - sphereRadius * sphereRadius;
                float disc = b * b - c;

                if (disc < 0.0)
                    return -1;

                float t = -b - sqrt(disc);
                if (t < 0.0)
                    t = -b + sqrt(disc);
                if (t < 0.0)
                    return -1;

                float3 n = normalize(rayOrigin + rayDir * t - sphereCenter);
                planetNormal = n;
                float3 absN = abs(n);

                float2 faceUV;
                if (absN.x >= absN.y && absN.x >= absN.z)
                    faceUV = float2(-sign(n.x) * n.z, -n.y) / absN.x;
                else if (absN.y >= absN.x && absN.y >= absN.z)
                    faceUV = float2(n.x, sign(n.y) * n.z) / absN.y;
                else
                    faceUV = float2(sign(n.z) * n.x, -n.y) / absN.z;

                return ((faceUV * 0.5 + 0.5) * uvTiling);
            }

            float2 DirToEquirect(float3 dir)
            {
                float u = frac(atan2(dir.z, -dir.x) * (0.5 / PI) + 1.0 + _Rotation / 360.0);
                float v = asin(clamp(dir.y, -1.0, 1.0)) / PI + 0.5;
                return float2(u, v);
            }

            #define DECLARE_PLANE(n) \
                float3 _Plane##n##Pos; \
                float3 _Plane##n##Tangent; \
                float3 _Plane##n##Bitangent; \
                float _Plane##n##Size; \
                float _Plane##n##Scroll; \
                sampler2D _Plane##n##Texture; \
                float4 _Plane##n##Texture_ST;

            DECLARE_PLANE(0)
            DECLARE_PLANE(1)
            DECLARE_PLANE(2)
            DECLARE_PLANE(3)
            DECLARE_PLANE(4)
            DECLARE_PLANE(5)
            DECLARE_PLANE(6)
            DECLARE_PLANE(7)
            DECLARE_PLANE(8)
            DECLARE_PLANE(9)

            #define APPLY_PLANE(n) \
                if(RayPlaneIntersect(_WorldSpaceCameraPos, rayDir, \
                    _Plane##n##Pos.rgb, \
                    normalize(_Plane##n##Tangent.rgb)   / _Plane##n##Size, \
                    normalize(_Plane##n##Bitangent.rgb) / _Plane##n##Size, planeUV)) \
                { \
                    planeUV = TRANSFORM_TEX(planeUV, _Plane##n##Texture) + float2(_Plane##n##Scroll * _Time.r, 0); \
                    float4 _planeCol = tex2D(_Plane##n##Texture, planeUV); \
                    result = lerp(result, _planeCol.rgb, _planeCol.a); \
                }

            // half4 instead of fixed4 to preserve HDR values above 1.0.
            half4 frag (v2f input) : SV_Target
            {
                float planetRadius = 6371000;
                float atmosphereRadius = 1000000;
                float3 sunDirection = _WorldSpaceLightPos0.xyz;

                float3 rayDir = normalize(input.worldPos - _WorldSpaceCameraPos);

                float4 col = tex2D(_BakedTexture, DirToEquirect(rayDir));
                float3 rayOrigin = float3(0, planetRadius + _Altitude, 0) + _WorldSpaceCameraPos;

                float3 planetNormal = 0;
                float2 uv = RaySphereUV(rayOrigin, rayDir, float3(0, 0, 0), planetRadius, 1000, planetNormal);

                float3 ground = tex2D(_GroundTexture, uv+float2(_GroundScollX, _GroundScollY) * _Time.r).rgb;

                float lighting = max(dot(planetNormal, sunDirection), 0);
                bool hit = !(uv.x == -1 && uv.y == -1);

                if(!hit || !_GroundEnabled)
                {
                    ground = 0;
                }

                float3 sun = dot(rayDir, sunDirection);
                _SunDiscRadius=_SunDiscRadius*_SunDiscRadius;
                sun = (sun-(1-_SunDiscRadius))/_SunDiscRadius;
                if(hit)
                    sun = 0;

                float3 atmosphere = col.rgb;
                ground = ground * (lighting + input.hemisphereAverage);

                sun = saturate(sun*10) * atmosphere * _LightColor0.xyz * _SunDiscBrightness;

                float3 result = atmosphere + ground + sun;

                float2 planeUV;
                APPLY_PLANE(0)
                APPLY_PLANE(1)
                APPLY_PLANE(2)
                APPLY_PLANE(3)
                APPLY_PLANE(4)
                APPLY_PLANE(5)
                APPLY_PLANE(6)
                APPLY_PLANE(7)
                APPLY_PLANE(8)
                APPLY_PLANE(9)

                return half4(result * _Exposure, 1);
            }
            ENDCG
        }
    }
    CustomEditor "ShoeboxSkyGUI"
}
