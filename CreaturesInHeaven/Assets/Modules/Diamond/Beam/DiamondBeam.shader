// Diamond - Beam sub-module
// Volumetric light shaft for stage spotlight fixtures.
//
// The beam points along the object's local +Y axis (fixtures hang from a
// ceiling and shine downward when rotated 180 degrees around X).
//
// The mesh used by this shader should be a UNIT CUBE (corners at +/-0.5 on
// every axis). The vertex shader expands that cube at render time so it
// exactly contains the frustum implied by the shader properties. There's
// no need to scale the GameObject's transform to "fit" the beam. Emitter
// dimensions, spread, shear, and beam length are all in world-space units.

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
        // falloff, the fixture's flux, and _BeamIntensity. A small threshold
        // means longer-looking beams (more shading work); a larger one cuts
        // them off sooner.
        _BeamCutoffThreshold ("Beam Cutoff Threshold", Float) = 0.0001

        // Hard ceiling on the auto-derived beam length, in metres. Prevents
        // pathologically long beams from a very high _BeamIntensity or a
        // very low _BeamCutoffThreshold.
        _BeamLengthMax ("Beam Length Max (metres)", Float) = 50

        // Counter-scale: set this to the GameObject's localScale to make the
        // shader render at true world size regardless of the cube's transform
        // scale. Useful when you want the bounding cube to be physically tiny
        // in the scene hierarchy (avoiding gizmo clutter, picking, etc.) but
        // still want the beam to render at metres-accurate dimensions.
        //
        // Example: cube scaled to (0.001, 0.001, 0.001) -> set this to
        // (0.001, 0.001, 0.001) and the beam renders at full world size.
        // Leave at (1, 1, 1) for normal use.
        _CubeLocalScale ("Cube Local Scale (compensation)", Vector) = (1, 1, 1, 0)

        _Color ("Color", Color) = (1, 1, 1, 1)

        // Intensity multiplier for the beam. Flat modifier with no real
        // world or physically-based analogue.
        _BeamIntensity ("Intensity", Float) = 1.0

        // Haze density: the per-metre extinction coefficient of the air the
        // beam passes through. Physically this controls two things at once,
        // because of conservation of energy:
        //
        //   1. How bright the beam appears: you only see a beam at all
        //      because particles in the air scatter the light toward your eye.
        //      More haze = more scattering = brighter beam.
        //   2. How fast the beam dies off: the same scattering takes light
        //      out of the beam's original direction, so it can't reach as far.
        //
        // Real-world ballpark values (per metre):
        //   ~0.005 - barely visible: clear room air
        //   ~0.02  - light atmospheric haze
        //   ~0.05  - typical concert / venue haze
        //   ~0.15  - heavy fog machine
        //   ~0.5+  - thick smoke
        _HazeDensity ("Haze Density (1/m)", Float) = 0.05

        // Edge softness: how much the beam's sides blur with distance and haze.
        // 0 = razor-sharp edges (the original look). Higher values blur the
        // beam's lateral edges more, simulating multiple scattering and the
        // finite size of real-world emitters. The blur grows with distance
        // from the emitter and with haze density, so close-up the beam stays
        // crisp and far away it softens naturally.
        //
        // Reasonable values: 0.0 - 2.0. Default 1.0 = subtle.
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

            #include "UnityCG.cginc"

            // Geometry properties shared across all instances of a fixture type
            // (set on the material asset; not animated).
            float  _ShearX;
            float  _ShearZ;
            float  _BeamCutoffThreshold;
            float  _BeamLengthMax;
            float  _HazeDensity;
            float  _BeamIntensityMultiplier;
            float  _EdgeSoftness;

            // Per-instance properties: pushed by DiamondFixtureDriver via a
            // MaterialPropertyBlock so each fixture can vary independently.
            // _SpreadX/_SpreadZ are animated (via BeamProps.localEulerAngles.x),
            // so they live here too. Stored as tan(half-angle) so the shader
            // can use them directly.
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float,  _EmitterWidth)
                UNITY_DEFINE_INSTANCED_PROP(float,  _EmitterHeight)
                UNITY_DEFINE_INSTANCED_PROP(float,  _SpreadX)
                UNITY_DEFINE_INSTANCED_PROP(float,  _SpreadZ)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _CubeLocalScale)
                UNITY_DEFINE_INSTANCED_PROP(float,  _BeamIntensity)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex          : SV_POSITION;
                // Vertex position in "beam space": coords are in world units,
                // emitter is at y=0, far cap is at y=beamLength. The frag's
                // ray math is done entirely in this space.
                float3 vertBeamSpace   : TEXCOORD0;
                float4 screenPos       : TEXCOORD1;
                float3 vertWorldSpace  : TEXCOORD2;
                // Oblique-frustum correction for mirror-camera depth reads.
                // Stored as dot(clipPos, correctionVec); frag divides by clipW.
                float  frustumCorrection : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            // Mirror cameras use an OBLIQUE near plane to clip geometry behind
            // the mirror surface. The standard Unity helper LinearEyeDepth()
            // assumes the projection matrix's third row has its default shape,
            // which obliques break. The fix is to derive a per-pixel correction
            // factor from the projection matrix and use it when reading depth.
            //
            // Adapted from LUTBeam (Torvid / ValueFactory / Micca), which in turn
            // adapted it from:
            //   https://github.com/lukis101/VRCUnityStuffs/blob/master/Shaders/DJL/Overlays/WorldPosOblique.shader
            float4 CalculateFrustumCorrection()
            {
                float x1 = -UNITY_MATRIX_P._31 / (UNITY_MATRIX_P._11 * UNITY_MATRIX_P._34);
                float x2 = -UNITY_MATRIX_P._32 / (UNITY_MATRIX_P._22 * UNITY_MATRIX_P._34);
                return float4(x1, x2, 0,
                    UNITY_MATRIX_P._33 / UNITY_MATRIX_P._34 + x1 * UNITY_MATRIX_P._13 + x2 * UNITY_MATRIX_P._23);
            }

            // Replacement for LinearEyeDepth that handles oblique near planes.
            // frustumCorrection is dot(clipPos, CalculateFrustumCorrection())
            // divided by clipPos.w, computed in vert and reconstructed in frag.
            float CorrectedLinearEyeDepth(float z, float frustumCorrection)
            {
                return 1.0 / (z / UNITY_MATRIX_P._34 + frustumCorrection);
            }

            // Evaluates the per-point brightness density at a distance from the
            // emitter, using the same formula the frag shader uses. Lets the
            // beam-length derivation actually match what gets rendered.
            float BeamDensityAtDistance(float distance,
                float emitterWidth, float emitterHeight,
                float spreadX, float spreadZ, float beamIntensity, float haze)
            {
                float crossWidth  = emitterWidth  + 2.0 * spreadX * distance;
                float crossHeight = emitterHeight + 2.0 * spreadZ * distance;
                float crossArea   = crossWidth * crossHeight;
                float emitterArea = emitterWidth * emitterHeight;
                float geometric   = emitterArea / max(crossArea, 1e-6);
                float extinction  = exp(-haze * distance);
                return geometric * haze * extinction * beamIntensity;
            }

            // Finds the distance at which beam density falls below the cutoff
            // threshold. We bisect against the actual per-point brightness
            // formula (instead of solving the components separately) so the
            // result matches what the frag shader actually renders.
            //
            // Both vert and frag call this so they agree on where the beam ends.
            float DeriveBeamLength(float emitterWidth, float emitterHeight,
                float spreadX, float spreadZ, float beamIntensity)
            {
                float threshold = max(_BeamCutoffThreshold, 1e-5);
                float intensity = max(beamIntensity, 0);
                float haze      = max(_HazeDensity, 1e-5);

                // If the beam isn't bright enough to be visible at all, bail.
                if (BeamDensityAtDistance(0, emitterWidth, emitterHeight, spreadX, spreadZ, intensity, haze) <= threshold)
                    return 0;

                // Bisect on [lo, hi]. lo is always above threshold, hi is always
                // below. Start hi at _BeamLengthMax.
                float lo = 0;
                float hi = _BeamLengthMax;

                // If the cap is still above threshold, beam reaches the cap.
                if (BeamDensityAtDistance(hi, emitterWidth, emitterHeight, spreadX, spreadZ, intensity, haze) > threshold)
                    return hi;

                // 16 iterations -> ~1/65000 of _BeamLengthMax resolution. Plenty.
                [unroll]
                for (int it = 0; it < 8; it++)
                {
                    float mid = 0.5 * (lo + hi);
                    float density = BeamDensityAtDistance(mid, emitterWidth, emitterHeight, spreadX, spreadZ, intensity, haze);
                    if (density > threshold) lo = mid;
                    else                     hi = mid;
                }
                return hi;
            }

            // Maps a unit-cube vertex to the bounding box of the frustum, in
            // beam space (world units, +Y along beam, origin at emitter centre).
            //
            // Input vertex is expected in [-0.5, +0.5] on every axis.
            //   x in [-0.5, +0.5] -> X side of the bounding box
            //   y in [-0.5, +0.5] -> 0 to beamLength along the beam
            //   z in [-0.5, +0.5] -> Z side of the bounding box
            //
            // The bounding half-extents at the far cap are:
            //   halfWidthFar  = emitterWidth/2  + (spreadX + |_ShearX|) * beamLength
            //   halfHeightFar = emitterHeight/2 + (spreadZ + |_ShearZ|) * beamLength
            // The near (y=0) cap is emitterWidth/2 x emitterHeight/2.
            // We linearly interpolate the half-extents along y so the cube
            // hugs the frustum at every slice.
            float3 ExpandUnitCubeToFrustumBounds(float3 unitVertex,
                float emitterWidth, float emitterHeight,
                float spreadX, float spreadZ, float beamLength)
            {
                float yT = unitVertex.y + 0.5;        // 0..1 along beam length
                float beamY = yT * beamLength;

                // Inflate the cube laterally by the soft-edge halo at the
                // far end of the beam (where it's widest). Matches the
                // softness formula in the frag shader so blurred pixels
                // don't get clipped at the bounding cube walls.
                //
                // Near cap stays un-inflated since softness = 0 at d = 0.
                float diffusionRate = _EdgeSoftness * (0.02 + _HazeDensity);
                float maxSoftness   = diffusionRate * beamLength;

                float halfWidthNear  = emitterWidth  * 0.5;
                float halfHeightNear = emitterHeight * 0.5;
                float halfWidthFar   = halfWidthNear  + (spreadX + abs(_ShearX)) * beamLength + maxSoftness;
                float halfHeightFar  = halfHeightNear + (spreadZ + abs(_ShearZ)) * beamLength + maxSoftness;

                // Correct interpolation: half-extent grows linearly with yT.
                float halfWidthAtY  = lerp(halfWidthNear,  halfWidthFar,  yT);
                float halfHeightAtY = lerp(halfHeightNear, halfHeightFar, yT);

                // unitVertex.x/z are in [-0.5, +0.5]; scale by full extent (2 * half).
                float3 beamSpace;
                beamSpace.x = unitVertex.x * 2.0 * halfWidthAtY;
                beamSpace.y = beamY;
                beamSpace.z = unitVertex.z * 2.0 * halfHeightAtY;
                return beamSpace;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                // Early-out: any of these conditions makes the beam contribute
                // nothing visible. Collapse every vertex to clip-space origin
                // so the triangle gets culled before fragments are rasterised:
                //   * Zero haze -> nothing scatters light into the camera.
                //   * Zero beam intensity -> per-fixture brightness multiplier
                //     is off (e.g. the animator has dimmed this fixture to 0).
                //   * Black colour -> nothing to add via additive blending.
                float earlyOutIntensity = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float4 earlyOutColor    = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  earlyOutColorMax = max(earlyOutColor.r, max(earlyOutColor.g, earlyOutColor.b));
                if (_HazeDensity <= 1e-5 || earlyOutIntensity <= 1e-5 || earlyOutColorMax <= 1e-5)
                {
                    o.vertex = float4(0, 0, 0, 0);
                    return o;
                }

                // Expand the unit cube into the frustum's beam-space bounding box.
                // This is in world metres -- the values we want to actually render at.
                // Emitter dimensions are instanced, so pull them per-instance and
                // pass them into the helper.
                float emitterWidth  = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
                float emitterHeight = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterHeight);
                float spreadX       = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadX);
                float spreadZ       = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadZ);
                float beamIntensity = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float beamLength    = DeriveBeamLength(emitterWidth, emitterHeight,
                                                       spreadX, spreadZ, beamIntensity);

                float3 beamSpace    = ExpandUnitCubeToFrustumBounds(
                    v.vertex.xyz, emitterWidth, emitterHeight,
                    spreadX, spreadZ, beamLength);

                // The cube's transform applies its localScale on top via ObjectToWorld.
                // To make the rendered size independent of that scale, pre-divide by
                // the user-supplied counter-scale so ObjectToWorld's scale cancels out.
                //
                // Note this only compensates SCALE -- rotation and translation of the
                // cube still apply as normal, which is what you want (the beam should
                // still follow the fixture's position and orientation).
                float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale).xyz;
                float3 objectSpace = beamSpace / cubeLocalScale;
                float4 expandedObject = float4(objectSpace, 1);

                o.vertex            = UnityObjectToClipPos(expandedObject);
                o.vertBeamSpace     = beamSpace;
                o.vertWorldSpace    = mul(unity_ObjectToWorld, expandedObject).xyz;
                o.screenPos         = ComputeScreenPos(o.vertex);
                o.frustumCorrection = dot(o.vertex, CalculateFrustumCorrection());
                return o;
            }

            // Returns the distance along the ray where it crosses a surface defined
            // by planeNormal and planeOffset.
            // Negative result means the intersection is behind the ray origin.
            float RayPlaneDistance(float3 rayOrigin, float3 rayDirection,
                float3 planeNormal, float planeOffset)
            {
                float distanceFromPlane = dot(planeNormal, rayOrigin) + planeOffset;
                float approachRate      = dot(planeNormal, rayDirection);
                return -distanceFromPlane / approachRate;
            }

            // Folds one plane (defined by its OUTWARD-pointing normal) into a running
            // [tEntry, tExit] interval. The plane defines a half-space: the inside of
            // the volume is where planeNormal . p + planeOffset <= 0.
            //
            // If the ray is moving toward the outside (dot(n, rd) > 0), this plane
            // marks where the ray EXITS the half-space -- tighten tExit.
            // If the ray is moving toward the inside (dot(n, rd) < 0), this plane
            // marks where the ray ENTERS the half-space -- tighten tEntry.
            //
            // Works for any convex shape, even when planes aren't parallel.
            void FoldPlaneIntoInterval(float3 rayOrigin, float3 rayDirection,
                float3 planeNormal, float planeOffset,
                inout float tEntry, inout float tExit)
            {
                float t = RayPlaneDistance(rayOrigin, rayDirection, planeNormal, planeOffset);

                if (dot(planeNormal, rayDirection) > 0)
                    tExit  = min(tExit,  t);   // exiting this half-space
                else
                    tEntry = max(tEntry, t);   // entering this half-space
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                // Pull per-instance properties up front so the rest of the function
                // can ignore the instancing accessor noise.
                float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale).xyz;
                float4 instColor      = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float  beamIntensity  = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
                float  emitterWidth   = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
                float  emitterHeight  = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterHeight);
                float  spreadX        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadX);
                float  spreadZ        = UNITY_ACCESS_INSTANCED_PROP(Props, _SpreadZ);

                // Auto-derive the effective beam length. Same call as vert(),
                // so both stages agree on where the beam ends.
                float  beamLength = DeriveBeamLength(emitterWidth, emitterHeight,
                                                     spreadX, spreadZ, beamIntensity);

                // Build the camera ray in BEAM SPACE. The ray's t parameter is now
                // directly in world-space metres along the ray.
                //
                // unity_WorldToObject lands the camera in the cube's object space.
                // Since vert() pre-divided by _CubeLocalScale, we multiply by it
                // here to undo that and land in beam space (where vertBeamSpace lives).
                float3 cameraObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayOrigin    = cameraObject * cubeLocalScale;
                float3 rayDirection = normalize(i.vertBeamSpace - rayOrigin);

                // Start with an unbounded interval, then shrink it against each of
                // the 6 planes that bound the frustum (all in beam-space metres).
                float tEntry = -1e20;
                float tExit  =  1e20;

                // Add the diffusion rate to the spread used for the intersection
                // walls. This widens the cone linearly with distance from the
                // emitter, exactly tracking the smoothstep softness in the
                // falloff math below. At d=0 the walls coincide with the
                // geometric cone (crisp emitter face); past that they grow.
                float diffusionRateWall = _EdgeSoftness * (0.02 + _HazeDensity);
                float spreadXSoft = spreadX + diffusionRateWall;
                float spreadZSoft = spreadZ + diffusionRateWall;

                // Four slanted side walls. Outward-pointing normals.
                // The wall tilts outward as y grows, expressed as -spreadX/Z in the y component.
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3( 1, -spreadXSoft - _ShearX,  0), -emitterWidth  / 2, tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(-1, -spreadXSoft + _ShearX,  0), -emitterWidth  / 2, tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3( 0, -spreadZSoft - _ShearZ,  1), -emitterHeight / 2, tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3( 0, -spreadZSoft + _ShearZ, -1), -emitterHeight / 2, tEntry, tExit);

                // Near cap (y = 0, outward normal -Y) and far cap (y = beamLength, outward normal +Y).
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(0, -1, 0), 0,           tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(0,  1, 0), -beamLength, tEntry, tExit);

                // Discard pixels that miss the volume entirely.
                if (tExit <= tEntry) discard;

                // Depth clamp: when the beam hits a part of the scene that is closer than
                // where the beam ends, terminate the beam at the closer position, simulating
                // the effect of a beam terminating at a surface.
                //
                // rawDepth == 0 means no geometry was written here (sky / background).
                // In these cases, leave tExit alone and let the beam reach its far cap.
                float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float  rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, screenUV);

                // Outputs true if the beam was clamped at this pixel.
                bool isDepthClamped = false;
                float tExitBefore = tExit;

                if (rawDepth > 0)
                {
                    // CorrectedLinearEyeDepth handles mirror cameras (oblique
                    // near planes) where stock LinearEyeDepth would be wrong.
                    // The frustumCorrection v2f field was filled in vert as
                    // dot(clipPos, correctionVec); divide by clipW here so the
                    // perspective interpolation cancels.
                    float  sceneEyeDepth   = CorrectedLinearEyeDepth(rawDepth, i.frustumCorrection / i.screenPos.w);
                    float3 cameraForwardWS = -UNITY_MATRIX_V[2].xyz;
                    float3 rayDirWS        = normalize(i.vertWorldSpace - _WorldSpaceCameraPos);

                    // Distance along our ray (in world metres) where the scene is hit.
                    // Our beam-space ray's t is also in metres, so no extra conversion
                    // is needed -- we can clamp tExit directly.
                    float sceneT = sceneEyeDepth / max(dot(cameraForwardWS, rayDirWS), 1e-5);

                    tExit = min(tExit, sceneT);
                    isDepthClamped = (tExit < tExitBefore);

                    if (tExit <= tEntry) discard;
                }

                // Cross-section density falloff.
                //
                // Think of a fixture's total light output as fixed. The cone widens
                // with distance, so at any point the light is spread across a larger
                // cross-section, so each unit area gets less. The brightness at a
                // point is therefore proportional to:
                //
                //     emitterArea / coneCrossSectionAreaAtDistance
                //
                // This single expression captures both effects in one go:
                //  * Tight beams stay dense for longer (cone widens slowly).
                //  * Wide beams dilute quickly.
                //  * At d=0 the ratio is exactly 1 (no dilution at the emitter face).
                //  * At large d it falls as ~1/d^2 (true inverse-square).
                //
                // No flux cap needed: the emitter's finite size naturally bounds
                // the near-field brightness, and the cone's growth provides the
                // far-field falloff.
                float tMid          = (tEntry + tExit) * 0.5;
                float3 beamMidpoint = rayOrigin + rayDirection * tMid;
                float distance      = beamMidpoint.y;                       // metres from emitter

                // Softness grows linearly with distance from the emitter.
                // No baseline: right at the emitter face the beam is crisp,
                // and the halo expands as you go down the beam. The growth
                // rate is set by _EdgeSoftness directly (1.0 -> 1 metre of
                // halo per metre of throw). Haze multiplies because thicker
                // air diffuses faster, but the relationship is gentle so a
                // beam in vacuum (haze ~ 0) still has a tiny intrinsic spread
                // from the emitter not being a point.
                float diffusionRate = _EdgeSoftness * (0.02 + _HazeDensity);
                float softness      = diffusionRate * distance;

                // Cone cross-section dimensions at this distance. spreadX/Z are
                // tan(half-angle), so the full width grows by 2*spread*d per metre.
                // Add softness to the effective cone width so the halo's lateral
                // extent dilutes the brightness too -- conservation of energy:
                // smearing the light over a wider apparent cross-section means
                // each unit area gets less.
                float crossWidth    = emitterWidth  + 2.0 * spreadX * distance + 2.0 * softness;
                float crossHeight   = emitterHeight + 2.0 * spreadZ * distance + 2.0 * softness;
                float crossArea     = crossWidth * crossHeight;
                float emitterArea   = emitterWidth * emitterHeight;

                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // Soft edges. A point near the centre of the cone gets full
                // brightness; one near the cone wall fades smoothly to zero.
                // distFromWall measures lateral distance to the nearest of the
                // four side walls of the GEOMETRIC cone at this point (i.e.
                // ignoring the softness inflation -- we want the smoothstep
                // centred on the geometric wall, not the inflated one).
                float geomHalfWidth  = 0.5 * (emitterWidth  + 2.0 * spreadX * distance);
                float geomHalfHeight = 0.5 * (emitterHeight + 2.0 * spreadZ * distance);
                float lateralX       = abs(beamMidpoint.x);
                float lateralZ       = abs(beamMidpoint.z);
                float distFromX      = geomHalfWidth  - lateralX;
                float distFromZ      = geomHalfHeight - lateralZ;
                float distFromWall   = min(distFromX, distFromZ);

                // Centred smoothstep: 0 brightness one softness *outside* the
                // cone wall, 1 brightness one softness *inside*. Crosses 0.5 at
                // the geometric cone wall itself. This produces a halo that
                // extends past the hard frustum boundary, which is why we
                // inflate the bounds in vert AND inflate the FoldPlane walls.
                float edgeFactor   = smoothstep(-max(softness, 1e-4), max(softness, 1e-4), distFromWall);

                // Haze-driven scattering and extinction. The same _HazeDensity
                // value plays two roles, because they're physically coupled:
                //  * It multiplies the brightness (more haze = more scattering
                //    toward the eye = brighter beam).
                //  * It controls the exponential extinction along the beam
                //    (more haze = light removed faster = shorter visible reach).
                // Cranking haze up makes the beam pop more in the near field but
                // die off sooner in the far field. Replicates physically accurate
                // conservation of energy.
                float haze = max(_HazeDensity, 0);
                float extinction = exp(-haze * distance);

                float lightFalloff = geometricFalloff * edgeFactor * haze * extinction;

                // Volumetric integration: brightness contributed by the ray
                // equals (light density) x (ray traversal length). lightFalloff
                // is the light per unit volume at this point; (tExit - tEntry)
                // is how many metres the ray spends inside the beam volume.
                float beamSegment = lightFalloff * (tExit - tEntry);

                // float3 debugOverlay = float3(0.05, 0, 0);

                // return fixed4(instColor.rgb * beamSegment * beamIntensity + debugOverlay, 1);
                return fixed4(instColor.rgb * beamSegment * beamIntensity, 1);
            }

            ENDCG
        }
    }
}
