// Diamond - Beam sub-module (round profile)
// Volumetric light shaft for stage spotlight fixtures with a circular emitter
// and a symmetric (circular) cone, such as that for a spotlight.
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
// round profile as of present.
//
// The mesh used by this shader should be a unit cube (corners at +/-0.5). The
// vertex shader expands it to contain the cone's bounding box.

Shader "Diamond/BeamRound"
{
    Properties
    {
        // Emitter diameter, in world-space units (metres). Radius = this / 2.
        _EmitterWidth  ("Emitter Diameter", Float) = 0.5

        // Cone half-angle, expressed as tan(half-angle): the radial widening
        // per unit length. Symmetric, so only X is used.
        _SpreadX ("Spread (tan of half angle)", Float) = 0.0

        // --- Unsupported by the round profile ------------------------------
        // These are declared (so the shared DiamondBeamCommon.cginc and one
        // MaterialPropertyBlock stay shape-agnostic with the rect shader) but
        // never read by the round frag. Hidden so they don't show as inert
        // sliders.
        //
        // Shear specifically is unsupported, a sheared circular cone is an
        // oblique quadric that breaks the cheap analytic intersection.
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

        // Anisotropy (Henyey-Greenstein g): how forward-biased the haze scatters
        // light toward the eye. Drives the view-dependent brightness -- the beam is
        // brighter looked at TOWARD the emitter (forward scatter) than across/behind.
        //   0    = isotropic (uniform in all directions; the old flat look)
        //   0..1 = forward scatter (real haze/fog ~0.6-0.8)
        //  -1..0 = back scatter (unusual; brighter from behind the light)
        _Anisotropy ("Anisotropy (HG g)", Range(-0.95, 0.95)) = 0.6

        _BeamCutoffThreshold ("Beam Cutoff Threshold", Float) = 0.0001
        _BeamLengthMax ("Beam Length Max (metres)", Float) = 50
        _CubeLocalScale ("Cube Local Scale (compensation)", Vector) = (1, 1, 1, 0)
        _Color ("Color", Color) = (1, 1, 1, 1)
        _BeamIntensity ("Intensity", Float) = 1.0
        _HazeDensity ("Haze Density (1/m)", Float) = 0.05

        // Focus: how fast the cone defocuses with distance, RELATIVE to its own
        // spread. Perfectly focused at the emitter, spreading more toward the far end.
        //   1 = perfectly collimated (crisp; only haze softens the edge)
        //   0 = defocuses to DOUBLE the cone half-angle downrange
        // Rate is proportional to spreadX, so narrow/wide beams defocus by the same
        // proportion (a collimated spreadX=0 beam is inert). See _Focus /
        // DiamondFocusSpill notes in the frag.
        _Focus ("Focus", Range(0,1)) = 1.0

        // Far-cap fade: fraction of the (auto-derived) beam length over which the
        // beam fades smoothly to zero approaching its far end, so it dissolves
        // instead of ending in a hard-clipped disc. 0 = hard cap; 0.15 = last 15%
        // fades. Range 0..1.
        _FarFade ("Far Cap Fade (fraction)", Range(0,1)) = 0.15

        // Debug visualisation for various components (plain Float; the
        // [DiamondBeamDebugMode] drawer gives a named dropdown). Keep in sync with
        // the frag dispatch chain AND DiamondBeamDebugModeDrawer.cs:
        //   0 Normal           4 FarCapFade      8 VertexBounds
        //   1 RaymarchDepth    5 DAxisIntegral   9 HGPhase
        //   2 GeometricFalloff 6 LateralU
        //   3 HazeExtinction   7 LateralEdge
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
            // The frag below computes only the inside-segment [tEntry, tExit] of
            // the camera ray through the cone shape, and outputs its length as
            // grayscale so we can verify the SHAPE in isolation before adding any
            // lighting. No density, no integration, no depth clamp yet.
            // ================================================================

            // Find where the ray is inside the solid circular cone, as an interval
            // [coneLo, coneHi]. The cone (within the +Y nappe) is the set
            //   x^2 + z^2 <= (r0 + s*y)^2 .
            // Along the ray this is the quadratic g(t) = a t^2 + b t + c <= 0.
            // Returns false if the ray never enters the solid (within real roots).
            //
            // IMPORTANT: the quadric x^2+z^2 = (r0+s*y)^2 is a double cone. Its
            // two nappes meet at the apex y = -r0/s; the mirror nappe (y below the
            // apex) is also "solid" to the bare quadratic. When the ray is aimed
            // mostly down the +/-Y axis, a = rd.x^2+rd.z^2 - (s*rd.y)^2 goes
            // NEGATIVE: the bare g(t)<=0 region is then the two outer tails
            // (-inf, t0] and [t1, +inf), which straddle the apex and include the
            // mirror nappe. Modelling that as a single interval is what produced
            // the black mirror-cone cutout when looking toward the emitter. So we
            // also require the surface point to lie on the real nappe, i.e.
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
                // then keep only crossings on the real nappe (radius r0+s*y >= 0).
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

            // Lateral diffusion rate DIAMOND_SCATTER_K lives in DiamondBeamCommon.cginc
            // (shared with the vert bounding box). See the metric-spill note there.

            // --- Lateral edge profile (one fuzzy edge) --------------------------
            // The beam's lateral brightness across the cross-section is a single
            // soft-edged profile in the normalized radial coord u = r / R(d):
            //   u = 0 axis, u = 1 wall (sharp-edge radius), u = 1 + w outer spill.
            // The half-width w handed in is not a free number: it is spill_m(d)/R(d),
            // where spill_m is an absolute metric spill (see DiamondEdgeWidth), so the
            // outer edge lands at metres R(d) + spill_m, a straight envelope. This
            // profile just draws the soft step; the anti-bow geometry is upstream.
            //
            //   DiamondEdgeProfile(u, w) = 1 - smoothstep(1 - w, 1 + w, u)
            //
            // w is the blur half-width in u, symmetric about the wall (u = 1):
            //   w = 0 -> hard step at the wall (razor-sharp circle);
            //   w = 1 -> 1 - smoothstep(0, 2, u): the wall softens out to u = 2 (image
            //            radius doubled). w has no fixed ceiling; it keeps growing with
            //            depth, but since spill_m grows linearly and R(d) also grows, w
            //            itself stays bounded in practice -- the edge just stays soft.
            //
            // We add their metric spills in quadrature (variances add, the way real optical
            // blurs stack) into one spill_m, then draw one edge. So there is only ever a
            // single edge with a single softness, producing genuine "halfway" blur values
            // instead of weird superpositions.
            //
            //   focusSpill   = DiamondFocusSpill(...)     (metres; grows with depth, _Focus rate)
            //   scatterSpill = DiamondScatterSpill(...)   (metres; grows with haze*depth)
            //   spill_m      = sqrt(focusSpill^2 + scatterSpill^2);  w = spill_m / R(d)
            float DiamondEdgeProfile(float u, float w)
            {
                return 1.0 - smoothstep(1.0 - w, 1.0 + w, u);
            }

            // === Lateral blur is measured in world-space metres, not normalized u ===
            // The edge profile lives in u = r/R(d), so a width w there places the
            // outer spill at u = 1 + w, i.e. metres (1 + w)*R(d) = R(d) + w*R(d). That
            // trailing *R(d) makes the spill inherit the cone taper and multiply it, so
            // a depth-growing w bows the envelope outward (the flare artifact). The fix
            // is to define the spill as an absolute metric amount spill_m(d) added to
            // the wall: outer edge = R(d) + spill_m(d). Straight iff spill_m is linear
            // in d. We then convert back to the profile's u-space at the point of use
            // via w = spill_m / R(d), so the profile/probe/debug all stay in u while the
            // geometry of the edge is decoupled from the taper.
            //
            // Growth is linear in d (not the old sqrt): sqrt was the diffusion shape but
            // it is itself super-linear, so even a metric sqrt spill would still bow.
            // Linear metric spill gives a genuinely straight edge, which is the whole
            // reason for moving to metres. (If a softer near-source ramp is wanted later
            // it can be added without re-coupling to R(d).)

            // Haze scatter spill in metres at depth d. Driven by optical depth
            // tau = haze*d and _ScatterStrength; linear in d. No WMAX clamp -- the
            // straight metric edge doesn't flat-line or bow, so it needs no ceiling
            // here (the vert box / cone-clip still bound it geometrically).
            float DiamondScatterSpill(float haze, float d, float strength)
            {
                return DIAMOND_SCATTER_K * max(haze, 0.0) * max(d, 0.0) * saturate(strength);
            }

            // Focus spill in metres at depth d. Focus defocuses even in clear air (no
            // haze coefficient): sharp at the emitter (d = 0 -> 0 spill), spreading with
            // distance. The rate is RELATIVE TO THE CONE'S OWN SPREAD, not an absolute
            // metric rate: spill_focus = (1 - _Focus) * spreadX * d. This is the key to
            // it feeling intuitive across beam widths -- an absolute rate made a narrow
            // beam defocus to a huge angle while a wide beam barely moved, because the
            // eye reads defocus as a FRACTION of the cone's own angle, not in metres.
            // Tying it to spreadX makes narrow and wide beams defocus by the same
            // PROPORTION of their spread.
            //   _Focus = 1 -> rate 0 (crisp wall at R(d))
            //   _Focus = 0 -> rate spreadX, so the outer edge sits at
            //                 R(d) + spreadX*d = r0 + 2*spreadX*d -- exactly DOUBLE the
            //                 cone half-angle (the agreed spec).
            // Consequence: a perfectly collimated beam (spreadX = 0) has no cone angle
            // to double, so focus is inert there (haze scatter still softens it). K is
            // kept as the shared diffusion scale so focus and scatter stay commensurate.
            float DiamondFocusSpill(float focusF, float spreadX, float d)
            {
                float rate = (1.0 - saturate(focusF)) * max(spreadX, 0.0);
                return DIAMOND_SCATTER_K * rate * max(d, 0.0);
            }

            // Combine the two metric spills (focus and haze scatter) into one spill
            // in metres via quadrature (both are diffusion-like spreads, variances add),
            // then convert to the profile's u-space half-width by dividing by the cone
            // radius R(d) at this depth. The division is the only place R(d) enters the
            // blur, and because we add spill in metres before dividing, the outer edge
            // is R(d) + spill_m -- straight, not the bowed (1 + w)*R(d).
            float DiamondEdgeWidth(float focusF, float spreadX, float haze, float d, float strength, float radiusAtD)
            {
                float focusSpill   = DiamondFocusSpill(focusF, spreadX, d);
                float scatterSpill = DiamondScatterSpill(haze, d, strength);
                float spillMetres  = sqrt(focusSpill*focusSpill + scatterSpill*scatterSpill);
                return spillMetres / max(radiusAtD, 1e-6);   // -> u-space half-width
            }

            // --- Henyey-Greenstein phase function -------------------------------
            // The fraction of light scattered toward the eye at a point, as a function
            // of the scattering angle theta = angle between the light's TRAVEL direction
            // and the direction to the camera. This is what makes the beam view-
            // dependent: bright looking back toward the emitter (forward scatter, small
            // theta), dim across/behind it.
            //
            //   p(cosTheta) = (1/4pi) * (1 - g^2) / (1 + g^2 - 2 g cosTheta)^(3/2)
            //
            //   g = 0    -> isotropic (1/4pi everywhere; the old flat look)
            //   g -> +1  -> sharp forward scatter (real haze/fog g ~ 0.6-0.8)
            //   g -> -1  -> back scatter
            //
            // cosTheta = dot(lightTravelDir, viewDir). Both must be in the SAME space
            // (we use beam space). lightTravelDir is where the beam's light is going at
            // this point -- it fans out from the apex, so lightTravelDir =
            // normalize(p - apex), NOT a constant axis (matters for wide cones).
            // The 1/4pi keeps it a normalized phase function (integrates to 1 over the
            // sphere); harmless as a constant scale that _BeamIntensity absorbs.
            float DiamondHGPhase(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;   // > 0 for |g| < 1
                denom = max(denom, 1e-6);                      // guard fp edge at grazing
                return (1.0 / (4.0 * UNITY_PI)) * (1.0 - g2) * rsqrt(denom * denom * denom);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                // === DEBUG: vertex-displacement bounds (mode 8) ==============
                // Faint red over every rasterised fragment of the expanded cube
                // (ExpandUnitCubeToFrustumBounds), returned before any cap/cone/
                // depth discard, so it shows the whole bounding box, including the
                // empty margin the beam doesn't fill. Use it to check the box isn't
                // clipping the halo (too tight) or wastefully huge (overdraw). The
                // beam shape is intentionally not visible in this mode. 0.05 reads on
                // the One-One additive blend against a dark scene.
                if (_DebugMode > 7.5 && _DebugMode < 8.5)
                    return fixed4(0.05, 0.0, 0.0, 1.0);

                float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale).xyz;
                float4 instColor      = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  beamIntensity  = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float  emitterWidth   = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
                float  spreadX        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadX);

                float beamLength;
                DIAMOND_DERIVE_BEAM_LENGTH(beamLength);

                float r0 = emitterWidth * 0.5;

                // Camera ray in beam space (origin at emitter centre, +Y along
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

                // 2) Cone, intersected into the slab. Defocus and haze scatter inflate
                // the lit radius by a metric spill spill_m(d) added to the wall, so the
                // outer lit edge is R(d) + spill_m(d) (see DiamondEdgeWidth). Both spills
                // are linear in d and zero at d = 0, so their sum is itself a cone: the
                // widened clip cone shares the emitter radius r0 (no over-radius at the
                // source) and just gets a steeper spread. The extra spread is the spill
                // RATE: focus rate (1 - _Focus)*spreadX (proportional to the cone's own
                // spread) and scatter rate haze*strength, combined in quadrature to match
                // DiamondFocusSpill / DiamondScatterSpill. Because it's an additive spread
                // bump (not a multiplicative reach), the clip wall is straight and hugs
                // the real spill instead of over-widening. Both profiles fade to 0 before
                // this wall, so the widened clip shows no hard edge; near the emitter where
                // spill ~= 0 the extra enclosed volume just shades black.
                float focusRate    = DIAMOND_SCATTER_K * (1.0 - saturate(_Focus)) * max(spreadX, 0.0);
                float scatterRate  = DIAMOND_SCATTER_K * max(_HazeDensity, 0.0) * saturate(_ScatterStrength);
                float spillSpread  = sqrt(focusRate*focusRate + scatterRate*scatterRate);
                float coneLo, coneHi;
                if (!ConeInterval(rayOrigin, rayDirection, r0, spreadX + spillSpread, coneLo, coneHi))
                    discard;
                tEntry = max(tEntry, coneLo);
                tExit  = min(tExit,  coneHi);

                // 3) Only the part in front of the camera.
                tEntry = max(tEntry, 0.0);

                // 4) Clamp the far end to the nearest scene surface, so the beam
                // lands on geometry (floor pool, occlusion) instead of passing
                // through it. Converts scene world-distance into beam-space t
                // (see DiamondBeamDepthClamp -- the cubeLocalScale conversion is
                // what keeps the old on-axis hole from coming back).
                DiamondBeamDepthClamp(i, rayDirection, cubeLocalScale, tExit);

                if (tExit <= tEntry) discard;

                // --- Sample point: segment entry (front face of the slice) ---
                // Geometric falloff and extinction depend only on the fore/aft
                // distance d, so they're constant across any cross-section slice.
                // Sampling at the ENTRY point shows the factor at the near surface
                // the pixel is looking at -- view it side-on, where entry-d is the
                // depth of the slice you see. Down-axis views read entry-d ~= 0.
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
                // We sample u at the surface hit (tExit, after the depth clamp), so
                // dropping a plane in the beam paints the cone's cross-section on it.
                float3 exitPt  = rayOrigin + rayDirection * tExit;
                float  exitD   = max(exitPt.y, 0.0);
                float  exitR   = length(exitPt.xz);            // radial dist from axis
                float  exitRad = r0 + spreadX * exitD;         // FOCUSED cone radius R(d)
                float  uLat    = exitR / max(exitRad, 1e-6);   // 0 axis .. 1 wall .. 2 max blur

                // NOTE: the lateral edge below is the surface probe -- sampled once
                // at the exit hit (uLat, exitD) so debug modes 6-8 read "what a plane
                // dropped in the beam shows". The actual beam brightness integrates
                // the edge per-point along the chord (inside the d-axis loop),
                // because both u and the blur width vary with depth as the ray
                // traverses the volume. Both paths call the same helpers.

                // === COMPONENT: lateral edge (focus + scatter, one edge) ======
                // A single soft-edged profile across the cross-section. Its blur
                // half-width w comes from two sources, combined in quadrature (see
                // DiamondEdgeWidth / DiamondEdgeProfile above) so there's exactly one
                // edge, never a hard-over-blurred superposition:
                //
                // Both are depth-dependent spills in metres: zero at the emitter,
                // growing linearly with distance. So the beam leaves crisp at r0 and
                // softens downrange without bowing the edge. They differ only in drive:
                //   focusSpill   -- K*(1-_Focus)*d: defocus with distance, driven by
                //                   _Focus alone (no haze coefficient), so it happens
                //                   even in clear air. focus 1 -> 0; focus 0 -> fastest.
                //   scatterSpill -- K*tau*strength, tau = haze*d: haze diffusion, driven
                //                   by optical depth (the same haze*d that drives
                //                   extinction). _ScatterStrength scales it (0 = off).
                // Combined in quadrature (metres), then DiamondEdgeWidth divides by the
                // cone radius R(d) to hand the profile a u-space half-width. Because the
                // spills are added in metres before that divide, the outer edge sits at
                // R(d) + spill_m -- a straight envelope, not the bowed (1 + w)*R(d).
                // Surface-probe uses the exit depth exitD and its radius exitRad.
                float focusF     = saturate(_Focus);
                float edgeW      = DiamondEdgeWidth(focusF, spreadX, _HazeDensity, exitD, _ScatterStrength, exitRad);
                float edgeProfile = DiamondEdgeProfile(uLat, edgeW);

                // === COMPONENT: Henyey-Greenstein phase (view-dependent) ======
                // Scattering angle theta = angle between the light's travel direction
                // and the direction to the camera. Surface probe evaluates it at the
                // exit hit; mode 9 shows p(theta) raw.
                float3 beamApex     = float3(0.0, (abs(spreadX) > 1e-6) ? (-r0 / spreadX) : 0.0, 0.0);
                float3 lightDirExit = normalize(exitPt - beamApex);
                float3 viewDirExit  = normalize(rayOrigin - exitPt);
                float  cosThetaExit = dot(lightDirExit, viewDirExit);
                float  hgPhase      = DiamondHGPhase(cosThetaExit, _Anisotropy);

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
                // toward the eye from every point along the chord [tEntry,tExit],
                // i.e. the integral over t of the product of every per-point factor
                // evaluated at that point p(t) = rayOrigin + rayDirection*t:
                //
                //   brightness = INTEGRAL falloff(d)*ext(d)*fade(d)*lateral(u,d) dt
                //
                // Two kinds of factor appear in that product:
                //   * d-axis only  (falloff, extinction, far-fade): depend solely
                //     on depth d(t), constant across a cross-section slice.
                //   * lateral (focus x scatter): depend on both the radial coord
                //     u(t) = |p(t).xz| / R(d(t)) and the depth-dependent scatter
                //     width w(d(t)). This is why the lateral term must live inside
                //     the loop and can't be a single multiply on the finished
                //     integral: as the ray traverses the volume, u sweeps across
                //     the cross-section and w widens with depth, so each point
                //     contributes its own softness. The blur is thus cumulative
                //     along the chord -- exactly what a single outer multiply
                //     (which would freeze one u and one w for the whole ray) loses.
                //
                // No clean closed form exists, so we integrate numerically with a
                // fixed count of midpoint substeps (not a fixed step size): the
                // chord is always split into N pieces no matter how long, so it is
                // length-independent and can't under-sample long beams (the old
                // far-cap ring that scaled with _BeamLengthMax).
                //
                // dAxisIntegral keeps the d-only product (for debug mode 5, so that
                // factor group stays viewable in isolation). beamIntegral is the FULL
                // brightness: the same chord integral with the lateral edge folded in
                // PER SUBSTEP.
                //
                // Why the lateral term lives inside the loop (not one outer multiply of
                // the finished dAxisIntegral): dAxisIntegral is already a collapsed sum,
                // so multiplying it by a single lateral value would compute
                //   (INTEGRAL dOnly dt) x lateral(one point)
                // whereas the correct brightness is
                //   INTEGRAL dOnly(t) x lateral(u(t), spill(t)) dt.
                // The lateral weight varies along the chord: as the ray cuts through the
                // cone its radial coord u(t) = |p.xz|/R(d) sweeps across the section, and
                // the metric spill width grows with depth. Freezing one u + one width for
                // the whole ray throws away the cumulative edge blur that makes it read
                // as volumetric -- so each substep gets its OWN lateral factor.
                //
                // 8 substeps (up from 4): the d-axis factors integrate fine at 4, but the
                // lateral edge varies fastest on grazing rays (u sweeps a lot per chord),
                // which under-samples and aliases the soft edge at 4. Still a fixed count
                // (length-independent), so no far-cap ring.
                #define DIAMOND_DAXIS_STEPS 4

                float dy      = rayDirection.y;           // dd/dt along the ray
                float stepLen = segLen / DIAMOND_DAXIS_STEPS;
                float dAxisIntegral = 0.0;   // d-only factors (debug 5)
                float beamIntegral  = 0.0;   // d-only x lateral (real brightness, mode 0)

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

                    // -- lateral edge at this point: u sweeps, spill grows with depth --
                    // Same metric-spill helpers as the surface probe (can't drift): the
                    // width is spill_m(d)/R(d) via DiamondEdgeWidth, drawn by the one
                    // soft-edge profile. rad IS R(d) at this substep.
                    float uHere = length(p.xz) / max(rad, 1e-6);
                    float wHere = DiamondEdgeWidth(focusF, spreadX, haze, d, _ScatterStrength, rad);
                    float fLat  = DiamondEdgeProfile(uHere, wHere);

                    // -- flux conservation as the edge spills wider --
                    // The geometric falloff (fFalloff) conserves flux over the disc of
                    // radius R(d) only. But focus/haze spill widens the lit disc beyond
                    // R(d), spreading the same flux over a larger area -> it must dim
                    // more. The edge profile's area integral is J(w) = 1/2 + w^2/10
                    // (closed form of the ∫[1-smoothstep]·u du over the section), so the
                    // spilled area is πR(d)^2·(1 + w^2/5). The flux-conserving factor is
                    // the hard-disc area over the spilled area:
                    //   J(0)/J(w) = 1 / (1 + w^2/5).
                    // One multiply/add/divide -- the integral was solved offline, so no
                    // per-pixel numerical integration. Exact for w<=1; for w>1 it gently
                    // over-dims (always in the safe direction), which is fine.
                    float fFluxNorm = 1.0 / (1.0 + wHere*wHere * 0.2);

                    // -- Henyey-Greenstein phase at this point (view-dependent) --
                    // Each substep has its OWN scattering angle: lightTravelDir fans out
                    // from the apex (normalize(p - apex)) and viewDir points back to the
                    // camera, both in beam space. So the phase must be per-substep, not a
                    // single outer multiply -- as the ray traverses, cosTheta changes.
                    // Same helper/apex as the surface probe (can't drift).
                    float3 lightDirHere = normalize(p - beamApex);
                    float3 viewDirHere  = normalize(rayOrigin - p);
                    float  phaseHere    = DiamondHGPhase(dot(lightDirHere, viewDirHere), _Anisotropy);

                    beamIntegral += dOnly * fLat * fFluxNorm * phaseHere;
                }
                dAxisIntegral *= stepLen;   // midpoint rule: sum * substep width
                beamIntegral  *= stepLen;

                // --- Debug dispatch ------------------------------------------
                // Additive blend on a dark scene -> grayscale reads directly.
                // Each mode shows one component so we can verify it in isolation.
                // (mode 8 VertexBounds is handled by the early return at the top.)
                //
                // Mode 0 is checked first and on its own: it is the smallest value, so a
                // `< 1.5`-style lower bound would also swallow it.
                if (_DebugMode < 0.5)                             // 0 / normal: the real beam
                    return fixed4(instColor.rgb * beamIntegral * beamIntensity, 1);
                if (_DebugMode < 1.5)   return fixed4(segLen.xxx * 0.1, 1);          // 1: segment length
                if (_DebugMode < 2.5)   return fixed4(geometricFalloff.xxx, 1);      // 2: geometric falloff (0..1)
                if (_DebugMode < 3.5)   return fixed4(extinction.xxx, 1);            // 3: distance extinction (0..1)
                if (_DebugMode < 4.5)   return fixed4(farFade.xxx, 1);               // 4: far-cap fade (0..1)
                if (_DebugMode < 5.5)   return fixed4((dAxisIntegral).xxx, 1);       // 5: d-axis integral (falloff x extinction x fade)
                if (_DebugMode < 6.5)   return fixed4(uLat.xxx, 1);                  // 6: lateral coord u at surface (0 axis .. 1 wall .. spill)
                if (_DebugMode < 7.5)   return fixed4(edgeProfile.xxx, 1);           // 7: lateral edge at surface (metric spill, one edge)
                // (mode 8 VertexBounds returns at the top of frag.)
                if (_DebugMode < 9.5)   return fixed4(hgPhase.xxx, 1);               // 9: HG phase at surface (raw p(theta); sweep _Anisotropy)

                // Safe fallback for any out-of-range _DebugMode: the real beam.
                return fixed4(instColor.rgb * beamIntegral * beamIntensity, 1);
            }

            ENDCG
        }
    }
}
