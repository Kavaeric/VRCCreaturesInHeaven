Shader "Hidden/LightVolumeAnimTest"
{
    Properties
    {
        _MainTex ("Atlas", 3D) = "white" {}
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
            sampler3D _UdonLVAnimTest_PackedTex;

            float3 _UdonLVAnimTest_UvwMin0;
            float3 _UdonLVAnimTest_UvwMax0;
            float3 _UdonLVAnimTest_UvwMin1;
            float3 _UdonLVAnimTest_UvwMax1;
            float3 _UdonLVAnimTest_UvwMin2;
            float3 _UdonLVAnimTest_UvwMax2;

            // Normalised sizes of one frame and one SH sub-texture in the packed texture.
            float _UdonLVAnimTest_FrameScaleY;  // = H / totalHeight  = 1/numFrames
            float _UdonLVAnimTest_SliceScaleZ;  // = D / totalDepth   = 1/3
            float _UdonLVAnimTest_NumFrames;
            // 1 = add fixture contribution on top of existing atlas data (default)
            // 0 = replace atlas data entirely
            float _UdonLVAnimTest_Additive;

            // Sample one SH sub-texture for a given frame, remapping local 0-1 UVW
            // into the correct block in the packed texture.
            float4 SamplePacked(float3 local, int frame, int shSlot)
            {
                float u = local.x;
                float v = (local.y + frame) * _UdonLVAnimTest_FrameScaleY;
                float w = (local.z + shSlot) * _UdonLVAnimTest_SliceScaleZ;
                return tex3D(_UdonLVAnimTest_PackedTex, float3(u, v, w));
            }

            // Sample and lerp between two adjacent frames for one SH sub-texture.
            float4 SampleLerped(float3 local, float t, int shSlot)
            {
                int   frameA = (int) t % _UdonLVAnimTest_NumFrames;
                int   frameB = (frameA + 1) % _UdonLVAnimTest_NumFrames;
                float blend  = frac(t);
                return lerp(SamplePacked(local, frameA, shSlot), SamplePacked(local, frameB, shSlot), blend);
            }

            float4 frag(v2f_customrendertexture i) : SV_Target
            {
                float3 uvw = i.localTexcoord.xyz;
                float4 col = tex3D(_MainTex, uvw);

                bool inTex0 = all(uvw >= _UdonLVAnimTest_UvwMin0) && all(uvw < _UdonLVAnimTest_UvwMax0);
                bool inTex1 = all(uvw >= _UdonLVAnimTest_UvwMin1) && all(uvw < _UdonLVAnimTest_UvwMax1);
                bool inTex2 = all(uvw >= _UdonLVAnimTest_UvwMin2) && all(uvw < _UdonLVAnimTest_UvwMax2);

                // Cycle through frames once per second.
                float t = fmod(_Time.y, _UdonLVAnimTest_NumFrames);

                float4 base = _UdonLVAnimTest_Additive > 0 ? col : float4(0, 0, 0, 0);

                if (inTex0)
                {
                    float3 local = (uvw - _UdonLVAnimTest_UvwMin0) / (_UdonLVAnimTest_UvwMax0 - _UdonLVAnimTest_UvwMin0);
                    return base + SampleLerped(local, t, 0);
                }
                if (inTex1)
                {
                    float3 local = (uvw - _UdonLVAnimTest_UvwMin1) / (_UdonLVAnimTest_UvwMax1 - _UdonLVAnimTest_UvwMin1);
                    return base + SampleLerped(local, t, 1);
                }
                if (inTex2)
                {
                    float3 local = (uvw - _UdonLVAnimTest_UvwMin2) / (_UdonLVAnimTest_UvwMax2 - _UdonLVAnimTest_UvwMin2);
                    return base + SampleLerped(local, t, 2);
                }

                return col;
            }
            ENDCG
        }
    }
}
