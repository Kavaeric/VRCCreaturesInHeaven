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

        // Far-cap fade: fraction of the (auto-derived) beam length over which the
        // beam fades smoothly to zero approaching its far end, so it dissolves
        // instead of ending in a hard-clipped face. 0 = hard cap; 0.15 = last 15%
        // fades. Range 0..1.
        _FarFade ("Far Cap Fade (fraction)", Range(0,1)) = 0.15

        // Debug visualisation for various components. 0 = normal.
        [Enum(Normal,0,RaymarchDepth,1,GeometricFalloff,2,HazeExtinction,3,FarFade,4)] _DebugMode ("Debug Mode", Float) = 0
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

            float _DebugMode;   // REBUILD DEBUG (see Properties)

            v2f vert(appdata v) { return DiamondBeamVert(v); }

            // ================================================================
            // REBUILD IN PROGRESS -- STEP 1: GEOMETRY ONLY
            // Computes ONLY the inside-segment [tEntry, tExit] of the camera ray
            // through the truncated-pyramid volume, output as grayscale length so
            // the SHAPE can be verified before any lighting. Mirrors the round
            // shader's Step 1. No density, integration, or depth clamp yet.
            // ================================================================

            // Clip [tEntry, tExit] to where a LINEAR f(t) = f0 + fd*t stays <= 0.
            void ClipLinear(float f0, float fd, inout float tEntry, inout float tExit)
            {
                if (abs(fd) < 1e-7) { if (f0 > 0) { tEntry = 1e20; tExit = -1e20; } return; }
                float t = -f0 / fd;
                if (fd > 0) tExit  = min(tExit,  t);
                else        tEntry = max(tEntry, t);
            }

            // Clip to where one lateral coordinate stays within a leaning, widening
            // band: |coord(t) - shear*y(t)| <= halfEmit + spread*y(t).
            //   coord(t) = c0 + cd*t,  y(t) = yo + yd*t.
            // Two linear half-spaces (the two walls) -> stays convex, one interval.
            void ClipSlab(float c0, float cd, float yo, float yd,
                float halfEmit, float spread, float shear,
                inout float tEntry, inout float tExit)
            {
                float u0 = c0 - shear * yo;          // coord - centre, at t = 0
                float ud = cd - shear * yd;          // d/dt of that
                float h0 = halfEmit + spread * yo;   // half-width at t = 0
                float hd = spread * yd;              // d/dt of half-width
                ClipLinear( u0 - h0,  ud - hd, tEntry, tExit);   //  u - half <= 0
                ClipLinear(-u0 - h0, -ud - hd, tEntry, tExit);   // -u - half <= 0
            }

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

                // Camera ray in BEAM SPACE (origin at emitter centre, +Y along
                // the beam, t in beam-space units).
                float3 cameraObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayOrigin    = cameraObject * cubeLocalScale;
                float3 rayDirection = normalize(i.vertBeamSpace - rayOrigin);

                float tEntry = -1e20;
                float tExit  =  1e20;

                // 1) Cap slab: 0 <= y <= beamLength.
                ClipLinear(-rayOrigin.y,              -rayDirection.y, tEntry, tExit);   // y >= 0
                ClipLinear( rayOrigin.y - beamLength,  rayDirection.y, tEntry, tExit);   // y <= beamLength

                // 2) Lateral walls (geometric, no soft widening yet).
                ClipSlab(rayOrigin.x, rayDirection.x, rayOrigin.y, rayDirection.y,
                    emitterWidth  * 0.5, spreadX, _ShearX, tEntry, tExit);
                ClipSlab(rayOrigin.z, rayDirection.z, rayOrigin.y, rayDirection.y,
                    emitterHeight * 0.5, spreadZ, _ShearZ, tEntry, tExit);

                // 3) Only the part in front of the camera.
                tEntry = max(tEntry, 0.0);

                if (tExit <= tEntry) discard;

                // --- Sample point: segment ENTRY (front face of the slice) ---
                // Geometric falloff and extinction depend only on the fore/aft
                // distance d, so they're constant across any cross-section slice.
                // Sampling at the ENTRY point shows the factor at the near surface
                // the pixel is looking at -- view it SIDE-ON, where entry-d is the
                // depth of the slice you see. (Down-axis views read entry-d ~= 0.)
                float  segLen   = tExit - tEntry;
                float3 entryPt  = rayOrigin + rayDirection * tEntry;
                float  dist     = max(entryPt.y, 0.0);    // metres from emitter

                // === COMPONENT: geometric falloff ============================
                // The pyramid widens with distance, so a fixed emitter flux is
                // spread over a larger cross-section -> dimmer.
                //   geometricFalloff = emitterArea / crossArea(d)
                //   d = 0      -> 1.0;  spread = 0 -> 1.0 everywhere (collimated)
                float crossWidth  = emitterWidth  + 2.0 * spreadX * dist;
                float crossHeight = emitterHeight + 2.0 * spreadZ * dist;
                float crossArea   = crossWidth * crossHeight;
                float emitterArea = emitterWidth * emitterHeight;
                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // === COMPONENT: distance extinction ==========================
                // Light scatters/absorbs out of the beam through haze
                // (Beer-Lambert): exp(-haze * d). Independent of geometry.
                //   d = 0 -> 1.0;  haze = 0 -> 1.0 everywhere;  haze up -> faster.
                float haze       = max(_HazeDensity, 0.0);
                float extinction = exp(-haze * dist);

                // === COMPONENT: far-cap fade =================================
                // Smoothly fade the beam to zero over the last _FarFade fraction
                // of its (auto-derived) length, so it dissolves instead of ending
                // in a hard-clipped face at beamLength. Purely a d-axis factor.
                //   d <= fadeStart -> 1.0;  d -> beamLength -> 0.0
                //   _FarFade = 0   -> hard cap (no fade band)
                float fadeStart = beamLength * (1.0 - saturate(_FarFade));
                float farFade   = (beamLength > fadeStart)
                    ? smoothstep(beamLength, fadeStart, dist)   // 1 at start, 0 at cap
                    : 1.0;

                // --- Debug dispatch ------------------------------------------
                // Additive blend on a dark scene -> grayscale reads directly.
                if (_DebugMode < 1.5)   return fixed4(segLen.xxx * 0.1, 1);          // 1 (and 0 for now): segment length
                if (_DebugMode < 2.5)   return fixed4(geometricFalloff.xxx, 1);      // 2: geometric falloff (0..1)
                if (_DebugMode < 3.5)   return fixed4(extinction.xxx, 1);            // 3: distance extinction (0..1)
                if (_DebugMode < 4.5)   return fixed4(farFade.xxx, 1);               // 4: far-cap fade (0..1)
                return fixed4(farFade.xxx, 1);
            }

            ENDCG
        }
    }
}
