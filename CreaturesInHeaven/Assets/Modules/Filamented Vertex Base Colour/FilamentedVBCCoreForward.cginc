// Unity built-in shader source. Copyright (c) 2016 Unity Technologies. MIT license (see license.txt)

#ifndef FILAMENTED_VBC_CORE_FORWARD_INCLUDED
#define FILAMENTED_VBC_CORE_FORWARD_INCLUDED

#include "Packages/s-ilent.filamented/Filamented/UnityStandardConfig.cginc"

#include "Packages/s-ilent.filamented/Filamented/UnityStandardCore.cginc"
VertexOutputForwardBase vertBase (VertexInput v) { return vertForwardBase(v); }
VertexOutputForwardAdd vertAdd (VertexInput v) { return vertForwardAdd(v); }

// Modified base colour function that multiplies by vertex colour
// Copied from UnityStandardCore.cginc
half4 fragVBCForwardBaseInternal (VertexOutputForwardBase i, bool gl_FrontFacing)
{
    UNITY_APPLY_DITHER_CROSSFADE(i.pos.xy);

    float4 i_texBase = i.tex;

    MATERIAL_SETUP(material)

    // Multiply material base colour with the vertex colour
    #if defined (HAS_ATTRIBUTE_COLOR)
        material.baseColor *= i.color;
    #endif

    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    ShadingParams shading = (ShadingParams)0;
    // Initialize shading with expected parameters
    computeShadingParamsForwardBase(shading, i, gl_FrontFacing);

    prepareMaterial(shading, material);

#if (defined(_NORMALMAP) && defined(NORMALMAP_SHADOW))
    float noise = noiseR2(i.pos.xy);
    float nmShade = NormalTangentShadow (i.tex, i.lightDirTS, noise);
    shading.attenuation = min(shading.attenuation, max(1-nmShade, 0));
#endif

    float4 c = evaluateMaterial (shading, material);

    UNITY_EXTRACT_FOG_FROM_EYE_VEC(i);
    UNITY_APPLY_FOG(_unity_fogCoord, c.rgb);
    return c;
}

half4 fragVBCForwardBase (VertexOutputForwardBase i, bool facing : SV_IsFrontFace) : SV_Target   // backward compatibility (this used to be the fragment entry function)
{
    return fragVBCForwardBaseInternal(i, facing);
}

// Modified base colour function that multiplies by vertex colour
// Copied from UnityStandardCore.cginc
half4 fragVBCForwardAddInternal (VertexOutputForwardAdd i, bool gl_FrontFacing)
{
    UNITY_APPLY_DITHER_CROSSFADE(i.pos.xy);

    MATERIAL_SETUP_FWDADD(material)

    // Multiply material base colour with the vertex colour
    #if defined (HAS_ATTRIBUTE_COLOR)
        material.baseColor *= i.color;
    #endif

    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    ShadingParams shading = (ShadingParams)0;
    // Initialize shading with expected parameters
    computeShadingParamsForwardAdd(shading, i, gl_FrontFacing);

    prepareMaterial(shading, material);

#if (defined(_NORMALMAP) && defined(NORMALMAP_SHADOW))
    float noise = noiseR2(i.pos.xy);
    float nmShade = NormalTangentShadow (i.tex, i.lightDirTS, noise);
    shading.attenuation = min(shading.attenuation, max(1-nmShade, 0));
#endif

    float4 c = evaluateMaterial (shading, material);

    UNITY_EXTRACT_FOG_FROM_EYE_VEC(i);
    UNITY_APPLY_FOG_COLOR(_unity_fogCoord, c.rgb, half4(0,0,0,0)); // fog towards black in additive pass
    return c;
}

half4 fragVBCForwardAdd (VertexOutputForwardAdd i, bool facing : SV_IsFrontFace) : SV_Target     // backward compatibility (this used to be the fragment entry function)
{
    return fragVBCForwardAddInternal(i, facing);
}

half4 fragBase (VertexOutputForwardBase i, bool facing : SV_IsFrontFace) : SV_Target { return fragVBCForwardBaseInternal(i, facing); }
half4 fragAdd (VertexOutputForwardAdd i, bool facing : SV_IsFrontFace) : SV_Target { return fragVBCForwardAddInternal(i, facing); }


#endif // FILAMENTED_VBC_CORE_FORWARD_INCLUDED
