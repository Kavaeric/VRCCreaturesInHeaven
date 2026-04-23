Shader "atmospheric/shoeboxsky"
{
    Properties
    {
        [Header(Atmosphere)] [Space]
        _BakedTexture ("_BakedTexture", 2D) = "white" {}
        _Exposure ("Exposure", Range(0, 8)) = 1
        _Rotation ("Rotation", Range(0, 360)) = 0
        
        [Header(Ground)] [Space]
        [Toggle] _GroundEnabled ("_GroundEnabled", Float) = 1
        _GroundTexture ("_GroundTexture", 2D) = "white" {}
        _GroundScollX ("_GroundScollX", Range(-1, 1)) = 0
        _GroundScollY ("_GroundScollY", Range(-1, 1)) = 0
        _Altitude ("Altitude (m)", Float) = 3000

        [Header(Sun Disc)] [Space]
        _SunDiscRadius ("_SunDiscRadius", Range(0, 0.03)) = 0.03
        _SunDiscBrightness ("_SunDiscBrightness", Range(0, 100)) = 10

        [Header(Plane 0)] [Space]
        _Plane0Texture ("_Plane0Texture", 2D) = "white" {}
        _Plane0Scroll ("_Plane0Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane0Pos ("_Plane0Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane0Tangent ("_Plane0Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane0Bitangent ("_Plane0Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane0Size ("_Plane0Size", Float) = 1000

        [Header(Plane 1)] [Space]
        _Plane1Texture ("_Plane1Texture", 2D) = "white" {}
        _Plane1Scroll ("_Plane1Scroll", Range(0, 1)) = 0
        [HideInInspector] _Plane1Pos ("_Plane1Pos", Vector) = (0,0,0,0)
        [HideInInspector] _Plane1Tangent ("_Plane1Tangent", Vector) = (1,0,0,0)
        [HideInInspector] _Plane1Bitangent ("_Plane1Bitangent", Vector) = (0,0,1,0)
        [HideInInspector] _Plane1Size ("_Plane1Size", Float) = 1000

        [Header(Rendering)] [Space]
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("Cull Mode", Float) = 0
    }
    SubShader
    {
        // HDR output tag required for values above 1.0 to survive to the framebuffer.
        Tags { "RenderType"="Opaque" "PreviewType"="Skybox" }

        Cull [_CullMode]
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            #define PI 3.141592

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

            float3 _Plane0Pos;
            float3 _Plane0Tangent;
            float3 _Plane0Bitangent;
            float _Plane0Size;
            float _Plane0Scroll;
            sampler2D _Plane0Texture;

            float3 _Plane1Pos;
            float3 _Plane1Tangent;
            float3 _Plane1Bitangent;
            float _Plane1Size;
            float _Plane1Scroll;
            sampler2D _Plane1Texture;

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
                if(RayPlaneIntersect(_WorldSpaceCameraPos, rayDir,
                              _Plane0Pos.rgb,
                    normalize(_Plane0Tangent.rgb)  /_Plane0Size,
                    normalize(_Plane0Bitangent.rgb)/_Plane0Size, planeUV))
                {
                    planeUV += float2(_Plane0Scroll*_Time.r, 0);
                    float4 plane = tex2D(_Plane0Texture, planeUV);
                    result = lerp(result, plane.rgb, plane.a);
                }

                if(RayPlaneIntersect(_WorldSpaceCameraPos, rayDir,
                              _Plane1Pos.rgb,
                    normalize(_Plane1Tangent.rgb)  /_Plane1Size,
                    normalize(_Plane1Bitangent.rgb)/_Plane1Size, planeUV))
                {
                    planeUV += float2(_Plane1Scroll*_Time.r, 0);
                    float4 plane = tex2D(_Plane1Texture, planeUV);
                    result = lerp(result, plane.rgb, plane.a);
                }

                return half4(result * _Exposure, 1);
            }
            ENDCG
        }
    }
}
