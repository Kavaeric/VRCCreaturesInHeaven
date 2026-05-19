// Voxel preview shader for AnimatedLightVolume.
//
// Derived from VRC Light Volumes' own LightVolumesPreview shader, but a
// kind of horrible mutant version that can evaluate per-voxel L1 SH to shade
// each sphere with directional lighting.
Shader "Hidden/Moment/ALVPreview" {

    SubShader {

        Pass {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Packages/red.sim.lightvolumes/Shaders/LightVolumes.cginc"

            StructuredBuffer<float3> _Positions;
            // SH textures unpacked per voxel:
            //   SH0: (L0.r,   L0.g,   L0.b,   L1r.z)
            //   SH1: (L1r.x,  L1g.x,  L1b.x,  L1g.z)
            //   SH2: (L1r.y,  L1g.y,  L1b.y,  L1b.z)
            StructuredBuffer<float4> _SH0;
            StructuredBuffer<float4> _SH1;
            StructuredBuffer<float4> _SH2;
            float _Scale;
            // 0 = Full, 1 = L0 only, 2 = L1 only
            int _SHMode;

            struct Attributes {
                float3 posOS  : POSITION;
                float3 normOS : NORMAL;
                uint   id     : SV_InstanceID;
            };

            struct Varyings {
                float4 posCS  : SV_Position;
                float3 normWS : NORMAL;
                float3 L0     : TEXCOORD0;
                float3 L1r    : TEXCOORD1;
                float3 L1g    : TEXCOORD2;
                float3 L1b    : TEXCOORD3;
            };

            Varyings vert (Attributes v) {
                float3 world = _Positions[v.id] + v.posOS * _Scale;

                float4 sh0 = _SH0[v.id];
                float4 sh1 = _SH1[v.id];
                float4 sh2 = _SH2[v.id];

                Varyings o;
                o.posCS  = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.normWS = v.normOS; // icosphere normals are already in world-ish space
                o.L0     = float3(sh0.r, sh0.g, sh0.b);
                o.L1r    = float3(sh1.r, sh2.r, sh0.a);
                o.L1g    = float3(sh1.g, sh2.g, sh1.a);
                o.L1b    = float3(sh1.b, sh2.b, sh2.a);
                return o;
            }

            float4 frag (Varyings i) : SV_Target {
                float3 n = normalize(i.normWS);
                float3 col;
                if (_SHMode == 1)
                {
                    // L0 only: ambient colour, no directionality.
                    col = i.L0;
                }
                else if (_SHMode == 2)
                {
                    // L1 only: directionality magnitude. abs() prevents negative
                    // values clamping to black; the result shows where light comes from.
                    float lr = dot(i.L1r, n);
                    float lg = dot(i.L1g, n);
                    float lb = dot(i.L1b, n);
                    col = abs(float3(lr, lg, lb));
                }
                else
                {
                    col = LightVolumeEvaluate(n, i.L0, i.L1r, i.L1g, i.L1b);
                }
                return float4(col, 1);
            }

            ENDHLSL

        }

    }

}
