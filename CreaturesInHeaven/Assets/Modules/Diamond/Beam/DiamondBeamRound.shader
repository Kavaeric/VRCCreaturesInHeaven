// DiamondBeamRound.shader
//
// A volumetric light shaft for stage spotlight fixtures: a circular emitter and a
// symmetric circular cone, like a spotlight.
//
// The shape-independent machinery lives in DiamondBeamCommon.cginc. This file adds
// only the circular specifics: an analytic cone (quadric) side-wall intersection, a
// circular cross-section area (pi*r^2), and a radial edge softness. The rectangular
// counterpart is DiamondBeam.shader.
//
// The cone is symmetric by construction: one spread value (_SpreadX, animated via
// BeamProps.localEulerAngles.x) drives it. The emitter is a circle of radius
// _EmitterWidth/2, so _EmitterHeight and _SpreadZ go unused. Shear is unsupported
// (a sheared circular cone is oblique and breaks the analytic intersection).
//
// The mesh should be a unit cube (corners at +/-0.5); the vertex shader expands it
// to contain the cone's bounding box.

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
        // These are declared so the shared cginc and one MaterialPropertyBlock can
        // stay shape-agnostic with the rect shader, but the round frag never reads
        // them. Hidden so they don't show as inert sliders.
        //
        // Shear is genuinely unsupported here: a sheared circular cone is an oblique
        // quadric, which breaks the cheap analytic intersection this profile relies on.
        [HideInInspector] _EmitterHeight ("Emitter Height (unused)", Float) = 0.5
        [HideInInspector] _SpreadZ ("Spread Z (unused)", Float) = 0.0
        [HideInInspector] _ShearX ("Shear X (unsupported)", Float) = 0.0
        [HideInInspector] _ShearZ ("Shear Z (unsupported)", Float) = 0.0

        // Lateral scattering: how strongly haze softens the beam edge with
        // distance. The edge is crisp at the emitter and blurs progressively toward
        // the far end as the beam passes through more haze. 0 leaves the edge sharp;
        // higher values diffuse it sooner and wider. See _ScatterStrength in the frag.
        //
        // At the default 0.5 and typical venue haze (~0.03), the edge softens
        // visibly across a normal throw without ever dissolving into a centre-only
        // glow. Raise toward 1 for heavier-atmosphere looks.
        _ScatterStrength ("Lateral Scatter Strength", Range(0,1)) = 0.5

        // Anisotropy (the g parameter of the Henyey-Greenstein phase function):
        // how forward-biased the haze scatters light toward the eye. This is what
        // makes the beam brighter when you look toward the emitter than across it.
        //   0        isotropic (even in all directions; a flat, view-independent look)
        //   0 to 1   forward scatter, like real haze and fog (around 0.6 to 0.8)
        //   -1 to 0  back scatter (unusual; brightest seen from behind the light)
        _Anisotropy ("Anisotropy (HG g)", Range(-0.95, 0.95)) = 0.6

        _BeamCutoffThreshold ("Beam Cutoff Threshold", Float) = 0.0001
        _BeamLengthMax ("Beam Length Max (metres)", Float) = 50
        _CubeLocalScale ("Cube Local Scale (compensation)", Vector) = (1, 1, 1, 0)
        _Color ("Color", Color) = (1, 1, 1, 1)
        _BeamIntensity ("Intensity", Float) = 1.0
        _HazeDensity ("Haze Density (1/m)", Float) = 0.05

        // Focus: how fast the cone defocuses with distance, as a fraction of its
        // own spread angle. The beam is sharp at the emitter and spreads more toward
        // the far end.
        //   1  perfectly collimated (crisp; only haze softens the edge)
        //   0  defocuses to twice the cone's half-angle by the far end
        // Because the rate scales with the spread, narrow and wide beams defocus by
        // the same proportion. A perfectly collimated beam (spread 0) has no angle to
        // widen, so focus has no effect on it. See DiamondFocusSpill in the frag.
        _Focus ("Focus", Range(0,1)) = 1.0

        // Far-cap fade: fraction of the (auto-derived) beam length over which the
        // beam fades smoothly to zero approaching its far end, so it dissolves
        // instead of ending in a hard-clipped disc. 0 = hard cap; 0.15 = last 15%
        // fades. Range 0..1.
        _FarFade ("Far Cap Fade (fraction)", Range(0,1)) = 0.15

        // Debug visualisation for various components (plain Float; the
        // [DiamondBeamDebugMode] drawer gives a named dropdown). Keep in sync with
        // the frag dispatch chain and DiamondBeamDebugModeDrawer.cs:
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

            // The debug scaffolding (component-isolation modes and surface probe) is
            // gated behind this keyword so it's not compiled in the production version.
            //
            // Using shader_feature_local rather than multi_compile means only the
            // variants materials actually use get built, i.e. a material left at mode
            // 0 never references DIAMOND_DEBUG and is stripped from the build.
            // The debug-mode drawer toggles the keyword with the dropdown.
            #pragma shader_feature_local DIAMOND_DEBUG

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

            float _DebugMode;   // component-isolation views; see Properties

            v2f vert(appdata v) { return DiamondBeamVert(v); }

            // Find the interval [coneLo, coneHi] where the camera ray is inside the
            // solid circular cone. Within its +Y nappe the cone is the set of points
            // with x^2 + z^2 <= (r0 + s*y)^2, so along the ray it reduces to the
            // quadratic g(t) = a t^2 + b t + c <= 0. Returns false if the ray never
            // enters the solid.
            //
            // The quadric x^2 + z^2 = (r0 + s*y)^2 is a double cone: two nappes
            // meeting at the apex y = -r0/s. The mirror nappe (below the apex) also
            // satisfies the bare quadratic, and when the ray aims mostly along the
            // Y axis, a = rd.x^2 + rd.z^2 - (s*rd.y)^2 turns negative and the region
            // g(t) <= 0 becomes two outer tails that straddle the apex and swallow
            // the mirror nappe. Treating that as one interval carves a black cone-
            // shaped hole out of the beam when you look toward the emitter.
            //
            // The fix: solve for the surface crossings, then keep only the ones on
            // the real nappe (where the radius r0 + s*y stays non-negative), and clip
            // the surviving span to that half-line at the apex. A crossing on the
            // wrong nappe is discarded rather than seeding a bogus interval.
            bool ConeInterval(float3 ro, float3 rd, float r0, float s,
                out float coneLo, out float coneHi)
            {
                coneLo = -1e20; coneHi = 1e20;

                float k  = r0 + s * ro.y;   // R at t = 0
                float kd = s * rd.y;        // dR/dt
                float a = rd.x*rd.x + rd.z*rd.z - kd*kd;
                float b = 2.0 * (ro.x*rd.x + ro.z*rd.z - k*kd);
                float c = ro.x*ro.x + ro.z*ro.z - k*k;

                // Restricted to the real nappe the solid cone is convex, so its inside
                // is the single span between the (at most two) valid crossings. Keeping
                // only real-nappe crossings is what prevents the mirror-cone hole.
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
                            // nappe, that end is open, so extend it to the apex: the
                            // real cone runs from the valid root out to the apex, which
                            // the cap then trims to y in [0, beamLength].
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

            // The lateral diffusion rate DIAMOND_SCATTER_K is defined in
            // DiamondBeamCommon.cginc, shared with the vertex bounding box.

            // Brightness across the beam's cross-section is one soft-edged profile.
            // The natural coordinate is the normalised radius u = r / R(d), where R(d)
            // is the cone radius at depth d: u = 0 on the axis, u = 1 at the cone wall.
            //
            // The softness itself is measured in world-space metres, not in u. The
            // reason is geometric: if a blur of half-width w lived directly in u, the
            // lit edge would sit at u = 1 + w, which in metres is (1 + w) * R(d). That
            // trailing R(d) makes the edge inherit the cone's taper and multiply it, so
            // a blur that grows with depth bows the whole beam outward like a trumpet.
            // Instead the blur is an amount of metres, spill_m(d), added to the wall:
            //   lit edge in metres = R(d) + spill_m(d).
            // If spill_m grows linearly with depth this edge is a straight cone, not a
            // curved one. Each spill source (focus, haze) is linear in d for that reason.
            // At the point of use the metres are converted back to the profile's
            // coordinate with w = spill_m / R(d), so everything downstream stays in u.

            // Draws the soft edge: full brightness inside the wall, fading to zero across
            // a band of half-width w centred on the wall (u = 1). w = 0 is a razor-sharp
            // circle; larger w softens and widens the edge. w has no fixed ceiling, but
            // because spill_m and R(d) both grow with depth, w stays bounded in practice.
            float DiamondEdgeProfile(float u, float w)
            {
                return 1.0 - smoothstep(1.0 - w, 1.0 + w, u);
            }

            // Haze scatter contributes an edge blur that grows with optical depth
            // (haze * d) and _ScatterStrength: crisp at the emitter, softer far away.
            // Linear in d, in metres. It needs no upper clamp because a straight metric
            // edge can't bow or flat-line; the geometry (vertex box, cone clip) bounds it.
            float DiamondScatterSpill(float haze, float d, float strength)
            {
                return DIAMOND_SCATTER_K * max(haze, 0.0) * max(d, 0.0) * saturate(strength);
            }

            // Focus contributes a second edge blur that also grows with distance, but
            // without a haze term, so a lamp defocuses even in perfectly clear air. Its
            // rate scales with the cone's own spread rather than an absolute amount of
            // metres:
            //   spill_focus = (1 - _Focus) * spreadX * d.
            // Scaling by spreadX is what makes focus feel consistent across beam widths.
            // An absolute rate would blow a narrow beam out to a huge angle while barely
            // touching a wide one, because the eye reads defocus as a fraction of the
            // cone's own angle. With this rate, narrow and wide beams defocus by the same
            // proportion:
            //   _Focus = 1  gives rate 0 (crisp wall at R(d))
            //   _Focus = 0  gives rate spreadX, putting the edge at R(d) + spreadX*d,
            //               i.e. twice the cone's half-angle.
            // A perfectly collimated beam (spreadX = 0) has no angle to widen, so focus
            // does nothing to it; haze scatter still softens its edge.
            float DiamondFocusSpill(float focusF, float spreadX, float d)
            {
                float rate = (1.0 - saturate(focusF)) * max(spreadX, 0.0);
                return DIAMOND_SCATTER_K * rate * max(d, 0.0);
            }

            // Combine the focus and haze spills into a single edge width. The two spills
            // add in quadrature (their variances add, the way independent optical blurs
            // stack), giving one metric spill; dividing by the cone radius converts it to
            // the profile's u coordinate. Adding the spills in metres before the divide is
            // what keeps the lit edge at R(d) + spill_m, straight rather than bowed.
            float DiamondEdgeWidth(float focusF, float spreadX, float haze, float d, float strength, float radiusAtD)
            {
                float focusSpill   = DiamondFocusSpill(focusF, spreadX, d);
                float scatterSpill = DiamondScatterSpill(haze, d, strength);
                float spillMetres  = sqrt(focusSpill*focusSpill + scatterSpill*scatterSpill);
                return spillMetres / max(radiusAtD, 1e-6);   // metres to u coordinate
            }

            // Henyey-Greenstein phase function: how much light a point scatters toward
            // the eye, as a function of the scattering angle theta between the light's
            // direction of travel and the direction to the camera. This is what makes
            // the beam view-dependent, brighter when you look back toward the emitter
            // (forward scatter, small theta) and dimmer across or behind it.
            //
            //   p(cosTheta) = (1 / 4pi) * (1 - g^2) / (1 + g^2 - 2 g cosTheta)^(3/2)
            //
            //   g = 0   even scattering in every direction (a flat, view-independent look)
            //   g > 0   forward scatter, like real haze and fog (around 0.6 to 0.8)
            //   g < 0   back scatter
            //
            // cosTheta is dot(lightTravelDir, viewDir) with both in beam space. Light
            // fans out from the cone apex, so lightTravelDir is normalize(p - apex)
            // rather than a fixed axis, which matters for wide cones. The 1/4pi keeps
            // the function normalised (it integrates to 1 over the sphere) and is a
            // constant scale that _BeamIntensity absorbs.
            float DiamondHGPhase(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;   // positive for |g| < 1
                denom = max(denom, 1e-6);                      // guard against grazing edge
                return (1.0 / (4.0 * UNITY_PI)) * (1.0 - g2) * rsqrt(denom * denom * denom);
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

              #ifdef DIAMOND_DEBUG
                // Debug mode 8: vertex bounds. Faint red over every fragment of the
                // expanded bounding cube, returned before any discard so the whole box
                // shows, including the empty margin the beam doesn't fill. Use it to
                // check the box is neither clipping the halo nor wastefully large. The
                // beam itself isn't visible in this mode.
                if (_DebugMode > 7.5 && _DebugMode < 8.5)
                    return half4(0.05, 0.0, 0.0, 1.0);
              #endif

                float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale);
                float4 instColor      = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  beamIntensity  = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float  emitterWidth   = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
                float  spreadX        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadX);

                // beamLength is derived once in vert and passed down (constant per
                // instance), no per-pixel bisection here. See v2f.beamLength.
                float beamLength = i.beamLength;

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

                // 2) Intersect the cone into the slab. The lit edge spills past the
                // cone wall (focus and haze both widen it), so the clip has to enclose
                // that spill or it would slice the halo off. Because each spill is
                // linear in depth and zero at the emitter, the spilled edge is itself a
                // cone: same emitter radius, just a steeper spread. So we widen the clip
                // by adding the combined spill rate to the spread, rather than scaling
                // the whole cone (which would over-widen near the source). The edge
                // profile fades to zero before this widened wall, so nothing hard-clips.
                float focusRate    = DIAMOND_SCATTER_K * (1.0 - saturate(_Focus)) * max(spreadX, 0.0);
                float scatterRate  = DIAMOND_SCATTER_K * max(_HazeDensity, 0.0) * saturate(_ScatterStrength);
                float spillSpread  = sqrt(focusRate*focusRate + scatterRate*scatterRate);
                float coneLo, coneHi;
                if (!ConeInterval(rayOrigin, rayDirection, r0, spreadX + spillSpread, coneLo, coneHi))
                    discard;
                tEntry = max(tEntry, coneLo);
                tExit  = min(tExit,  coneHi);

                // 3) Keep only the part of the ray in front of the camera.
                tEntry = max(tEntry, 0.0);

                // 4) Clamp the far end to the nearest scene surface so the beam lands on
                // geometry (the floor, occluders) instead of passing through it.
                // DiamondBeamDepthClamp converts the scene's world-space distance into
                // beam-space t; that unit conversion is essential, since beam space is
                // scaled relative to world space.
                DiamondBeamDepthClamp(i, rayDirection, cubeLocalScale, tExit);

                if (tExit <= tEntry) discard;

                // Values the real integral and final colour need, computed for every
                // pixel. The per-component surface probe below is debug-only and gated
                // behind DIAMOND_DEBUG, so it costs nothing in the shipping variant.
                float segLen    = tExit - tEntry;                            // feeds stepLen
                float haze      = max(_HazeDensity, 0.0);                    // loop extinction
                float fadeStart = beamLength * (1.0 - saturate(_FarFade));   // loop far-fade
                // Cone apex: light fans out from here, so it seeds the HG light dir in
                // the loop. y = -r0/spread (apex of the quadric); origin if collimated.
                float3 beamApex = float3(0.0, (abs(spreadX) > 1e-6) ? (-r0 / spreadX) : 0.0, 0.0);

              #ifdef DIAMOND_DEBUG
                // Surface probe (debug only). Each per-component debug mode samples its
                // factor once at the exit hit, showing what a plane dropped into the beam
                // would display. The real brightness integrates these along the chord in
                // the loop above; the probe just makes each factor viewable on its own,
                // using the same helpers so the two can't diverge.
                float3 entryPt  = rayOrigin + rayDirection * tEntry;
                float  dist     = max(entryPt.y, 0.0);          // metres from emitter

                float3 exitPt  = rayOrigin + rayDirection * tExit;
                float  exitD   = max(exitPt.y, 0.0);
                float  exitR   = length(exitPt.xz);             // radial distance from axis
                float  exitRad = r0 + spreadX * exitD;          // cone radius R(d)
                float  uLat    = exitR / max(exitRad, 1e-6);    // 0 axis, 1 wall, more is spill

                // Lateral edge.
                float focusF      = saturate(_Focus);
                float edgeW       = DiamondEdgeWidth(focusF, spreadX, _HazeDensity, exitD, _ScatterStrength, exitRad);
                float edgeProfile = DiamondEdgeProfile(uLat, edgeW);

                // HG phase at the exit hit. The direction to the camera is exactly
                // -rayDirection (the sample is in front of the camera, ray normalised).
                float3 lightDirExit = normalize(exitPt - beamApex);
                float  cosThetaExit = dot(lightDirExit, -rayDirection);
                float  hgPhase      = DiamondHGPhase(cosThetaExit, _Anisotropy);

                // Geometric falloff at the entry point.
                float radius      = r0 + spreadX * dist;
                float crossArea   = UNITY_PI * radius * radius;
                float emitterArea = UNITY_PI * r0 * r0;
                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // Distance extinction and far-fade at the entry point.
                float extinction = exp(-haze * dist);
                float farFade    = (beamLength > fadeStart)
                    ? smoothstep(beamLength, fadeStart, dist)
                    : 1.0;
              #endif // DIAMOND_DEBUG

                // Integrate the brightness along the chord
                // -----------------------------------------
                // What the camera sees is the light scattered toward it from every
                // point along the chord [tEntry, tExit], which is the integral of the
                // product of all per-point factors:
                //
                //   brightness = integral of falloff(d) * ext(d) * fade(d) * lateral(u,d) dt
                //
                // The factors are of two kinds. Falloff, extinction and far-fade depend
                // only on depth d, so they're constant across a cross-section slice. The
                // lateral edge depends on both the radial coordinate u and the depth-
                // dependent blur width, and both change along the ray: as the chord cuts
                // through the cone, u sweeps across the section and the blur widens with
                // depth. That is why the lateral factor is evaluated per point inside the
                // loop rather than multiplied onto the finished sum; folding it in per
                // point is what accumulates the edge blur that reads as volumetric.
                //
                // There's no clean closed form, so we integrate numerically with a fixed
                // number of midpoint substeps. Using a fixed count (rather than a fixed
                // step size) splits any chord into the same number of pieces however long
                // it is, so long beams aren't under-sampled.
                //
                // The substep count is the loop's cost/quality dial. The depth-only
                // factors integrate cleanly at 4; the lateral edge varies fastest on
                // grazing rays and can stair-step at low counts, so raise this if that
                // shows. It also sets the unroll length, so keep it small.
                #define DIAMOND_DAXIS_STEPS 4

                float stepLen = segLen / DIAMOND_DAXIS_STEPS;
                float dAxisIntegral = 0.0;   // depth-only factors, for debug mode 5
                float beamIntegral  = 0.0;   // full brightness, mode 0

                // Henyey-Greenstein constants that don't vary along the chord. g is the
                // uniform _Anisotropy, so (1 - g^2)/4pi and g^2 are the same at every
                // substep; hoisting them out leaves only the per-point denominator in the
                // loop. hgTwoG is the 2g of the (1 + g^2 - 2g cosTheta) denominator.
                float hgG      = _Anisotropy;
                float hgG2     = hgG * hgG;
                float hgNum    = (1.0 - hgG2) * (1.0 / (4.0 * UNITY_PI));
                float hgOnePlus = 1.0 + hgG2;
                float hgTwoG   = 2.0 * hgG;

                // Beer-Lambert extinction is exp(-haze*d) with d affine in the substep
                // index, so along the chord it's a geometric progression: a base at the
                // first midpoint times a fixed per-step ratio. This replaces one exp per
                // substep with a single exp plus a multiply. dExt tracks exp(-haze*p.y);
                // inside the beam p.y is in [0, beamLength], matching the loop's max(p.y,0).
                float extBaseY  = rayOrigin.y + rayDirection.y * (tEntry + 0.5 * stepLen);
                float extStepY  = rayDirection.y * stepLen;
                float fExt      = exp(-haze * extBaseY);   // extinction at first midpoint
                float extRatio  = exp(-haze * extStepY);   // multiply per substep

                [unroll]
                for (int si = 0; si < DIAMOND_DAXIS_STEPS; si++)
                {
                    float t = tEntry + (si + 0.5) * stepLen;  // midpoint of substep
                    float3 p = rayOrigin + rayDirection * t;  // point on the chord
                    float d = max(p.y, 0.0);                  // depth there

                    // Geometric falloff: the cone widens with depth, spreading a fixed
                    // emitter flux over a larger disc, so brightness drops as (r0/R(d))^2.
                    float rad = r0 + spreadX * d;
                    float fFalloff = (r0*r0) / max(rad*rad, 1e-12);

                    // Beer-Lambert extinction: light is absorbed and scattered out of the
                    // beam as it travels through haze. fExt is carried as a running
                    // geometric progression (see the exp setup above) and advanced by
                    // extRatio at the end of each substep, so there's no per-step exp.

                    // Far-cap fade: dissolve the last stretch of the beam instead of
                    // ending it in a hard disc.
                    float fFade = (beamLength > fadeStart)
                        ? smoothstep(beamLength, fadeStart, d)
                        : 1.0;

                    float dOnly = fFalloff * fExt * fFade;
                    dAxisIntegral += dOnly;

                    // Lateral edge at this point. The blur width is DiamondEdgeWidth
                    // written out directly: both spills are linear in depth, so depth
                    // factors out of the quadrature and the combined spill in metres is
                    // just spillSpread * d (spillSpread was already computed for the cone
                    // clip). Dividing by the cone radius gives the width in u. Keep this in
                    // sync with DiamondEdgeWidth, which the debug surface probe uses.
                    float uHere = length(p.xz) / max(rad, 1e-6);
                    float wHere = spillSpread * d / max(rad, 1e-6);
                    float fLat  = DiamondEdgeProfile(uHere, wHere);

                    // Flux conservation as the edge spills wider. The geometric falloff
                    // above conserves flux over the disc of radius R(d), but the spill
                    // widens the lit disc past that, so the same light covers more area
                    // and must dim further. The edge profile's area integral has the
                    // closed form J(w) = 1/2 + w^2/10, giving a spilled area of
                    // pi*R(d)^2 * (1 + w^2/5), so the correcting factor is the ratio of
                    // the sharp-disc area to the spilled area, 1 / (1 + w^2/5). Exact for
                    // w <= 1; beyond that it dims very slightly too much, which is safe.
                    float fFluxNorm = 1.0 / (1.0 + wHere*wHere * 0.2);

                    // Henyey-Greenstein phase at this point. Light fans out from the apex,
                    // so its travel direction is normalize(p - apex) and changes along the
                    // chord, which is why the phase is evaluated per point. The direction
                    // to the camera is exactly -rayDirection (every sample is in front of
                    // the camera and the ray is normalised), so it needs no normalize.
                    //
                    // Inlined from DiamondHGPhase with its uniform terms (hgNum, hgOnePlus,
                    // hgTwoG) hoisted above the loop. The per-point cosTheta is
                    // dot(normalize(lightVec), -rd) = dot(lightVec, -rd) * rsqrt(|lightVec|^2),
                    // folding the normalize into one rsqrt shared with the length.
                    float3 lightVec  = p - beamApex;
                    float  lightDot  = dot(lightVec, -rayDirection);
                    float  cosTheta  = lightDot * rsqrt(max(dot(lightVec, lightVec), 1e-12));
                    float  hgDenom   = max(hgOnePlus - hgTwoG * cosTheta, 1e-6);
                    float  phaseHere = hgNum * rsqrt(hgDenom * hgDenom * hgDenom);

                    beamIntegral += dOnly * fLat * fFluxNorm * phaseHere;

                    fExt *= extRatio;   // advance extinction to the next substep midpoint
                }
                dAxisIntegral *= stepLen;   // midpoint rule: sum times substep width
                beamIntegral  *= stepLen;

              #ifdef DIAMOND_DEBUG
                // Debug dispatch, compiled out of the shipping variant. On the additive
                // blend against a dark scene, each grayscale value reads directly, and
                // every mode isolates one component. Mode 0 is tested first and on its
                // own, since it's the smallest value and a "< 1.5" bound would catch it
                // too. Mode 8 (vertex bounds) returns from the top of the function.
                if (_DebugMode < 0.5)                             // 0: the real beam
                    return half4(instColor.rgb * beamIntegral * beamIntensity, 1);
                if (_DebugMode < 1.5)   return half4(segLen.xxx * 0.1, 1);          // 1: segment length
                if (_DebugMode < 2.5)   return half4(geometricFalloff.xxx, 1);      // 2: geometric falloff
                if (_DebugMode < 3.5)   return half4(extinction.xxx, 1);            // 3: distance extinction
                if (_DebugMode < 4.5)   return half4(farFade.xxx, 1);               // 4: far-cap fade
                if (_DebugMode < 5.5)   return half4((dAxisIntegral).xxx, 1);       // 5: depth-only integral
                if (_DebugMode < 6.5)   return half4(uLat.xxx, 1);                  // 6: lateral coordinate u
                if (_DebugMode < 7.5)   return half4(edgeProfile.xxx, 1);           // 7: lateral edge
                if (_DebugMode < 9.5)   return half4(hgPhase.xxx, 1);               // 9: HG phase
              #endif // DIAMOND_DEBUG

                // Shipping path, and the fallback for any out-of-range debug value: the
                // real beam. dAxisIntegral feeds only debug mode 5, so it (and its
                // accumulation in the loop) is dead-stripped from the shipping variant.
                return half4(instColor.rgb * beamIntegral * beamIntensity, 1);
            }

            ENDCG
        }
    }
}
