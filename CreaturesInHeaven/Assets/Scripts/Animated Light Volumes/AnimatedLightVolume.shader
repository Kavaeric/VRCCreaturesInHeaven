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
        _FrameScaleY ("Frame Scale Y", Float) = 0.5   // = 1 / numFrames
        _SliceScaleZ ("Slice Scale Z", Float) = 0.333 // = 1 / 3

        // Normalised playback position: 0 = first frame, 1 = last frame.
        _Time4D ("Time", Range(0, 1)) = 0

        // Blending mode (matches ALVBlendingMode enum):
        // 0 = Replace, 1 = Add, 2 = Subtract, 3 = Multiply
        _BlendMode ("Blend Mode", Int) = 1

        // Scales the SH contribution before blending.
        _Intensity ("Intensity", Range(0, 1)) = 1
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

            float _FrameScaleY;
            float _SliceScaleZ;
            float _Time4D;
            float _Intensity;
            int   _BlendMode;

            // Sample one SH sub-texture for a given frame index and SH slot (0, 1 or 2).
            float4 SamplePacked(float3 local, int frame, int shSlot)
            {
                float u = local.x;
                float v = (local.y + frame) * _FrameScaleY;
                float w = (local.z + shSlot) * _SliceScaleZ;
                return tex3D(_PackedTex, float3(u, v, w));
            }

            // Lerp between two adjacent frames for one SH sub-texture.
            // t is in [0, numFrames), derived from _Time4D by the caller.
            float4 SampleLerped(float3 local, float t, uint shSlot, int numFrames)
            {
                uint  frameA = (uint)t % numFrames;
                uint  frameB = (frameA + 1) % numFrames;
                float blend  = frac(t);
                return lerp(SamplePacked(local, frameA, shSlot), SamplePacked(local, frameB, shSlot), blend);
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

                // Derive frame count from FrameScaleY (= 1/numFrames).
                int   numFrames = (int)round(1.0 / _FrameScaleY);
                float t         = _Time4D * (numFrames - 1);

                // Resolve which SH slot and local UVW this texel belongs to.
                float3 local;
                uint   shSlot;
                if (inTex0) { local = (uvw - _UvwMin0) / (_UvwMax0 - _UvwMin0); shSlot = 0; }
                else if (inTex1) { local = (uvw - _UvwMin1) / (_UvwMax1 - _UvwMin1); shSlot = 1; }
                else             { local = (uvw - _UvwMin2) / (_UvwMax2 - _UvwMin2); shSlot = 2; }

                float4 sh = SampleLerped(local, t, shSlot, numFrames) * _Intensity;

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
