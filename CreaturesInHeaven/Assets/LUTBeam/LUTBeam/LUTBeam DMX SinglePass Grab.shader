// I dedicate this work to the public domain. Do as you will.
// Initial implementation by Torvid
// Optimizations by ValueFactory
// Tweaks and MDMX integration by Micca

Shader "MDMX/LUTBeam Experimental SinglePass Grabpass"
{
    Properties
    {
        _GoboTex ("Gobo Array", 2DArray) = "white" {}
        _GoboLUT ("LUT Array", 2DArray) = "white" {}

        _Angle ("_Angle", Float) = -0.31
        _Offset ("_Offset", Float) = -0.12
        _NearRadius ("_NearRadius", Float) = 0.05
        _FarZ ("_FarZ", Float) = 120
        _FarZMaxZoom ("_FarZMaxZoom", Float) = 40

        _BrightnessGoboZoomMin ("_BrightnessGoboZoomMin", Float) = 5
        _BrightnessGoboZoomMax ("_BrightnessGoboZoomMax", Float) = 0.5
        _BrightnessVolumeZoomMin ("_BrightnessVolumeZoomMin", Float) = 5
        _BrightnessVolumeZoomMax ("_BrightnessVolumeZoomMax", Float) = 1

        _FadeDist ("Volume Fade Distance", Float) = 1
        _FadeMult ("Volume Fade Mult", Float) = 1

        _AngleMin ("Zoom Min", Float) = 0.02
        _AngleMax ("Zoom Max", Float) = 0.5

        _PanOffset("Pan Offset", Float) = 0
        _PanMin("Pan Min", Float) = -180
        _PanMax("Pan Max", Float) = 180
        
        _TiltOffset("Tilt Offset", Float) = 0
        _TiltMin("Tilt Min", Float) = -35.7
        _TiltMax("Tilt Max", Float) = 215.8

        _Pan("_Pan", Range(0,1)) = 0

        _Tilt("_Tilt", Range(0,1)) = 0

        _SpinMult("Spin Speed", Float) = 0.8

        _DMXChannel("DMX Channel", Int) = -1

        _DMXMotionChannel ("Motion Channel", Int) = -1
        _MotionScale("Motion Scale", Vector) = (1,1,1)
        _MotionOffset("Motion Offset", Vector) = (1,1,1)
            
        _Wtf("_Wtf", Float) = 0.8

        //[Header("DMX")]
        _Pan("_Pan", Range(0,1)) = 0
        _Tilt("_Tilt", Range(0,1)) = 0
        _Zoom("_Zoom", Range(0,1)) = 0
        _Strobe("_Strobe", Range(0,1)) = 0
        _Color("_Color", Color) = (1,1,1,0)
        _GoboSpin("_GoboSpin", Range(0,1)) = 0
        _GoboSelection("_GoboSelection", Range(0,1)) = 0
        _Speed("_Speed", Range(0,1)) = 0
        _Dimmer("_Dimmer", Range(0,1)) = 0

    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        LOD 100
        Cull Front
        ZTest Off
        ZWrite Off
        
        GrabPass { "_GrabTexture" }
        Pass
        {
            Name "LUTBeam Experimental SinglePass Grabpass"
        
            Blend One One
        
            CGPROGRAM
            
            #pragma multi_compile_instancing
        
            #define VOLUME_PASS
            #define DECAL_PASS
            #define IS_GRAB
            #define SHOW_WHEN_INSIDE
            #define SHOW_WHEN_OUTSIDE
        
            #include "UnityCG.cginc"
            #include "LUTBeam.cginc"
        
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
        
            ENDCG
        }
    }
}
