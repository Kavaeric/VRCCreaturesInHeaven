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

            float3 _UdonLVAnimTest_UvwMin0;
            float3 _UdonLVAnimTest_UvwMax0;
            float3 _UdonLVAnimTest_UvwMin1;
            float3 _UdonLVAnimTest_UvwMax1;
            float3 _UdonLVAnimTest_UvwMin2;
            float3 _UdonLVAnimTest_UvwMax2;

            float4 frag(v2f_customrendertexture i) : SV_Target
            {
                float3 uvw = i.localTexcoord.xyz;
                float4 col = tex3D(_MainTex, uvw);

                bool inTex0 = all(uvw >= _UdonLVAnimTest_UvwMin0) && all(uvw < _UdonLVAnimTest_UvwMax0);
                bool inTex1 = all(uvw >= _UdonLVAnimTest_UvwMin1) && all(uvw < _UdonLVAnimTest_UvwMax1);
                bool inTex2 = all(uvw >= _UdonLVAnimTest_UvwMin2) && all(uvw < _UdonLVAnimTest_UvwMax2);

                // SH L1 packing layout:
                //   Tex0: (L0.r,  L0.g,  L0.b,  L1r.z)
                //   Tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
                //   Tex2: (L1r.y, L1g.y, L1b.y, L1b.z)
                //
                // e.g. if L0.r = 1.0 for red light:
                //   L1r.z = 1.0  →  red light from +Z 
                //   L1r.x = 1.0  →  red light from +X
                //   L1r.y = 1.0  →  red light from +Y (i.e. directly overhead)
                //
                float pulse = abs(sin(_Time.y * 2.0));
                if (inTex0) return float4(pulse, 0, 0, 0);
                if (inTex1) return float4(0, 0, 0, 0);
                if (inTex2) return float4(0, 0, 0, 0);

                return col;
            }
            ENDCG
        }
    }
}
