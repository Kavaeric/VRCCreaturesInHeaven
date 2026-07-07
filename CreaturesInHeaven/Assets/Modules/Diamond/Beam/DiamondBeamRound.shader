// Diamond - Beam sub-module (ROUND profile)
// Volumetric light shaft for stage spotlight fixtures with a CIRCULAR emitter
// and a symmetric (circular) cone -- a true spotlight.
//
// Shape-independent machinery lives in DiamondBeamCommon.cginc; this file
// supplies only the circular specifics: an analytic cone (quadric) side-wall
// intersection, a circular cross-section area (pi*r^2), and a radial edge
// softness. The rectangular counterpart is DiamondBeam.shader.
//
// Symmetric by construction: a single spread value (_SpreadX, animated via
// BeamProps.localEulerAngles.x) drives the whole cone. The emitter is a circle
// of radius _EmitterWidth/2 (the fixture's FixtureWidth is its diameter);
// _EmitterHeight and _SpreadZ are unused here. Shear is not supported for the
// round profile (a sheared circular cone is an oblique cone -- out of scope).
//
// The mesh used by this shader should be a UNIT CUBE (corners at +/-0.5). The
// vertex shader expands it to contain the cone's bounding box.

Shader "Diamond/BeamRound"
{
    Properties
    {
        // Emitter DIAMETER, in WORLD-space units (metres). Radius = this / 2.
        _EmitterWidth  ("Emitter Diameter", Float) = 0.5

        // Cone half-angle, expressed as tan(half-angle): the radial widening
        // per unit length. Symmetric, so only X is used.
        _SpreadX ("Spread (tan of half angle)", Float) = 0.0

        // --- Unsupported by the round profile ------------------------------
        // These are declared (so the shared DiamondBeamCommon.cginc and one
        // MaterialPropertyBlock stay shape-agnostic with the rect shader) but
        // never read by the round frag. Hidden so they don't show as inert
        // sliders. Shear specifically is UNSUPPORTED, not merely unwired: a
        // sheared circular cone is an oblique quadric that breaks the cheap
        // analytic intersection -- lean a round beam by rotating the Head.
        [HideInInspector] _EmitterHeight ("Emitter Height (unused)", Float) = 0.5
        [HideInInspector] _SpreadZ ("Spread Z (unused)", Float) = 0.0
        [HideInInspector] _ShearX ("Shear X (unsupported)", Float) = 0.0
        [HideInInspector] _ShearZ ("Shear Z (unsupported)", Float) = 0.0

        // Lateral scattering: how strongly haze softens the beam edge with
        // distance. Unlike Focus (depth-invariant), this is crisp at the emitter
        // and blurs progressively toward the far end as the beam passes through
        // more haze. 0 = no lateral softening; higher = edge diffuses sooner and
        // wider. See _ScatterStrength notes in the frag.
        //
        // Default 0.5: with the fixed rate K=1, the edge reaches FULL blur (the
        // _Focus=0 dome) at d = 1/(haze*strength). At 0.5 and venue haze (~0.03)
        // that's ~67m -- past a typical ~50m throw -- so the edge softens visibly
        // all the way down without ever fully dissolving into a centre-only dome.
        // Raise toward 1 (full blur by ~2/3 down) for heavier-atmosphere looks.
        _ScatterStrength ("Lateral Scatter Strength", Range(0,1)) = 0.5

        _BeamCutoffThreshold ("Beam Cutoff Threshold", Float) = 0.0001
        _BeamLengthMax ("Beam Length Max (metres)", Float) = 50
        _CubeLocalScale ("Cube Local Scale (compensation)", Vector) = (1, 1, 1, 0)
        _Color ("Color", Color) = (1, 1, 1, 1)
        _BeamIntensity ("Intensity", Float) = 1.0
        _HazeDensity ("Haze Density (1/m)", Float) = 0.05
        _EdgeSoftness ("Edge Softness", Float) = 1.0

        // Focus: gobo sharpness (depth-invariant, laser-like across the throw).
        // 1 = crisp circle to the wall; 0 = soft dome, image radius doubled,
        // bright only at the centre. See _Focus notes in DiamondBeamCommon.cginc.
        _Focus ("Focus", Range(0,1)) = 1.0

        // Far-cap fade: fraction of the (auto-derived) beam length over which the
        // beam fades smoothly to zero approaching its far end, so it dissolves
        // instead of ending in a hard-clipped disc. 0 = hard cap; 0.15 = last 15%
        // fades. Range 0..1.
        _FarFade ("Far Cap Fade (fraction)", Range(0,1)) = 0.15

        // Debug visualisation for various components (plain Float -- type the
        // number; Unity's [Enum] drawer caps at 7 entries and we have more):
        //   0 Normal         4 FarFade         8 ScatterProfile
        //   1 RaymarchDepth  5 DAxisIntegral   9 BeamIntegral
        //   2 GeometricFalloff  6 LateralU
        //   3 HazeExtinction    7 Focus
        [DiamondBeamDebugMode] _DebugMode ("Debug Mode", Float) = 0
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

            // --- Circular shape definitions -------------------------------
            // Cross-section is a disc of radius r(d) = R0 + spread*d, where R0
            // is the emitter radius (= emitterWidth/2). Area = pi*r^2. The
            // DERIVE_BEAM_LENGTH macro evaluates these where emitterWidth and
            // spreadX are locals.
            #define DIAMOND_RADIUS0        (emitterWidth * 0.5)
            #define DIAMOND_EMITTER_AREA   (UNITY_PI * DIAMOND_RADIUS0 * DIAMOND_RADIUS0)
            #define DIAMOND_CROSS_AREA(d)  (UNITY_PI * (DIAMOND_RADIUS0 + spreadX * (d)) * (DIAMOND_RADIUS0 + spreadX * (d)))

            // Bounding box is a square that contains the circle: both axes use
            // the single (symmetric) spread.
            #define DIAMOND_BOUNDS_SPREAD_X spreadX
            #define DIAMOND_BOUNDS_SPREAD_Z spreadX

            #include "DiamondBeamCommon.cginc"

            float _DebugMode;   // REBUILD DEBUG (see Properties)

            v2f vert(appdata v) { return DiamondBeamVert(v); }

            // ================================================================
            // REBUILD IN PROGRESS -- STEP 1: GEOMETRY ONLY
            // The frag below computes ONLY the inside-segment [tEntry, tExit] of
            // the camera ray through the cone volume, and outputs its length as
            // grayscale so we can verify the SHAPE in isolation before adding any
            // lighting. No density, no integration, no depth clamp yet.
            // ================================================================

            // Find where the ray is inside the SOLID circular cone, as an interval
            // [coneLo, coneHi]. The cone (within the +Y nappe) is the set
            //   x^2 + z^2 <= (r0 + s*y)^2 .
            // Along the ray this is the quadratic g(t) = a t^2 + b t + c <= 0.
            // Returns false if the ray never enters the solid (within real roots).
            //
            // IMPORTANT: the quadric x^2+z^2 = (r0+s*y)^2 is a DOUBLE cone. Its
            // two nappes meet at the apex y = -r0/s; the MIRROR nappe (y below the
            // apex) is also "solid" to the bare quadratic. When the ray is aimed
            // mostly down the +/-Y axis, a = rd.x^2+rd.z^2 - (s*rd.y)^2 goes
            // NEGATIVE: the bare g(t)<=0 region is then the two OUTER tails
            // (-inf, t0] and [t1, +inf), which straddle the apex and include the
            // mirror nappe. Modelling that as a single interval is what produced
            // the black mirror-cone cutout when looking toward the emitter. So we
            // also require the surface point to lie on the REAL nappe, i.e.
            // radius r0 + s*y >= 0  <=>  y >= -r0/s, and intersect that half-line
            // into the result. (Within the beam's own caps y>=0 this is always
            // satisfied, but the cone interval must enforce it itself so a mirror
            // root never survives the later cap intersection.)
            bool ConeInterval(float3 ro, float3 rd, float r0, float s,
                out float coneLo, out float coneHi)
            {
                coneLo = -1e20; coneHi = 1e20;

                float k  = r0 + s * ro.y;   // R at t = 0
                float kd = s * rd.y;        // dR/dt
                float a = rd.x*rd.x + rd.z*rd.z - kd*kd;
                float b = 2.0 * (ro.x*rd.x + ro.z*rd.z - k*kd);
                float c = ro.x*ro.x + ro.z*ro.z - k*k;

                // We solve g(t) = a t^2 + b t + c = 0 for the surface crossings,
                // then KEEP ONLY crossings on the real nappe (radius r0+s*y >= 0).
                // Restricted to the real nappe the solid cone is convex, so the
                // inside is the single span between the (at most two) valid
                // crossings. This is what kills the mirror-nappe cutout: a root on
                // the wrong nappe is simply discarded instead of seeding a bogus
                // half-infinite interval.
                #define DIAMOND_ON_REAL_NAPPE(t) ((r0 + s * (ro.y + rd.y * (t))) >= -1e-5)

                // Linear degenerate (ray parallel to a cone wall): single crossing.
                if (abs(a) < 1e-7)
                {
                    if (abs(b) < 1e-12)
                    {
                        // Constant g: ray is uniformly in (c<=0) or out (c>0). If
                        // in, the whole real-nappe half-line is inside; clip below.
                        if (c > 0) return false;
                        coneLo = -1e20; coneHi = 1e20;
                    }
                    else
                    {
                        float t = -c / b;
                        // g increasing (b>0): inside for t<=root; decreasing: t>=root.
                        if (b > 0) { coneLo = -1e20; coneHi = t; }
                        else       { coneLo = t;     coneHi = 1e20; }
                    }
                }
                else
                {
                    float disc = b*b - 4.0*a*c;
                    if (disc < 0)
                    {
                        // No real roots: whole ray inside (a<0) or outside (a>0).
                        if (a > 0) return false;
                        coneLo = -1e20; coneHi = 1e20;
                    }
                    else
                    {
                        float sq = sqrt(disc);
                        float t0 = (-b - sq) / (2.0*a);
                        float t1 = (-b + sq) / (2.0*a);
                        if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; }

                        // Validity of each crossing (must touch the real nappe).
                        bool v0 = DIAMOND_ON_REAL_NAPPE(t0);
                        bool v1 = DIAMOND_ON_REAL_NAPPE(t1);

                        if (a > 0)
                        {
                            // Convex bare region [t0,t1]. If a root is on the mirror
                            // nappe, that end is open -> extend it to the apex (the
                            // real cone runs from the valid root out to the apex,
                            // which the cap then trims to y in [0,beamLength]).
                            coneLo = v0 ? t0 : -1e20;
                            coneHi = v1 ? t1 :  1e20;
                            if (!v0 && !v1) return false;   // both on mirror nappe
                        }
                        else
                        {
                            // Bare region is the two outer tails (-inf,t0],[t1,+inf).
                            // On the real nappe at most one tail survives; pick it.
                            if (v0 && !v1)      { coneLo = -1e20; coneHi = t0; }
                            else if (v1 && !v0) { coneLo = t1;    coneHi = 1e20; }
                            else if (v0 && v1)  { coneLo = -1e20; coneHi = 1e20; } // skims apex; cap clips
                            else                return false;
                        }
                    }
                }

                // Final guard: clip the surviving span to the real-nappe half-line
                // so an open (-inf/+inf) end can't leak across the apex.
                if (abs(s) > 1e-9)
                {
                    float yApex = -r0 / s;             // radius = 0 here
                    if (abs(rd.y) < 1e-9)
                    {
                        bool onReal = (s > 0) ? (ro.y >= yApex) : (ro.y <= yApex);
                        if (!onReal) return false;
                    }
                    else
                    {
                        float tApex = (yApex - ro.y) / rd.y;
                        // Real nappe is the side of the apex where radius >= 0.
                        if ((s > 0) == (rd.y > 0)) coneLo = max(coneLo, tApex);
                        else                       coneHi = min(coneHi, tApex);
                    }
                }

                #undef DIAMOND_ON_REAL_NAPPE
                return (coneHi > coneLo);
            }

            // --- Lateral-scatter constants ---------------------------------------
            // Used by the cone-clip reach, the surface probe, and the volumetric
            // integral, so they're declared here before any use site.
            //   WMAX = 1 -> fully-scattered edge matches the _Focus=0 profile
            //               (1 - smoothstep(0,2,u)); spill reaches u = 2.
            //   K        -> diffusion rate: w = sqrt(K*tau*strength), so w hits
            //               WMAX at tau = 1/(K*strength), i.e. distance
            //               d = 1/(K*haze*strength).
            //
            // K is fixed at 1 deliberately -- it's the "law", not an art knob.
            // K=1 anchors full blur to one unit of optical depth (tau=1, the
            // e-fold point of extinction), scaled by strength: full blur at
            // tau = 1/strength. That keeps the haze<->blur relationship
            // dimensionally honest, so _ScatterStrength alone art-directs the
            // look. Retune K only if the underlying diffusion model changes.
            #define DIAMOND_SCATTER_WMAX 1.0
            #define DIAMOND_SCATTER_K    1.0

            // --- Lateral profile (focus x scatter), evaluated at a point --------
            // The beam's lateral brightness at a point is the product of two
            // profiles in the normalized radial coord u = r / R(d):
            //   * focus   -- depth-INVARIANT gobo sharpness (same at every d)
            //   * scatter -- depth-DEPENDENT haze diffusion (crisp near emitter,
            //                soft far), whose blur half-width w GROWS with depth.
            // Factored into one helper so the SURFACE PROBE (debug modes 6-8) and
            // the VOLUMETRIC INTEGRAL (per-substep, inside the d-axis loop) use the
            // identical math and can't drift. `scatterW` is passed in because it is
            // depth-dependent -- the caller computes it from that point's d.
            float DiamondFocusProfile(float u, float focusF)
            {
                // innerEdge = f (full-bright core to here); outerEdge = 2 - f
                // (profile reaches 0 here). f=1 -> hard step at u=1; f=0 ->
                // smoothstep(0,2,u), peak only at centre, image radius doubled.
                return 1.0 - smoothstep(focusF, 2.0 - focusF, u);
            }

            float DiamondScatterProfile(float u, float scatterW)
            {
                // Symmetric softening band centred on the wall (u = 1): at w = 0 a
                // hard step at the wall; as w grows the core shrinks inward AND
                // light spills outward to u = 1 + w. At w = 1 this is
                // 1 - smoothstep(0,2,u), matching the _Focus=0 dome.
                return 1.0 - smoothstep(1.0 - scatterW, 1.0 + scatterW, u);
            }

            // Depth-dependent scatter blur half-width at optical depth tau = haze*d.
            // sqrt(k*tau*strength) (diffusion random-walk growth), clamped to WMAX.
            // See the scatter component block for the full rationale.
            float DiamondScatterWidth(float haze, float d, float strength)
            {
                float tau = max(haze, 0.0) * d;
                return min(sqrt(DIAMOND_SCATTER_K * tau * saturate(strength)), DIAMOND_SCATTER_WMAX);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale).xyz;
                float4 instColor      = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  beamIntensity  = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float  emitterWidth   = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
                float  spreadX        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadX);

                float beamLength;
                DIAMOND_DERIVE_BEAM_LENGTH(beamLength);

                float r0 = emitterWidth * 0.5;

                // Camera ray in BEAM SPACE (origin at emitter centre, +Y along
                // the beam, t in beam-space units).
                float3 cameraObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayOrigin    = cameraObject * cubeLocalScale;
                float3 rayDirection = normalize(i.vertBeamSpace - rayOrigin);

                // 1) Cap slab: 0 <= y <= beamLength, with y(t) = ro.y + rd.y t.
                float tEntry, tExit;
                if (abs(rayDirection.y) < 1e-7)
                {
                    // Ray parallel to caps: inside the slab only if origin is.
                    if (rayOrigin.y < 0 || rayOrigin.y > beamLength) discard;
                    tEntry = -1e20; tExit = 1e20;
                }
                else
                {
                    float tA = (0.0        - rayOrigin.y) / rayDirection.y;
                    float tB = (beamLength - rayOrigin.y) / rayDirection.y;
                    tEntry = min(tA, tB);
                    tExit  = max(tA, tB);
                }

                // 2) Cone, intersected into the slab. Both defocus AND lateral
                // scatter inflate the lit radius, so we clip against the WIDENED
                // cone R'(d) = reach*(r0 + spread*d) that encloses BOTH haloes:
                //   * focus spills out to u = 2 - _Focus   (up to u = 2)
                //   * scatter spills out to u = 1 + wMax   (= u = 2, since wMax = 1)
                // reach is the max of the two so neither halo is ever hard-clipped.
                // Both profiles (shaded below, in the FOCUSED coord u = r/R(d)) fade
                // to 0 before this wall, so the widened clip shows no hard edge.
                // (reach is uniform over depth; near the emitter where scatter w~=0
                // the extra enclosed volume just shades black, same as focus does.)
                float scatterReach = (saturate(_ScatterStrength) > 0.0) ? (1.0 + DIAMOND_SCATTER_WMAX) : 1.0;
                float reach = max(2.0 - saturate(_Focus), scatterReach);   // 1 (crisp) .. 2 (full blur)
                float coneLo, coneHi;
                if (!ConeInterval(rayOrigin, rayDirection, r0 * reach, spreadX * reach, coneLo, coneHi))
                    discard;
                tEntry = max(tEntry, coneLo);
                tExit  = min(tExit,  coneHi);

                // 3) Only the part in front of the camera.
                tEntry = max(tEntry, 0.0);

                // 4) Clamp the far end to the nearest scene surface, so the beam
                // lands ON geometry (floor pool, occlusion) instead of passing
                // through it. Converts scene world-distance into beam-space t
                // (see DiamondBeamDepthClamp -- the cubeLocalScale conversion is
                // what keeps the old on-axis hole from coming back).
                DiamondBeamDepthClamp(i, rayDirection, cubeLocalScale, tExit);

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

                // --- LATERAL INFRA: normalized radial position at the surface --
                // The lateral factors (focus, edge diffusion) operate in "fraction
                // of the way from axis to wall", not absolute metres -- a gobo's
                // profile is depth-invariant in that coordinate. At depth d the
                // cross-section is a disc of radius R(d) = r0 + spread*d, so a point
                // at radial distance r from the axis has u = r / R(d):
                //   u = 0 at the axis, u = 1 at the cone wall.
                // We sample u at the SURFACE HIT (tExit, after the depth clamp), so
                // dropping a plane in the beam paints the cone's cross-section on it
                // -- a real light-pool probe at whatever depth the plane sits.
                float3 exitPt  = rayOrigin + rayDirection * tExit;
                float  exitD   = max(exitPt.y, 0.0);
                float  exitR   = length(exitPt.xz);            // radial dist from axis
                float  exitRad = r0 + spreadX * exitD;         // FOCUSED cone radius R(d)
                float  uLat    = exitR / max(exitRad, 1e-6);   // 0 axis .. 1 wall .. 2 max blur

                // NOTE: the focus + scatter profiles below are the SURFACE PROBE
                // -- sampled once at the exit hit (uLat, exitD) so debug modes 6-8
                // read "what a plane dropped in the beam shows". The actual beam
                // BRIGHTNESS integrates these per-point along the chord (inside the
                // d-axis loop), because both u and the scatter width vary with depth
                // as the ray traverses the volume. Both paths call the same
                // DiamondFocusProfile / DiamondScatterProfile helpers.

                // === COMPONENT: focus (gobo sharpness) =======================
                // Depth-invariant profile in u = r/R(d) (the FOCUSED radius).
                //   innerEdge = f      : full-bright core out to here
                //   outerEdge = 2 - f  : profile reaches 0 here (image radius)
                // f=1 -> step at u=1 (crisp circle to the wall);
                // f=0 -> smoothstep(0,2,u): peak only at centre, 0 at u=2 (doubled).
                float focusF       = saturate(_Focus);
                float focusProfile = DiamondFocusProfile(uLat, focusF);

                // === COMPONENT: lateral scattering (haze edge diffusion) ======
                // Unlike focus (a fixed gobo sharpness at every depth), lateral
                // scattering is depth-DEPENDENT: the beam is crisp at the emitter
                // and softens toward the far end, because light scatters sideways
                // out of the shaft in proportion to how much haze it has passed
                // through. The natural driver is OPTICAL DEPTH tau = haze * d --
                // the same haze*d that drives extinction -- so a denser medium or
                // a longer throw both soften the edge more.
                //
                // We turn tau into a symmetric softening half-width w in the uLat
                // coordinate, centred on the wall (u = 1):
                //   scatterProfile = 1 - smoothstep(1 - w, 1 + w, u)
                // At w = 0 this is a hard step at the wall (crisp); as w grows the
                // core shrinks (inward) AND light spills past the wall (outward),
                // reading as a soft glowing edge.
                //
                // w grows as the SQRT of optical depth, then CLAMPS at wMax = 1:
                //   w = min(sqrt(k * tau * _ScatterStrength), 1)
                // - The sqrt is the physically-motivated shape: edge diffusion
                //   through a scattering medium is a random walk, whose spread
                //   grows like sqrt(number of scattering events) ~ sqrt(sigma_s*d).
                //   With single-scattering albedo ~1 for haze, sigma_s ~ sigma_t =
                //   _HazeDensity, so tau = haze*d IS that event count. (The earlier
                //   linear-in-tau form was legible but phenomenological; this
                //   matches the diffusion approximation of multiple scattering.)
                // - Practically the sqrt grows FAST early then eases, so the blur
                //   ramps in near the emitter and spreads its haze-response across
                //   the beam length instead of concentrating it at one distance.
                // - wMax = 1 is the FULLY-SCATTERED limit: at w = 1 the profile is
                //   1 - smoothstep(0, 2, u), IDENTICAL to the _Focus = 0 look --
                //   a smooth gradient from the axis out to u = 2, only the centre
                //   peaking. Clamping there stops it blurring past that.
                // - w hits 1 at tau = 1/(k*strength), i.e. distance
                //   d = 1 / (k * haze * _ScatterStrength); denser haze pulls that in.
                // - _ScatterStrength scales the effect inside the sqrt (0 disables).
                //
                // NOTE: the outward spill reaches u = 2, so the cone-clip reach
                // (step 2) is widened to enclose 1 + w as well as the focus halo.
                // Surface-probe values (debug 8): width from the exit depth exitD.
                float scatterW       = DiamondScatterWidth(_HazeDensity, exitD, _ScatterStrength);
                float scatterProfile = DiamondScatterProfile(uLat, scatterW);

                // === COMPONENT: geometric falloff ============================
                // The cone widens with distance, so a fixed emitter flux is spread
                // over a larger cross-section -> dimmer. For the circle this is
                // (r0 / (r0 + spread*d))^2 = emitterArea / crossArea(d).
                //   d = 0        -> 1.0 (full, at the emitter)
                //   spread = 0   -> 1.0 everywhere (collimated: no geometric loss)
                //   d increasing -> falls toward 0
                float radius      = r0 + spreadX * dist;
                float crossArea   = UNITY_PI * radius * radius;
                float emitterArea = UNITY_PI * r0 * r0;
                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // === COMPONENT: distance extinction ==========================
                // Light scatters/absorbs out of the beam as it travels through
                // haze (Beer-Lambert): exp(-haze * d). Independent of geometry --
                // depends only on the haze density and distance.
                //   d = 0      -> 1.0 (no haze traversed yet)
                //   haze = 0   -> 1.0 everywhere (no medium)
                //   haze up    -> decays faster (exponential)
                float haze       = max(_HazeDensity, 0.0);
                float extinction = exp(-haze * dist);

                // === COMPONENT: far-cap fade =================================
                // Smoothly fade the beam to zero over the last _FarFade fraction
                // of its (auto-derived) length, so it dissolves instead of ending
                // in a hard-clipped disc at beamLength. Purely a d-axis factor.
                //   d <= fadeStart -> 1.0;  d -> beamLength -> 0.0
                //   _FarFade = 0   -> hard cap (no fade band)
                float fadeStart = beamLength * (1.0 - saturate(_FarFade));
                float farFade   = (beamLength > fadeStart)
                    ? smoothstep(beamLength, fadeStart, dist)   // 1 at start, 0 at cap
                    : 1.0;

                // === INTEGRATION: along the chord ============================
                // The brightness the camera sees is the sum of light scattered
                // toward the eye from EVERY point along the chord [tEntry,tExit],
                // i.e. the integral over t of the PRODUCT of every per-point factor
                // evaluated at that point p(t) = rayOrigin + rayDirection*t:
                //
                //   brightness = INTEGRAL falloff(d)*ext(d)*fade(d)*lateral(u,d) dt
                //
                // Two KINDS of factor appear in that product:
                //   * d-axis only  (falloff, extinction, far-fade): depend solely
                //     on depth d(t), constant across a cross-section slice.
                //   * lateral (focus x scatter): depend on BOTH the radial coord
                //     u(t) = |p(t).xz| / R(d(t)) AND the depth-dependent scatter
                //     width w(d(t)). This is why the lateral term must live INSIDE
                //     the loop and can't be a single multiply on the finished
                //     integral: as the ray traverses the volume, u sweeps across
                //     the cross-section and w widens with depth, so each point
                //     contributes its own softness. The blur is thus cumulative
                //     along the chord -- exactly what a single outer multiply
                //     (which would freeze one u and one w for the whole ray) loses.
                //
                // No clean closed form exists, so we integrate numerically with a
                // FIXED COUNT of midpoint substeps (not a fixed step size): the
                // chord is always split into N pieces no matter how long, so it is
                // length-INDEPENDENT and can't under-sample long beams (the old
                // far-cap ring that scaled with _BeamLengthMax).
                //
                // dAxisIntegral keeps the d-only product (for debug mode 5, so that
                // factor group stays viewable in isolation); beamIntegral is the
                // full product including the lateral term -- the actual brightness.
                #define DIAMOND_DAXIS_STEPS 4

                float dy      = rayDirection.y;           // dd/dt along the ray
                float stepLen = segLen / DIAMOND_DAXIS_STEPS;
                float dAxisIntegral = 0.0;   // d-only factors (debug 5)
                float beamIntegral  = 0.0;   // d-only x lateral (brightness)

                [unroll]
                for (int si = 0; si < DIAMOND_DAXIS_STEPS; si++)
                {
                    float t = tEntry + (si + 0.5) * stepLen;  // midpoint of substep
                    float3 p = rayOrigin + rayDirection * t;  // point on the chord
                    float d = max(p.y, 0.0);                  // depth there

                    // -- falloff(d): r0^2 / (r0 + s*d)^2 --
                    float rad = r0 + spreadX * d;
                    float fFalloff = (r0*r0) / max(rad*rad, 1e-12);

                    // -- extinction(d): exp(-haze*d) --
                    float fExt = exp(-haze * d);

                    // -- farFade(d): smoothstep tail band --
                    float fFade = (beamLength > fadeStart)
                        ? smoothstep(beamLength, fadeStart, d)
                        : 1.0;

                    float dOnly = fFalloff * fExt * fFade;
                    dAxisIntegral += dOnly;

                    // -- lateral(u, d): focus x scatter at THIS point --
                    // u sweeps across the cross-section as the ray cuts through;
                    // the scatter width widens with this point's own depth d.
                    float uHere   = length(p.xz) / max(rad, 1e-6);   // R(d) = rad
                    float wHere   = DiamondScatterWidth(haze, d, _ScatterStrength);
                    float fLat    = DiamondFocusProfile(uHere, focusF)
                                  * DiamondScatterProfile(uHere, wHere);

                    beamIntegral += dOnly * fLat;
                }
                dAxisIntegral *= stepLen;   // midpoint rule: sum * substep width
                beamIntegral  *= stepLen;

                // --- Debug dispatch ------------------------------------------
                // Additive blend on a dark scene -> grayscale reads directly.
                // Each mode shows ONE component so we can verify it in isolation.
                if (_DebugMode < 1.5)   return fixed4(segLen.xxx * 0.1, 1);          // 1 (and 0 for now): segment length
                if (_DebugMode < 2.5)   return fixed4(geometricFalloff.xxx, 1);      // 2: geometric falloff (0..1)
                if (_DebugMode < 3.5)   return fixed4(extinction.xxx, 1);            // 3: distance extinction (0..1)
                if (_DebugMode < 4.5)   return fixed4(farFade.xxx, 1);               // 4: far-cap fade (0..1)
                if (_DebugMode < 5.5)   return fixed4((dAxisIntegral).xxx, 1);// 5: d-axis integral (falloff x extinction x fade)
                if (_DebugMode < 6.5)   return fixed4(uLat.xxx, 1);                  // 6: lateral coord u at surface (0 axis .. 1 wall)
                if (_DebugMode < 7.5)   return fixed4(focusProfile.xxx, 1);          // 7: focus profile at surface (1 core .. 0 edge)
                if (_DebugMode < 8.5)   return fixed4(scatterProfile.xxx, 1);        // 8: lateral scatter profile (crisp at emitter .. soft far)
                if (_DebugMode < 9.5)   return fixed4(beamIntegral.xxx, 1);          // 9: full integral (d-axis x lateral) -- brightness
                return fixed4(beamIntegral.xxx, 1);
            }

            ENDCG
        }
    }
}
