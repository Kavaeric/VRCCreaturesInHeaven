// AnimatedLightVolume.shader
//
// Internal shader used to sample an animated light volume texture and apply it
// to a VRC Light Volumes atlas.

Shader "Hidden/AnimatedLightVolume"
{
    Properties
    {
        _MainTex ("Atlas", 3D) = "white" {}
        _PackedTex ("Packed SH Texture", 3D) = "black" {}

        // UVW bounds of the target volume's three SH islands in the atlas.
        _UvwMin0 ("UVW Min 0", Vector) = (0, 0, 0, 0)
        _UvwMax0 ("UVW Max 0", Vector) = (1, 1, 1, 0)
        _UvwMin1 ("UVW Min 1", Vector) = (0, 0, 0, 0)
        _UvwMax1 ("UVW Max 1", Vector) = (1, 1, 1, 0)
        _UvwMin2 ("UVW Min 2", Vector) = (0, 0, 0, 0)
        _UvwMax2 ("UVW Max 2", Vector) = (1, 1, 1, 0)

        // Packed texture layout parameters, derived from texture dimensions at setup time.
        _NumSamples ("Num Samples", Int) = 2
        _SampleScale ("Sample scale", Float) = 0.5   // = 1 / numSamples
        _SliceScale  ("Slice scale",  Float) = 0.333 // = 1 / numSlots

        // Normalised playback position: 0 = first sample, 1 = last sample.
        _Time4D ("Time", Range(0, 1)) = 0

        // Blending mode (matches ALVBlendingMode enum):
        // 0 = Replace, 1 = Add, 2 = Subtract, 3 = Multiply
        _BlendMode ("Blend Mode", Int) = 1

        // Scales the SH contribution before blending.
        _Intensity ("Intensity", Range(0, 1)) = 1

        // SH fidelity mode (matches ALVSHMode enum):
        // 0 = L1 (full, 3 slots), 1 = MonoL1 (2 slots), 2 = MonoL0 (1 slot)
        _SHMode ("SH Mode", Int) = 0

        // Bit depth (matches ALVBitDepth enum):
        // 0 = Depth8 (RGBA32/RGB24 UNORM), 1 = Depth16 (RGBAHalf float or RGB48 UNORM)
        _BitDepth ("Bit Depth", Int) = 0

        // 1 if the packed texture is UNORM and needs the [0,1]->[-1,1] decode.
        // True for all Depth8 modes and MonoL1+Depth16 (RGB48).
        _IsUnorm ("Is UNORM", Int) = 1
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #include "UnityCustomRenderTexture.cginc"

            sampler3D _MainTex;
            sampler3D _PackedTex;

            float3 _UvwMin0, _UvwMax0;
            float3 _UvwMin1, _UvwMax1;
            float3 _UvwMin2, _UvwMax2;

            int   _NumSamples;
            float _SampleScale;
            float _SliceScale;
            float _Time4D;
            float _Intensity;
            int   _BlendMode;
            int   _SHMode;
            int   _BitDepth;
            int   _IsUnorm;

            // Sample one slot from the packed texture for a given sample index and slot index.
            float4 SamplePacked(float3 local, int sample, int slot)
            {
                float u = local.x;
                float v = (local.y + sample) * _SampleScale;
                float w = (local.z + slot)   * _SliceScale;
                float4 s = tex3D(_PackedTex, float3(u, v, w));
                // UNORM formats store values remapped to [0,1]; decode back to [-1,1].
                if (_IsUnorm)
                    s = s * 2.0 - 1.0;
                return s;
            }

            // Lerp between two adjacent samples for a given slot.
            // t is in [0, numSamples), derived from _Time4D by the caller.
            float4 SampleLerped(float3 local, float t, int slot)
            {
                int   sampleA = (int)t % _NumSamples;
                int   sampleB = (sampleA + 1) % _NumSamples;
                float blend   = frac(t);
                return lerp(SamplePacked(local, sampleA, slot),
                            SamplePacked(local, sampleB, slot), blend);
            }

            // Reconstruct a full-SH (L1 mode) output from 3 packed slots.
            // Slots: 0 = (L0.r, L0.g, L0.b, L1r.z)
            //        1 = (L1r.x, L1g.x, L1b.x, L1g.z)
            //        2 = (L1r.y, L1g.y, L1b.y, L1b.z)
            // The atlas uses the same layout, so we write the sampled values straight back.
            float4 ReconstructL1(float3 local, float t, uint atlasSlot)
            {
                return SampleLerped(local, t, atlasSlot);
            }

            // Reconstruct a MonoL1 output for a given atlas slot.
            // Packed slots: 0 = (L0.r, L0.g, L0.b, 0), 1 = (L1.x, L1.y, L1.z, 0).
            // Atlas slot layout:
            //   slot 0: (L0.r, L0.g, L0.b, L1mono.z)
            //   slot 1: (L1mono.x, L1mono.x, L1mono.x, L1mono.z)  [same x for all channels]
            //   slot 2: (L1mono.y, L1mono.y, L1mono.y, L1mono.z)  [same y for all channels]
            float4 ReconstructMonoL1(float3 local, float t, uint atlasSlot)
            {
                float4 s0 = SampleLerped(local, t, 0); // (L0.r, L0.g, L0.b, 0)
                float4 s1 = SampleLerped(local, t, 1); // (L1.x, L1.y, L1.z, 0)
                float l1x = s1.r, l1y = s1.g, l1z = s1.b;

                if (atlasSlot == 0) return float4(s0.r, s0.g, s0.b, l1z);
                if (atlasSlot == 1) return float4(l1x,  l1x,  l1x,  l1z);
                                    return float4(l1y,  l1y,  l1y,  l1z);
            }

            // Reconstruct a MonoL0 output for a given atlas slot.
            // Packed slot: 0 = (L0, L1.x, L1.y, L1.z).
            // Atlas slot layout mirrors MonoL1 but with uniform L0 across all channels.
            float4 ReconstructMonoL0(float3 local, float t, uint atlasSlot)
            {
                float4 s0 = SampleLerped(local, t, 0); // (L0, L1.x, L1.y, L1.z)
                float l0 = s0.r, l1x = s0.g, l1y = s0.b, l1z = s0.a;

                if (atlasSlot == 0) return float4(l0,  l0,  l0,  l1z);
                if (atlasSlot == 1) return float4(l1x, l1x, l1x, l1z);
                                    return float4(l1y, l1y, l1y, l1z);
            }

            float4 frag(v2f_customrendertexture i) : SV_Target
            {
                float3 uvw = i.localTexcoord.xyz;
                float4 col = tex3D(_MainTex, uvw);

                bool inTex0 = all(uvw >= _UvwMin0) && all(uvw < _UvwMax0);
                bool inTex1 = all(uvw >= _UvwMin1) && all(uvw < _UvwMax1);
                bool inTex2 = all(uvw >= _UvwMin2) && all(uvw < _UvwMax2);

                if (!inTex0 && !inTex1 && !inTex2)
                    return col;

                float t = _Time4D * (_NumSamples - 1);

                // Resolve which atlas slot and local UVW this texel belongs to.
                float3 local;
                uint   atlasSlot;
                if (inTex0) { local = (uvw - _UvwMin0) / (_UvwMax0 - _UvwMin0); atlasSlot = 0; }
                else if (inTex1) { local = (uvw - _UvwMin1) / (_UvwMax1 - _UvwMin1); atlasSlot = 1; }
                else             { local = (uvw - _UvwMin2) / (_UvwMax2 - _UvwMin2); atlasSlot = 2; }

                float4 sh;
                if      (_SHMode == 2) sh = ReconstructMonoL0(local, t, atlasSlot) * _Intensity;
                else if (_SHMode == 1) sh = ReconstructMonoL1(local, t, atlasSlot) * _Intensity;
                else                   sh = ReconstructL1    (local, t, atlasSlot) * _Intensity;

                switch (_BlendMode)
                {
                    case 1:  return col + sh;  // Add
                    case 2:  return col - sh;  // Subtract
                    case 3:  return col * sh;  // Multiply
                    default: return sh;        // Replace
                }
            }
            ENDCG
        }
    }
}
