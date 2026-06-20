// Diamond - Beam sub-module (RECTANGULAR profile)
// Volumetric light shaft for stage spotlight fixtures with a rectangular
// emitter and (optionally elliptical) rectangular cone.
//
// Shape-independent machinery lives in DiamondBeamCommon.cginc; this file
// supplies only the rectangular specifics: the 4-plane side-wall intersection,
// the rectangular cross-section area (w*h), and the rectangular edge softness.
//
// The round-emitter counterpart is DiamondBeamRound.shader.
//
// The mesh used by this shader should be a UNIT CUBE (corners at +/-0.5 on
// every axis). The vertex shader expands that cube at render time so it exactly
// contains the frustum implied by the shader properties.

Shader "Diamond/Beam"
{
    Properties
    {
        // Physical size of the emitter face, in WORLD-space units (metres).
        // The +Y face of this rectangle is what the beam projects from.
        _EmitterWidth  ("Emitter Width",  Float) = 0.5
        _EmitterHeight ("Emitter Height", Float) = 0.5

        // Beam half-angles, expressed as tan(half-angle).
        // This is how much the beam widens per unit of length on each side.
        _SpreadX ("Spread X (tan of half angle)", Float) = 0.0
        _SpreadZ ("Spread Z (tan of half angle)", Float) = 0.0

        // Beam shear, which angles the light shaft equally across an axis.
        _ShearX ("Shear X", Float) = 0.0
        _ShearZ ("Shear Z", Float) = 0.0

        // Brightness threshold below which the beam stops rendering. The
        // effective beam length is auto-derived per-frame from the inverse-square
        // falloff, the fixture's flux, and _BeamIntensity.
        _BeamCutoffThreshold ("Beam Cutoff Threshold", Float) = 0.0001

        // Hard ceiling on the auto-derived beam length, in metres.
        _BeamLengthMax ("Beam Length Max (metres)", Float) = 50

        // Counter-scale: set this to the GameObject's localScale to make the
        // shader render at true world size regardless of the cube's transform
        // scale. Leave at (1, 1, 1) for normal use.
        _CubeLocalScale ("Cube Local Scale (compensation)", Vector) = (1, 1, 1, 0)

        _Color ("Color", Color) = (1, 1, 1, 1)

        // Intensity multiplier for the beam.
        _BeamIntensity ("Intensity", Float) = 1.0

        // Haze density: per-metre extinction coefficient of the air the beam
        // passes through. Controls both brightness and falloff (see .cginc).
        //   ~0.005 clear air, ~0.05 venue haze, ~0.15 heavy fog, ~0.5+ smoke.
        _HazeDensity ("Haze Density (1/m)", Float) = 0.05

        // Edge softness: how much the beam's sides blur with distance and haze.
        // 0 = razor-sharp edges. Reasonable values 0.0 - 2.0; default 1.0.
        _EdgeSoftness ("Edge Softness", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        ZWrite Off
        ZTest Off
        Cull Front

        Pass
        {
            Blend One One

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile_instancing

            // --- Rectangular shape definitions ----------------------------
            // Cross-section is a rectangle whose half-extents grow with spread.
            // The DERIVE_BEAM_LENGTH macro evaluates these in a scope where
            // emitterWidth/emitterHeight/spreadX/spreadZ are locals.
            #define DIAMOND_EMITTER_AREA   (emitterWidth * emitterHeight)
            #define DIAMOND_CROSS_AREA(d)  ((emitterWidth + 2.0 * spreadX * (d)) * (emitterHeight + 2.0 * spreadZ * (d)))

            // Bounding box uses the two spreads independently (elliptical cone).
            #define DIAMOND_BOUNDS_SPREAD_X spreadX
            #define DIAMOND_BOUNDS_SPREAD_Z spreadZ

            #include "DiamondBeamCommon.cginc"

            v2f vert(appdata v) { return DiamondBeamVert(v); }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale).xyz;
                float4 instColor      = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  beamIntensity  = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float  emitterWidth   = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
                float  emitterHeight  = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterHeight);
                float  spreadX        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadX);
                float  spreadZ        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadZ);

                float beamLength;
                DIAMOND_DERIVE_BEAM_LENGTH(beamLength);

                // Build the camera ray in BEAM SPACE (t is in world metres).
                float3 cameraObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayOrigin    = cameraObject * cubeLocalScale;
                float3 rayDirection = normalize(i.vertBeamSpace - rayOrigin);

                float tEntry = -1e20;
                float tExit  =  1e20;

                // Widen the intersection walls by the diffusion rate so they
                // track the smoothstep softness in the falloff math below.
                float diffusionRateWall = _EdgeSoftness * (0.02 + _HazeDensity);
                float spreadXSoft = spreadX + diffusionRateWall;
                float spreadZSoft = spreadZ + diffusionRateWall;

                // Four slanted side walls (outward-pointing normals). The wall
                // tilts outward as y grows, expressed as -spread in the y comp.
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3( 1, -spreadXSoft - _ShearX,  0), -emitterWidth  / 2, tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(-1, -spreadXSoft + _ShearX,  0), -emitterWidth  / 2, tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3( 0, -spreadZSoft - _ShearZ,  1), -emitterHeight / 2, tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3( 0, -spreadZSoft + _ShearZ, -1), -emitterHeight / 2, tEntry, tExit);

                // Near cap (y = 0, normal -Y) and far cap (y = beamLength, +Y).
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(0, -1, 0), 0,           tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(0,  1, 0), -beamLength, tEntry, tExit);

                if (tExit <= tEntry) discard;

                DiamondBeamDepthClamp(i, rayDirection, tExit);
                if (tExit <= tEntry) discard;

                // --- Cross-section density falloff (rectangular) -----------
                float tMid          = (tEntry + tExit) * 0.5;
                float3 beamMidpoint = rayOrigin + rayDirection * tMid;
                float  distance     = beamMidpoint.y;                       // metres from emitter

                float diffusionRate = _EdgeSoftness * (0.02 + _HazeDensity);
                float softness      = diffusionRate * distance;

                // Rectangular cross-section: full width grows by 2*spread*d.
                // Add softness so the halo's lateral extent dilutes brightness.
                float crossWidth    = emitterWidth  + 2.0 * spreadX * distance + 2.0 * softness;
                float crossHeight   = emitterHeight + 2.0 * spreadZ * distance + 2.0 * softness;
                float crossArea     = crossWidth * crossHeight;
                float emitterArea   = emitterWidth * emitterHeight;
                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // Soft edges: distance to the nearest of the four geometric walls.
                float geomHalfWidth  = 0.5 * (emitterWidth  + 2.0 * spreadX * distance);
                float geomHalfHeight = 0.5 * (emitterHeight + 2.0 * spreadZ * distance);
                float distFromX      = geomHalfWidth  - abs(beamMidpoint.x);
                float distFromZ      = geomHalfHeight - abs(beamMidpoint.z);
                float distFromWall   = min(distFromX, distFromZ);
                float edgeFactor     = smoothstep(-max(softness, 1e-4), max(softness, 1e-4), distFromWall);

                float haze       = max(_HazeDensity, 0);
                float extinction = exp(-haze * distance);
                float lightFalloff = geometricFalloff * edgeFactor * haze * extinction;

                return DiamondBeamIntegrate(i, rayOrigin, rayDirection,
                    tEntry, tExit, lightFalloff, instColor.rgb, beamIntensity);
            }

            ENDCG
        }
    }
}
