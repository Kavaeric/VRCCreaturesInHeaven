// DiamondBeamRect.shader
//
// A volumetric light shaft for stage spotlight fixtures: a rectangular emitter
// and an (optionally elliptical) rectangular cone, like a barn-doored fixture.
//
// The shape-independent machinery lives in DiamondBeamCommon.cginc. This file adds
// only the rectangular specifics: a 4-plane (half-space) side-wall intersection, a
// rectangular cross-section area (w*h), and a two-axis edge softness.
//
// The two axes zoom and shear independently, so the cone need not be square or
// axis-symmetric. The circular counterpart is DiamondBeamRound.shader.
//
// The mesh should be a unit cube (corners at +/-0.5); the vertex shader expands it
// to contain the pyramid's bounding box.

Shader "Diamond/BeamRect"
{
    Properties
    {
        // Physical size of the emitter face, in world-space units (metres).
        // The +Y face of this rectangle is what the beam projects from.
        _EmitterWidth  ("Emitter width",  Float) = 0.5
        _EmitterHeight ("Emitter height", Float) = 0.5

        // Cone half-angles, expressed as tan(half-angle): the per-axis widening
        // per unit length. Independent per axis, so the cone need not be square.
        _ZoomX ("Zoom X (tan of half angle)", Float) = 0.0
        _ZoomZ ("Zoom Z (tan of half angle)", Float) = 0.0

        // Shear: leans the whole light shaft sideways at a constant rate per
        // metre of depth, independently per axis. 0 keeps the beam straight.
        _ShearX ("Shear X", Float) = 0.0
        _ShearZ ("Shear Z", Float) = 0.0

        _BeamCutoffThreshold ("Beam cutoff threshold", Float) = 0.0001
        _BeamLengthMax ("Beam length max (metres)", Float) = 50
        _CubeLocalScale ("Cube local scale (compensation)", Vector) = (1, 1, 1, 0)
        _Color ("Color", Color) = (1, 1, 1, 1)
        _BeamIntensity ("Intensity", Float) = 1.0
        _HazeDensity ("Haze density (1/m)", Float) = 0.03

        // Lateral scattering: how strongly haze softens the beam edge with
        // distance. The edge is crisp at the emitter and blurs progressively toward
        // the far end as the beam passes through more haze. 0 leaves the edge sharp;
        // higher values diffuse it sooner and wider. Same model as the round
        // shader's _ScatterStrength, applied identically to both axes.
        //
        // At the default 0.5 and typical venue haze (~0.03), the edge softens
        // visibly across a normal throw without ever dissolving into a centre-only
        // glow. Raise toward 1 for heavier-atmosphere looks.
        _ScatterStrength ("Lateral scatter strength", Range(0,1)) = 0.5

        // Focus: how fast the beam defocuses with distance, as a fraction of its
        // own zoom. The beam is sharp at the emitter and spreads more toward the
        // far end.
        //   1  perfectly collimated (crisp; only haze softens the edge)
        //   0  defocuses to twice the zoom by the far end
        // The rate scales per-axis with that axis's own zoom, so narrow and wide
        // sides defocus by the same proportion. An axis with zero zoom has
        // nothing to widen, so focus has no effect on it. Same model as the
        // round's _Focus. See DiamondFocusSpill in the frag.
        _Focus ("Focus", Range(0,1)) = 1.0

        // Anisotropy (the g parameter of the Henyey-Greenstein phase function):
        // how forward-biased the haze scatters light toward the eye. This is what
        // makes the beam brighter when you look toward the emitter than across it.
        //   0        isotropic (even in all directions; a flat, view-independent look)
        //   0 to 1   forward scatter, like real haze and fog (around 0.5 to 0.8)
        //   -1 to 0  back scatter (unusual; brightest seen from behind the light)
        _Anisotropy ("Anisotropy (HG g)", Range(-0.95, 0.95)) = 0.5

        // Far-cap fade: fraction of the (auto-derived) beam length over which the
        // beam fades smoothly to zero approaching its far end, so it dissolves
        // instead of ending in a hard-clipped face. 0 = hard cap; 0.15 = last 15%
        // fades. Range 0..1.
        _FarFade ("Far cap fade (fraction)", Range(0,1)) = 0.15

        // Debug visualisation for various components (plain Float; the
        // [DiamondBeamDebugMode] drawer gives a named dropdown). Keep in sync with
        // the frag dispatch chain and DiamondBeamDebugModeDrawer.cs:
        //   0 Normal           4 FarCapFade      8 VertexBounds
        //   1 RaymarchDepth    5 DAxisIntegral   9 HGPhase
        //   2 GeometricFalloff 6 LateralU
        //   3 HazeExtinction   7 LateralEdge
        [DiamondBeamDebugMode] _DebugMode ("Debug mode", Float) = 0
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

            // --- Rectangular shape definitions -----------------------------
            // Cross-section is a rectangle whose half-extents grow with zoom,
            // independently per axis. The DERIVE_BEAM_LENGTH macro evaluates these
            // where emitterWidth/emitterHeight/zoomX/zoomZ are locals.
            #define DIAMOND_EMITTER_AREA   (emitterWidth * emitterHeight)
            #define DIAMOND_CROSS_AREA(d)  ((emitterWidth + 2.0 * zoomX * (d)) * (emitterHeight + 2.0 * zoomZ * (d)))

            // Bounding box uses the two zooms independently (elliptical cone).
            #define DIAMOND_BOUNDS_ZOOM_X zoomX
            #define DIAMOND_BOUNDS_ZOOM_Z zoomZ

            #include "DiamondBeamCommon.cginc"

            float _DebugMode;   // component-isolation views; see Properties

            v2f vert(appdata v) { return DiamondBeamVert(v); }

            // Find the interval [tEntry, tExit] where the camera ray is inside the
            // solid truncated pyramid. Unlike the round cone, the side walls are
            // genuine half-spaces (planes), so the pyramid is convex by construction.
            // There is no mirror-nappe hazard to guard against; each plane just folds
            // into the running interval. See ClipLinear/ClipSlab below.

            // Clip [tEntry, tExit] to where a linear f(t) = f0 + fd*t stays <= 0.
            void ClipLinear(float f0, float fd, inout float tEntry, inout float tExit)
            {
                if (abs(fd) < 1e-7) { if (f0 > 0) { tEntry = 1e20; tExit = -1e20; } return; }
                float t = -f0 / fd;
                if (fd > 0) tExit  = min(tExit,  t);
                else        tEntry = max(tEntry, t);
            }

            // Clip to where one lateral coordinate stays within a leaning, widening
            // band: |coord(t) - shear*y(t)| <= halfEmit + zoom*y(t).
            //   coord(t) = c0 + cd*t,  y(t) = yo + yd*t.
            // Two linear half-spaces (the two walls) -> stays convex, one interval.
            void ClipSlab(float c0, float cd, float yo, float yd,
                float halfEmit, float zoom, float shear,
                inout float tEntry, inout float tExit)
            {
                float u0 = c0 - shear * yo;          // coord - centre, at t = 0
                float ud = cd - shear * yd;          // d/dt of that
                float h0 = halfEmit + zoom * yo;     // half-width at t = 0
                float hd = zoom * yd;                // d/dt of half-width
                ClipLinear( u0 - h0,  ud - hd, tEntry, tExit);   //  u - half <= 0
                ClipLinear(-u0 - h0, -ud - hd, tEntry, tExit);   // -u - half <= 0
            }

            // --- Lateral edge softness (rectangular) --------------------------
            // Brightness across the cross-section is one soft-edged profile per axis.
            // The softness is measured in world-space metres, not in the normalised
            // coordinate u, so a blur that grows with depth stays a straight-walled
            // pyramid edge instead of bowing:
            //   lit edge in metres = halfWidth(d) + spill_m(d).
            // The metres are converted to the profile's u only at the point of use,
            // w = spill_m / halfWidth(d). Each spill source (haze scatter, focus) is
            // linear in d so the metric edge stays straight.
            //
            // The two axes have different half-widths, so one metric spill converts to
            // a different width w per axis. The profile is therefore evaluated per axis
            // (each with its own u and w) and the two are combined with min(): a point
            // is only lit if it's inside on BOTH axes, so the dimmer axis governs. This
            // pairs with the max() used for the u coordinate: max on the coordinate,
            // min on the resulting brightness.

            // Draws the soft edge: full brightness inside the wall (u <= 1-w), fading
            // to zero across a band of half-width w centred on the wall (u = 1). w = 0
            // is a razor-sharp edge; larger w softens and widens it.
            float DiamondEdgeProfile(float u, float w)
            {
                return 1.0 - smoothstep(1.0 - w, 1.0 + w, u);
            }

            // Haze scatter contributes an edge blur that grows with optical depth
            // (haze * d) and _ScatterStrength: crisp at the emitter, softer far away.
            // Linear in d, in metres; DIAMOND_SCATTER_K sets the diffusion scale.
            // Scatter is isotropic in the air, so it's the same on both axes.
            float DiamondScatterSpill(float haze, float d, float strength)
            {
                return DIAMOND_SCATTER_K * max(haze, 0.0) * max(d, 0.0) * saturate(strength);
            }

            // Focus contributes a second edge blur that grows with distance without a
            // haze term (a lamp defocuses even in clear air). Its rate scales with the
            // axis's OWN zoom, so narrow and wide sides defocus by the same
            // proportion: spill_focus = (1 - _Focus) * zoom * d. An axis with zero
            // zoom has nothing to widen, so focus does nothing to it. Evaluated
            // per-axis since the rect's two zooms are independent.
            float DiamondFocusSpill(float focusF, float zoom, float d)
            {
                float rate = (1.0 - saturate(focusF)) * max(zoom, 0.0);
                return DIAMOND_SCATTER_K * rate * max(d, 0.0);
            }

            // Total metric spill on one axis: the focus and haze-scatter spills add in
            // quadrature (their variances add, the way independent optical blurs stack).
            // Scatter is the same on both axes; focus is per-axis, so the combined
            // spill differs per axis too.
            float DiamondAxisSpill(float focusF, float zoom, float haze, float d, float strength)
            {
                float f = DiamondFocusSpill(focusF, zoom, d);
                float s = DiamondScatterSpill(haze, d, strength);
                return sqrt(f*f + s*s);
            }

            // Combine the per-axis soft edges into one lateral factor, given each
            // axis's profile width w directly. min() takes the dimmer axis, the
            // counterpart to the max() used for the u coordinate.
            float DiamondRectEdgeW(float uLatX, float uLatZ, float wX, float wZ)
            {
                float eX = DiamondEdgeProfile(uLatX, wX);
                float eZ = DiamondEdgeProfile(uLatZ, wZ);
                return min(eX, eZ);
            }

            // Convenience overload taking per-axis metric spill: divides each by that
            // axis's own half-width (metres -> profile width) before combining. Adding
            // the spills in metres before the divide keeps the lit edge at
            // halfWidth + spill, straight rather than bowed.
            float DiamondRectEdge(float uLatX, float uLatZ, float halfWX, float halfHZ,
                float spillX, float spillZ)
            {
                float wX = spillX / max(halfWX, 1e-6);
                float wZ = spillZ / max(halfHZ, 1e-6);
                return DiamondRectEdgeW(uLatX, uLatZ, wX, wZ);
            }

            // Flux conservation as the edge spills wider. The geometric falloff
            // conserves flux over the sharp-walled rectangle only; the spill widens the
            // lit area past that, so the same light covers more area and must dim. The
            // lateral factor's area integral over the cross-section has a closed form:
            // I(wX, wZ) = 1 + wX*wZ/5 (exact for w <= 1; verified numerically to machine
            // precision in-range). Note the PRODUCT wX*wZ, not wX^2 + wZ^2: under the
            // min() combine, a blur on one axis alone adds no net lit area while the
            // other axis stays sharp. Only simultaneous spill on both axes enlarges the
            // lit rectangle, so the correcting factor is 1 / (1 + wX*wZ/5). For w > 1 it
            // errs mostly toward over-dimming, which is the safe direction.
            float DiamondRectFluxNorm(float wX, float wZ)
            {
                return 1.0 / (1.0 + wX * wZ * 0.2);
            }

            // --- Anisotropic (Henyey-Greenstein) phase ------------------------
            // Henyey-Greenstein phase function: how much light a point scatters toward
            // the eye, as a function of the scattering angle theta between the light's
            // direction of travel and the direction to the camera. This is what makes
            // the beam view-dependent, brighter looking back toward the emitter
            // (forward scatter, small theta) and dimmer across or behind it.
            //
            //   p(cosTheta) = (1 / 4pi) * (1 - g^2) / (1 + g^2 - 2 g cosTheta)^(3/2)
            //
            // The phase function itself is shape-independent; only the light-travel
            // direction fed into cosTheta depends on the emitter's shape (see
            // DiamondRectLightDir below).
            float DiamondHGPhase(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;   // positive for |g| < 1
                denom = max(denom, 1e-6);                      // guard against grazing edge
                return (1.0 / (4.0 * UNITY_PI)) * (1.0 - g2) * rsqrt(denom * denom * denom);
            }

            // Light-travel direction at a point in the rectangular pyramid.
            //
            // A round cone fans from a single apex, so its light direction is just
            // normalize(p - apex). A rectangular pyramid has independent per-axis
            // zooms, so its two wall-pairs converge at different depths behind the
            // emitter. There is no single apex point; instead each axis fans from its
            // own apex: the X walls meet at y = -(w/2)/zoomX, the Z walls at
            // y = -(h/2)/zoomZ.
            //
            // The light ray through p therefore has a per-axis slope. Measuring the
            // lateral offset from the sheared fan centre (the walls lean by shear), the
            // X slope is dx/dy = (px - shearX*py) / (py - apexX_y). Substituting the
            // apex depth turns the awkward (py - apex) into the half-width at py:
            //   dx/dy = (px - shearX*py) * zoomX / (halfEmitX + zoomX*py).
            // That form is well-behaved at zoomX = 0 (parallel walls -> slope 0, light
            // travels straight along y for that axis), needing no apex-at-infinity guard.
            // The shear itself also tilts the travel direction, so it's added back in.
            // The Y component is the shared forward travel; normalise at the end.
            //
            //   px, pz     lateral position at this point
            //   py         depth (metres from emitter)
            // Returns a unit vector in beam space.
            float3 DiamondRectLightDir(float px, float py, float pz,
                float emitterWidth, float emitterHeight,
                float zoomX, float zoomZ, float shearX, float shearZ)
            {
                float halfWXd = emitterWidth  * 0.5 + zoomX * py;
                float halfHZd = emitterHeight * 0.5 + zoomZ * py;
                // Fan slope from each axis's apex, plus the shear lean of the whole beam.
                float dxdy = (px - shearX * py) * zoomX / max(halfWXd, 1e-6) + shearX;
                float dzdy = (pz - shearZ * py) * zoomZ / max(halfHZd, 1e-6) + shearZ;
                return normalize(float3(dxdy, 1.0, dzdy));
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
                float  emitterHeight  = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterHeight);
                float  zoomX          = UNITY_ACCESS_INSTANCED_PROP(Props, _ZoomX);
                float  zoomZ          = UNITY_ACCESS_INSTANCED_PROP(Props, _ZoomZ);
                float  focusF         = saturate(UNITY_ACCESS_INSTANCED_PROP(Props, _Focus));

                // beamLength is derived once in vert and passed down (constant per
                // instance), no per-pixel bisection here. See v2f.beamLength.
                float beamLength = i.beamLength;

                // Camera ray in beam space (origin at emitter centre, +Y along
                // the beam, t in beam-space units).
                float3 cameraObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayOrigin    = cameraObject * cubeLocalScale;
                float3 rayDirection = normalize(i.vertBeamSpace - rayOrigin);

                float tEntry = -1e20;
                float tExit  =  1e20;

                // 1) Cap slab: 0 <= y <= beamLength.
                ClipLinear(-rayOrigin.y,              -rayDirection.y, tEntry, tExit);   // y >= 0
                ClipLinear( rayOrigin.y - beamLength,  rayDirection.y, tEntry, tExit);   // y <= beamLength

                // 2) Lateral walls. The lit edge spills past the geometric wall (focus
                // and haze both widen it), so the clip has to enclose that spill or it
                // would slice the soft edge off. Each spill is linear in depth and zero
                // at the emitter, so the spilled edge is itself a widened pyramid: same
                // emitter size, a steeper zoom. The clip widens by adding the per-axis
                // spill RATE (spill per metre) to the zoom. The edge profile fades to
                // zero before this widened wall, so nothing hard-clips.
                float scatterRate = DIAMOND_SCATTER_K * max(_HazeDensity, 0.0) * saturate(_ScatterStrength);
                float focusRateX  = DIAMOND_SCATTER_K * (1.0 - focusF) * max(zoomX, 0.0);
                float focusRateZ  = DIAMOND_SCATTER_K * (1.0 - focusF) * max(zoomZ, 0.0);
                float spillRateX  = sqrt(focusRateX*focusRateX + scatterRate*scatterRate);
                float spillRateZ  = sqrt(focusRateZ*focusRateZ + scatterRate*scatterRate);

                ClipSlab(rayOrigin.x, rayDirection.x, rayOrigin.y, rayDirection.y,
                    emitterWidth  * 0.5, zoomX + spillRateX, _ShearX, tEntry, tExit);
                ClipSlab(rayOrigin.z, rayDirection.z, rayOrigin.y, rayDirection.y,
                    emitterHeight * 0.5, zoomZ + spillRateZ, _ShearZ, tEntry, tExit);

                // 3) Only the part in front of the camera.
                tEntry = max(tEntry, 0.0);

                // 4) Clamp the far end to the nearest scene surface so the beam lands
                // on geometry (the floor, occluders) instead of passing through it.
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
                float emitterArea = emitterWidth * emitterHeight;

              #ifdef DIAMOND_DEBUG
                // Surface probe (debug only). Each per-component debug mode samples
                // its factor once at the segment entry point (the front face of the
                // slice), showing what a plane dropped into the beam would display.
                // Geometric falloff and extinction depend only on the fore/aft
                // distance d, so they're constant across any cross-section slice.
                // (Down-axis views read entry-d ~= 0.)
                float3 entryPt  = rayOrigin + rayDirection * tEntry;
                float  dist     = max(entryPt.y, 0.0);    // metres from emitter

                // Lateral coordinate at the exit hit (the surface the pixel looks at).
                // The round profile has a single radius u = r/R(d); a rectangle has no
                // single radius, so each axis gets its own normalised coordinate and
                // they're combined. The walls lean with shear, so the centre offset is
                // shear*d (matching ClipSlab), and the half-width grows as zoom*d.
                //   ux = |x - shearX*d| / halfW(d),  uz = |z - shearZ*d| / halfH(d)
                // Combined with max(): the u = 1 iso-contour is then the rectangle wall
                // itself (corners included), the honest match to the box geometry.
                float3 exitPt = rayOrigin + rayDirection * tExit;
                float  exitD  = max(exitPt.y, 0.0);
                float  halfWX = emitterWidth  * 0.5 + zoomX * exitD;   // X half-width at exitD
                float  halfHZ = emitterHeight * 0.5 + zoomZ * exitD;   // Z half-width at exitD
                float  uLatX  = abs(exitPt.x - _ShearX * exitD) / max(halfWX, 1e-6);
                float  uLatZ  = abs(exitPt.z - _ShearZ * exitD) / max(halfHZ, 1e-6);
                // max() keeps the u = 1 iso-contour at the true rectangle wall, corners
                // included. length() is a future option: it rounds the corners off for
                // an elliptical-cone look instead. Worth exposing as a combine toggle
                // later; not wired up yet.
                float  uLat   = max(uLatX, uLatZ);   // 0 axis, 1 wall, more is spill

                // Lateral edge at the exit hit. Each axis's spill combines its own
                // focus (scaled by that axis's zoom) with the shared haze scatter.
                float spillLatX = DiamondAxisSpill(focusF, zoomX, _HazeDensity, exitD, _ScatterStrength);
                float spillLatZ = DiamondAxisSpill(focusF, zoomZ, _HazeDensity, exitD, _ScatterStrength);
                float edgeProfile = DiamondRectEdge(uLatX, uLatZ, halfWX, halfHZ, spillLatX, spillLatZ);

                // HG phase at the exit hit. Light fans from the pyramid's per-axis
                // apexes (see DiamondRectLightDir); the direction to the camera is
                // exactly -rayDirection (sample in front of the camera, ray normalised).
                float3 lightDirExit = DiamondRectLightDir(exitPt.x, exitD, exitPt.z,
                    emitterWidth, emitterHeight, zoomX, zoomZ, _ShearX, _ShearZ);
                float  hgPhase = DiamondHGPhase(dot(lightDirExit, -rayDirection), _Anisotropy);

                // Geometric falloff: the pyramid widens with distance, so a fixed
                // emitter flux is spread over a larger cross-section -> dimmer.
                //   geometricFalloff = emitterArea / crossArea(d)
                //   d = 0      -> 1.0;  zoom = 0 -> 1.0 everywhere (collimated)
                float crossWidth  = emitterWidth  + 2.0 * zoomX * dist;
                float crossHeight = emitterHeight + 2.0 * zoomZ * dist;
                float crossArea   = crossWidth * crossHeight;
                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // Distance extinction: light scatters/absorbs out of the beam
                // through haze (Beer-Lambert): exp(-haze * d).
                //   d = 0 -> 1.0;  haze = 0 -> 1.0 everywhere;  haze up -> faster.
                float extinction = exp(-haze * dist);

                // Far-cap fade: smoothly fade the beam to zero over the last
                // _FarFade fraction of its (auto-derived) length, so it dissolves
                // instead of ending in a hard-clipped face at beamLength.
                //   d <= fadeStart -> 1.0;  d -> beamLength -> 0.0
                //   _FarFade = 0   -> hard cap (no fade band)
                float farFade   = (beamLength > fadeStart)
                    ? smoothstep(beamLength, fadeStart, dist)   // 1 at start, 0 at cap
                    : 1.0;
              #endif // DIAMOND_DEBUG

                // Integrate the brightness along the chord
                // -----------------------------------------
                // What the camera sees is the light scattered toward it from every
                // point along the chord [tEntry, tExit], the integral of the product
                // of all per-point factors. The depth-only factors (falloff,
                // extinction, far-fade) are constant across a cross-section slice; the
                // lateral edge depends on both the position within the slice and the
                // depth-dependent blur width, both of which change along the ray, so it
                // MUST be evaluated per point inside the loop rather than multiplied onto
                // the finished sum. Folding it in per point is what accumulates the edge
                // blur that reads as volumetric.
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
                float hgG       = _Anisotropy;
                float hgG2      = hgG * hgG;
                float hgNum     = (1.0 - hgG2) * (1.0 / (4.0 * UNITY_PI));
                float hgOnePlus = 1.0 + hgG2;
                float hgTwoG    = 2.0 * hgG;

                // Beer-Lambert extinction is exp(-haze*d) with d affine in the substep
                // index, so along the chord it's a geometric progression: a base at the
                // first midpoint times a fixed per-step ratio. This replaces one exp per
                // substep with a single exp plus a multiply. fExt tracks exp(-haze*p.y);
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

                    // Half-widths at this depth (the walls lean by shear, widen by
                    // zoom), shared by the falloff, lateral edge, and light-direction
                    // terms below.
                    float halfWXd = emitterWidth  * 0.5 + zoomX * d;
                    float halfHZd = emitterHeight * 0.5 + zoomZ * d;

                    // Geometric falloff: the pyramid widens with depth, spreading a
                    // fixed emitter flux over a larger rectangle, so brightness drops
                    // as the ratio of emitter area to cross-section area.
                    float fFalloff = emitterArea / max(4.0 * halfWXd * halfHZd, 1e-12);

                    // Far-cap fade: dissolve the last stretch of the beam instead of
                    // ending it in a hard face.
                    float fFade = (beamLength > fadeStart)
                        ? smoothstep(beamLength, fadeStart, d)
                        : 1.0;

                    // fExt carries Beer-Lambert extinction as a running geometric
                    // progression (see setup above); advanced at the end of the substep.
                    float dOnly = fFalloff * fExt * fFade;
                    dAxisIntegral += dOnly;

                    // Lateral edge at this point: the per-axis normalised coord and the
                    // per-axis profile width w = metric spill / half-width (spill =
                    // spillRate * d, reusing the rates computed for the widened clip).
                    // Both spills are linear in depth, so w = spillRate*d/half.
                    float uxHere  = abs(p.x - _ShearX * d) / max(halfWXd, 1e-6);
                    float uzHere  = abs(p.z - _ShearZ * d) / max(halfHZd, 1e-6);
                    float wxHere  = spillRateX * d / max(halfWXd, 1e-6);
                    float wzHere  = spillRateZ * d / max(halfHZd, 1e-6);
                    float fLat    = DiamondRectEdgeW(uxHere, uzHere, wxHere, wzHere);

                    // Flux conservation as the edge spills wider (see
                    // DiamondRectFluxNorm): dim by the ratio of the sharp-rect area to
                    // the spilled area so the widened lit region doesn't gain energy.
                    float fFluxNorm = DiamondRectFluxNorm(wxHere, wzHere);

                    // Henyey-Greenstein phase at this point. Light fans from the
                    // pyramid's per-axis apexes, so its travel direction changes along
                    // the chord (which is why the phase is per point). The direction to
                    // the camera is exactly -rayDirection (every sample is in front of
                    // the camera and the ray is normalised).
                    //
                    // Inlined from DiamondRectLightDir/DiamondHGPhase with the uniform HG
                    // terms (hgNum, hgOnePlus, hgTwoG) hoisted above the loop, and reusing
                    // halfWXd/halfHZd from the lateral edge above (same depth, same
                    // half-widths). The per-point cosTheta is dot(normalize(lightVec), -rd),
                    // folding the normalize into one rsqrt shared with the length, rather
                    // than a full normalize() followed by a separate rsqrt in the phase.
                    float lightDx  = (p.x - _ShearX * d) * zoomX / max(halfWXd, 1e-6) + _ShearX;
                    float lightDz  = (p.z - _ShearZ * d) * zoomZ / max(halfHZd, 1e-6) + _ShearZ;
                    float3 lightVec  = float3(lightDx, 1.0, lightDz);
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
                if (_DebugMode < 1.5)   return half4(segLen.xxx * 0.1, 1);        // 1: segment length
                if (_DebugMode < 2.5)   return half4(geometricFalloff.xxx, 1);    // 2: geometric falloff
                if (_DebugMode < 3.5)   return half4(extinction.xxx, 1);          // 3: distance extinction
                if (_DebugMode < 4.5)   return half4(farFade.xxx, 1);             // 4: far-cap fade
                if (_DebugMode < 5.5)   return half4((dAxisIntegral).xxx, 1);     // 5: depth-only integral
                if (_DebugMode < 6.5)   return half4(uLat.xxx, 1);                // 6: lateral coordinate u
                if (_DebugMode < 7.5)   return half4(edgeProfile.xxx, 1);         // 7: lateral edge
                if (_DebugMode < 9.5)   return half4(hgPhase.xxx, 1);             // 9: HG phase
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
