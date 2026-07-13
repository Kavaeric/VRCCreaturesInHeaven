// DiamondBeamCommon.cginc
//
// Profile-independent code shared by every beam shape.
//
// Each concrete beam shader includes this file and supplies only the parts that
// differ between shapes:
//
//   * the ray/volume side-wall intersection (planes for rect, a quadric for round)
//   * the cross-section area used for the inverse-square geometric falloff
//   * the lateral edge-softness distance-to-wall
//
// Everything else lives here so the two shape shaders can't drift.
//
// The beam points along the object's local +Y axis (fixtures hang from a
// ceiling and shine downward when rotated 180 degrees around X). The mesh is a
// unit cube (corners at +/-0.5); the vertex shader expands it to contain the
// frustum implied by the shader properties.

#ifndef DIAMOND_BEAM_COMMON_INCLUDED
#define DIAMOND_BEAM_COMMON_INCLUDED

#include "UnityCG.cginc"

// --- Lateral diffusion rate --------------------------------------------------
// The constant that sets how fast the lateral edge spills with depth. Both the
// focus and haze-scatter spills grow as DIAMOND_SCATTER_K * rate * d (in metres),
// so this sets the overall diffusion scale while _Focus and haze*strength supply
// the per-source rate. At 1, one unit of rate produces one metre of spill per metre
// of depth. It lives here rather than in the frag because both the vertex bounding
// box (ExpandUnitCubeToFrustumBounds) and the frag's spill math read it and must
// agree. It's a fixed property of the diffusion model, not an art control; _Focus,
// _ScatterStrength and _HazeDensity are the knobs that shape the look.
//
// Because the spill is measured in metres and added to the cone wall (lit edge at
// R(d) + spill_m(d)), it needs no upper clamp: a straight metric edge can neither
// bow nor flat-line, and the geometry (vertex box and cone clip) bounds it.
#define DIAMOND_SCATTER_K 1.0

// --- Material-level (non-instanced) properties -------------------------------
// Shared across all instances of a fixture type (set on the material asset).
float  _ShearX;
float  _ShearZ;
float  _BeamCutoffThreshold;
float  _BeamLengthMax;
float  _HazeDensity;
float  _EdgeSoftness;
float  _FarFade;
float  _ScatterStrength;
float  _Anisotropy;

// --- Per-instance properties -------------------------------------------------
// Pushed by DiamondManager via a MaterialPropertyBlock so each fixture
// can vary independently. _ZoomX/_ZoomZ are animated (via
// BeamProps.localEulerAngles.x), stored as tan(half-angle) so the shader uses
// them directly. The round shader only reads _ZoomX (symmetric cone) but the
// manager writes both, and _ZoomZ is simply ignored there.
//
// Focus: how fast the cone defocuses with distance, as a fraction of its own zoom.
// Sharp at the emitter, spreading more toward the far end.
//   1 = perfectly collimated: crisp edge, only haze softens it across the throw.
//   0 = defocuses to twice the cone's half-angle by the far end.
// The focus spill is measured in metres, grows linearly with depth, and scales with
// the zoom: spill_focus = K*(1-_Focus)*zoomX*d. It combines in quadrature with
// the haze scatter spill and is then divided by the cone radius to give the edge
// profile's width (see DiamondFocusSpill and DiamondEdgeWidth in the frag). Scaling
// by the zoom is what makes defocus feel consistent across beam widths: narrow and
// wide beams defocus by the same proportion, instead of a narrow beam blowing out to
// a huge angle. A collimated beam (zoomX = 0) has no angle to widen, so focus does
// nothing to it, though haze scatter still softens its edge. Because the spill is
// zero at the emitter and added in metres to the wall, the source stays at radius r0.
// Per-instance (not material-level) so each fixture can animate focus independently
// without breaking GPU instancing -- a plain global float is shared by every
// instance in a batch, so a per-fixture MaterialPropertyBlock write would either
// silently desync instances sharing a material or break the batch.
UNITY_INSTANCING_BUFFER_START(Props)
    UNITY_DEFINE_INSTANCED_PROP(float,  _EmitterWidth)
    UNITY_DEFINE_INSTANCED_PROP(float,  _EmitterHeight)
    UNITY_DEFINE_INSTANCED_PROP(float,  _ZoomX)
    UNITY_DEFINE_INSTANCED_PROP(float,  _ZoomZ)
    UNITY_DEFINE_INSTANCED_PROP(float,  _Focus)
    UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
    UNITY_DEFINE_INSTANCED_PROP(float3, _CubeLocalScale)
    UNITY_DEFINE_INSTANCED_PROP(float,  _BeamIntensity)
    // Row of this fixture's colour texel in the baked lightshow texture, and the
    // manager/show slot in the global frame array. Both are static per fixture, seeded
    // once at Start rather than per frame. Only read when DIAMOND_LIGHTSHOW_TEX is
    // enabled, and harmless (unread) otherwise.
    UNITY_DEFINE_INSTANCED_PROP(float,  _FixtureRow)
    UNITY_DEFINE_INSTANCED_PROP(float,  _ShowIndex)
UNITY_INSTANCING_BUFFER_END(Props)

// --- Baked lightshow lookup (DIAMOND_LIGHTSHOW_TEX) ---------------------------
// When enabled, the animated per-fixture values (_Color, _ZoomX/Z, _Focus,
// _BeamIntensity) are read from a baked texture instead of the instancing buffer,
// so DiamondManager never touches them per frame on the CPU. The texture globals,
// addressing math, and frame lerp live in DiamondLightshowSample.cginc, shared
// verbatim with the lamp-glow shader so the two can't drift. Here we add only
// the beam-specific master-scale global.
#include "../Lightshow/DiamondLightshowSample.cginc"

#ifdef DIAMOND_LIGHTSHOW_TEX
// Manager-wide master multiplier on beam intensity. The proxy path folds this into
// _BeamIntensity per fixture as `beamIntensity * BeamIntensityScale`; here it's one global the
// shader multiplies onto the texture-recovered intensity. Distinct from
// _UdonDiamondLightshowBeamScale, which is the per-bake HDR de-scale, not an art knob. The
// manager always seeds this in texture mode (>= 0, with 1 a no-op), so the shader can use it
// directly: a raw 0 here means "master off, all beams dark", a legitimate authored value rather
// than an unset sentinel. Beam-only (the lamp glow ignores beam intensity), so it stays here
// rather than in the shared sampler include.
float       _UdonDiamondBeamIntensityScale;
#endif // DIAMOND_LIGHTSHOW_TEX

struct appdata
{
    float4 vertex : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f
{
    float4 vertex          : SV_POSITION;
    // Vertex position in "beam space": coords are in world units, emitter is at
    // y=0, far cap is at y=beamLength. The frag's ray math is done entirely in
    // this space.
    float3 vertBeamSpace   : TEXCOORD0;
    float4 screenPos       : TEXCOORD1;
    float3 vertWorldSpace  : TEXCOORD2;
    // Oblique-frustum correction for mirror-camera depth reads.
    // Stored as dot(clipPos, correctionVec); frag divides by clipW.
    float  frustumCorrection : TEXCOORD3;
    // Auto-derived beam length, in metres. It's constant per instance, so vert
    // computes it once (the bisection in DIAMOND_DERIVE_BEAM_LENGTH) and passes it
    // down, sparing the frag that per-pixel bisection. Interpolation is exact because
    // every vertex of an instance produces the same value.
    float  beamLength : TEXCOORD4;
#ifdef DIAMOND_LIGHTSHOW_TEX
    // Texture-sourced animated values, sampled once in vert (they're per-instance
    // constants, like beamLength) and passed to frag. Interpolation is exact: every
    // vertex of an instance samples the same frame/row, so these are flat across it.
    //   xyzw = _Color.rgb, _BeamIntensity   (packed to save an interpolator)
    //   xyz  = _ZoomX, _Focus, (_ZoomZ = _ZoomX for the beam bake)
    float4 lsColorIntensity : TEXCOORD5;
    float3 lsZoomFocus      : TEXCOORD6;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// --- Animated-value resolvers ------------------------------------------------
// Each animated per-fixture value comes from either the baked texture (via the v2f, sampled in
// vert) or the instancing buffer. Frag and vert read them through these macros so the two paths
// differ in exactly one place. In the texture path _ZoomZ mirrors _ZoomX, since the bake stores
// a single zoom and drives both axes from it for a symmetric cone.
#ifdef DIAMOND_LIGHTSHOW_TEX
    #define DIAMOND_COLOR(i)      float4((i).lsColorIntensity.rgb, 1)
    #define DIAMOND_INTENSITY(i)  ((i).lsColorIntensity.w)
    #define DIAMOND_ZOOMX(i)      ((i).lsZoomFocus.x)
    #define DIAMOND_ZOOMZ(i)      ((i).lsZoomFocus.x)
    #define DIAMOND_FOCUS(i)      ((i).lsZoomFocus.y)
#else
    #define DIAMOND_COLOR(i)      UNITY_ACCESS_INSTANCED_PROP(Props, _Color)
    #define DIAMOND_INTENSITY(i)  UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity)
    #define DIAMOND_ZOOMX(i)      UNITY_ACCESS_INSTANCED_PROP(Props, _ZoomX)
    #define DIAMOND_ZOOMZ(i)      UNITY_ACCESS_INSTANCED_PROP(Props, _ZoomZ)
    #define DIAMOND_FOCUS(i)      UNITY_ACCESS_INSTANCED_PROP(Props, _Focus)
#endif

UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

// --- Mirror-camera oblique depth correction ----------------------------------
// Mirror cameras use an oblique near plane to clip geometry behind the mirror
// surface. The standard Unity helper LinearEyeDepth() assumes the projection
// matrix's third row has its default shape, which obliques break. The fix is to
// derive a per-pixel correction factor from the projection matrix and use it
// when reading depth.
//
// Adapted from LUTBeam (Torvid / ValueFactory / Micca), which in turn adapted
// it from:
//   https://github.com/lukis101/VRCUnityStuffs/blob/master/Shaders/DJL/Overlays/WorldPosOblique.shader
float4 CalculateFrustumCorrection()
{
    float x1 = -UNITY_MATRIX_P._31 / (UNITY_MATRIX_P._11 * UNITY_MATRIX_P._34);
    float x2 = -UNITY_MATRIX_P._32 / (UNITY_MATRIX_P._22 * UNITY_MATRIX_P._34);
    return float4(x1, x2, 0,
        UNITY_MATRIX_P._33 / UNITY_MATRIX_P._34 + x1 * UNITY_MATRIX_P._13 + x2 * UNITY_MATRIX_P._23);
}

// Replacement for LinearEyeDepth that handles oblique near planes.
// frustumCorrection is dot(clipPos, CalculateFrustumCorrection()) divided by
// clipPos.w, computed in vert and reconstructed in frag.
float CorrectedLinearEyeDepth(float z, float frustumCorrection)
{
    return 1.0 / (z / UNITY_MATRIX_P._34 + frustumCorrection);
}

// --- Beam length derivation --------------------------------------------------
// Evaluates the per-point brightness density at a distance from the emitter,
// using the same formula the frag shader uses. Lets the beam-length derivation
// actually match what gets rendered.
//
// crossArea is the cone's cross-section area at the given distance. It's the only
// shape-dependent term, so callers pass it in. (Rect: w*h. Round:
// pi*r^2.) emitterArea is the cross-section area at the emitter face.
float BeamDensityAtDistance(float distance, float crossArea, float emitterArea,
    float beamIntensity, float haze)
{
    float geometric  = emitterArea / max(crossArea, 1e-6);
    float extinction = exp(-haze * distance);
    return geometric * haze * extinction * beamIntensity;
}

// Finds the distance at which beam density falls below the cutoff threshold.
// We bisect against the actual per-point brightness formula (instead of solving
// the components separately) so the result matches what the frag shader renders.
//
// crossAreaAtDistance / emitterArea encapsulate the shape; the caller supplies
// them via the shape-specific cross-section macros declared below, so this stays
// shape-agnostic.
//
// Both vert and frag call this so they agree on where the beam ends.
//
// Shape shaders define DIAMOND_CROSS_AREA(distance) and DIAMOND_EMITTER_AREA as
// expressions in terms of the locals already in scope (emitter dims, zoom).
#define DIAMOND_DERIVE_BEAM_LENGTH(outLength)                                   \
{                                                                              \
    float _threshold = max(_BeamCutoffThreshold, 1e-5);                        \
    float _intensity = max(beamIntensity, 0);                                  \
    float _haze      = max(_HazeDensity, 1e-5);                                \
    float _emArea    = DIAMOND_EMITTER_AREA;                                   \
    if (BeamDensityAtDistance(0, DIAMOND_CROSS_AREA(0), _emArea, _intensity, _haze) <= _threshold) \
        outLength = 0;                                                         \
    else if (BeamDensityAtDistance(_BeamLengthMax, DIAMOND_CROSS_AREA(_BeamLengthMax), _emArea, _intensity, _haze) > _threshold) \
        outLength = _BeamLengthMax;                                            \
    else                                                                       \
    {                                                                          \
        float _lo = 0;                                                         \
        float _hi = _BeamLengthMax;                                            \
        [unroll]                                                              \
        for (int _it = 0; _it < 8; _it++)                                      \
        {                                                                      \
            float _mid = 0.5 * (_lo + _hi);                                    \
            float _d   = BeamDensityAtDistance(_mid, DIAMOND_CROSS_AREA(_mid), _emArea, _intensity, _haze); \
            if (_d > _threshold) _lo = _mid; else _hi = _mid;                  \
        }                                                                      \
        outLength = _hi;                                                       \
    }                                                                          \
}

// --- Unit-cube -> tapered bounding frustum ------------------------------------
// Maps a unit-cube vertex to a bounding box in beam space (world units, +Y along
// beam, origin at emitter centre) that guarantees to contain the beam in every
// configuration. A circular cone fits inside the same box as a square one of
// equal zoom, so both shapes share this (round passes its single zoom twice).
//
// Tapered: since both the geometric zoom and the lateral spill grow linearly
// with depth, the true worst-case cross-section is itself linear in Y, so the
// near cap can be as tight as the emitter instead of matching the far cap's
// width. Lerping half-width by unitVertex.y (0 at the near cap, 1 at the far
// cap) reproduces that taper exactly with the same 8-vertex cube topology --
// no extra vertices, just a per-vertex Y-dependent width. This is the main
// overdraw saving for narrow-emitter / wide-zoom fixtures, where the old
// constant-width box wasted a large fraction of its cross-section as empty
// margin near the source.
//
//   Height (Y): _BeamLengthMax, the absolute ceiling, ignoring whether extinction
//               or far-fade ends the beam sooner. Always tall enough.
//   Lateral (X/Z): interpolated from the emitter half-size at y=0 to the worst-case
//               far-cap half-width at y=beamLength:
//               emitter half-size
//             + (geometric zoom + lateral spill spread) * y
//             + shear lean * y
//
// The lateral spill (defocus + haze scatter) is an additive metric amount that
// grows linearly with depth (see DiamondEdgeWidth in the round frag), so it reads
// as extra SPREAD rather than a multiplicative reach. Worst case per metre: full
// defocus (focus = 0 -> rate DIAMOND_SCATTER_K) and haze scatter (haze*strength),
// combined in quadrature to match the frag's cone-clip. Conservative on focus (uses
// the max rate regardless of the current _Focus) so the box never clips the halo.
//
// Input vertex is expected in [-0.5, +0.5] on every axis.
//   x in [-0.5, +0.5] -> X side of the box
//   y in [-0.5, +0.5] -> 0 to _BeamLengthMax along the beam
//   z in [-0.5, +0.5] -> Z side of the box
float3 ExpandUnitCubeToFrustumBounds(float3 unitVertex,
    float emitterWidth, float emitterHeight,
    float zoomX, float zoomZ, float beamLength)
{
    float maxLen = max(_BeamLengthMax, 0.0);
    float yFrac  = unitVertex.y + 0.5;   // 0 at near cap, 1 at far cap

#ifdef DIAMOND_CONSERVATIVE_BOX
    // Debug/build-in-progress escape hatch: a giant constant box that cannot
    // possibly clip the beam, so a too-tight bound can't be mistaken for a frag
    // bug while a shader's passes are still being brought up. A shape shader
    // #defines DIAMOND_CONSERVATIVE_BOX before including this file to opt in; the
    // finished shapes leave it undefined and get the tight taper below. Sized to
    // the worst-case far half-width on every axis and held constant top to bottom
    // (no taper), extended by a safety margin. Wasteful on overdraw by design --
    // it exists only to take the vertex box out of the equation.
    float scatterRateC  = DIAMOND_SCATTER_K * max(_HazeDensity, 0.0) * saturate(_ScatterStrength);
    float spillSpreadXC = sqrt(zoomX*zoomX * (DIAMOND_SCATTER_K*DIAMOND_SCATTER_K) + scatterRateC*scatterRateC);
    float spillSpreadZC = sqrt(zoomZ*zoomZ * (DIAMOND_SCATTER_K*DIAMOND_SCATTER_K) + scatterRateC*scatterRateC);
    float halfWidthC  = emitterWidth  * 0.5 + (zoomX + spillSpreadXC + abs(_ShearX)) * maxLen + 1.0;
    float halfHeightC = emitterHeight * 0.5 + (zoomZ + spillSpreadZC + abs(_ShearZ)) * maxLen + 1.0;

    float3 boxC;
    boxC.x = unitVertex.x * 2.0 * halfWidthC;
    boxC.y = yFrac * maxLen;
    boxC.z = unitVertex.z * 2.0 * halfHeightC;
    return boxC;
#else

    // Worst-case extra spread per metre from lateral spill (focus plus haze scatter),
    // matching the frag's spillSpread. Focus scales with the cone's own zoom, and at
    // its worst (focus = 0) the rate equals the zoom, so the focus term is per-axis
    // rather than a single absolute rate. Scatter is the same on both axes.
    float scatterRate  = DIAMOND_SCATTER_K * max(_HazeDensity, 0.0) * saturate(_ScatterStrength);
    float spillSpreadX = sqrt(zoomX*zoomX * (DIAMOND_SCATTER_K*DIAMOND_SCATTER_K) + scatterRate*scatterRate);
    float spillSpreadZ = sqrt(zoomZ*zoomZ * (DIAMOND_SCATTER_K*DIAMOND_SCATTER_K) + scatterRate*scatterRate);

    // Half-extents at this vertex's depth (yFrac * maxLen metres from the emitter),
    // tapering from the emitter half-size up to the far-cap worst case.
    float halfWidth  = emitterWidth  * 0.5 + (zoomX + spillSpreadX) * maxLen * yFrac + abs(_ShearX) * maxLen * yFrac;
    float halfHeight = emitterHeight * 0.5 + (zoomZ + spillSpreadZ) * maxLen * yFrac + abs(_ShearZ) * maxLen * yFrac;

    float3 beamSpace;
    beamSpace.x = unitVertex.x * 2.0 * halfWidth;
    beamSpace.y = yFrac * maxLen;   // 0 .. _BeamLengthMax
    beamSpace.z = unitVertex.z * 2.0 * halfHeight;
    return beamSpace;
#endif // DIAMOND_CONSERVATIVE_BOX
}

// --- Ray / interval helpers (cap planes; round side wall is a quadric) --------
// Returns the distance along the ray where it crosses a plane.
// Negative result means the intersection is behind the ray origin.
float RayPlaneDistance(float3 rayOrigin, float3 rayDirection,
    float3 planeNormal, float planeOffset)
{
    float distanceFromPlane = dot(planeNormal, rayOrigin) + planeOffset;
    float approachRate      = dot(planeNormal, rayDirection);
    return -distanceFromPlane / approachRate;
}

// Folds one plane (defined by its outward-pointing normal) into a running
// [tEntry, tExit] interval. The plane defines a half-space: the inside of the
// volume is where planeNormal . p + planeOffset <= 0.
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

// --- Shared vert -------------------------------------------------------------
// The whole vertex stage is shape-independent: it derives beam length (via the
// shape macros) and expands the cube. Shape shaders just #define the macros and
// call this. Declared as a function the shape's "vert" entry point forwards to.
v2f DiamondBeamVert(appdata v)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);

    // Resolve the animated per-fixture values. In the texture path these come from the baked
    // lightshow (sampled once here, since they're per-instance constants) and are stashed in the
    // v2f for the frag; otherwise they're instancing-buffer reads.
#ifdef DIAMOND_LIGHTSHOW_TEX
    float  fixtureRow   = UNITY_ACCESS_INSTANCED_PROP(Props, _FixtureRow);
    float  showIndex    = UNITY_ACCESS_INSTANCED_PROP(Props, _ShowIndex);
    float4 lsColor; float lsZoom; float lsFocus; float lsIntensity;
    DiamondSampleLightshow(fixtureRow, showIndex, lsColor, lsZoom, lsFocus, lsIntensity);

    // Master beam-intensity scale (manager-wide), folded in here so it flows through the
    // early-out (a 0 master collapses the beam) and the beam-length derivation. This matches the
    // proxy path, which bakes `beamIntensity * BeamIntensityScale` into _BeamIntensity.
    lsIntensity *= _UdonDiamondBeamIntensityScale;

    o.lsColorIntensity = float4(lsColor.rgb, lsIntensity);
    o.lsZoomFocus      = float3(lsZoom, lsFocus, lsZoom);   // zoomZ mirrors zoomX

    float  beamIntensity = lsIntensity;
    float4 valColor      = lsColor;
    float  zoomX         = lsZoom;
    float  zoomZ         = lsZoom;
#else
    float  beamIntensity = UNITY_ACCESS_INSTANCED_PROP(Props, _BeamIntensity);
    float4 valColor      = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
    float  zoomX         = UNITY_ACCESS_INSTANCED_PROP(Props, _ZoomX);
    float  zoomZ         = UNITY_ACCESS_INSTANCED_PROP(Props, _ZoomZ);
#endif

    // Early-out: any of these conditions makes the beam contribute nothing
    // visible. Collapse every vertex to the clip-space origin so the triangle
    // gets culled before fragments are rasterised:
    //   * Zero haze -> nothing scatters light into the camera.
    //   * Zero beam intensity -> per-fixture brightness multiplier is off.
    //   * Black colour -> nothing to add via additive blending.
    float earlyOutColorMax = max(valColor.r, max(valColor.g, valColor.b));
    if (_HazeDensity <= 1e-5 || beamIntensity <= 1e-5 || earlyOutColorMax <= 1e-5)
    {
        o.vertex = float4(0, 0, 0, 0);
        o.vertBeamSpace = 0; o.screenPos = 0; o.vertWorldSpace = 0; o.frustumCorrection = 0;
        o.beamLength = 0;
    #ifdef DIAMOND_LIGHTSHOW_TEX
        o.lsColorIntensity = 0; o.lsZoomFocus = 0;
    #endif
        return o;
    }

    float emitterWidth  = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterWidth);
    float emitterHeight = UNITY_ACCESS_INSTANCED_PROP(Props, _EmitterHeight);

    float beamLength;
    DIAMOND_DERIVE_BEAM_LENGTH(beamLength);

    // The round shader collapses to a symmetric cone: its DIAMOND_BOUNDS_ZOOM_X/Z
    // macros both resolve to zoomX (it ignores _ZoomZ), so the bounding box is a
    // square that contains the circle. (Rect uses the two independently.)
    float3 beamSpace = ExpandUnitCubeToFrustumBounds(
        v.vertex.xyz, emitterWidth, emitterHeight,
        DIAMOND_BOUNDS_ZOOM_X, DIAMOND_BOUNDS_ZOOM_Z, beamLength);

    // The cube's transform applies its localScale on top via ObjectToWorld. To
    // make the rendered size independent of that scale, pre-divide by the
    // user-supplied counter-scale so ObjectToWorld's scale cancels out.
    float3 cubeLocalScale = UNITY_ACCESS_INSTANCED_PROP(Props, _CubeLocalScale);
    float3 objectSpace    = beamSpace / cubeLocalScale;
    float4 expandedObject = float4(objectSpace, 1);

    o.vertex            = UnityObjectToClipPos(expandedObject);
    o.vertBeamSpace     = beamSpace;
    o.vertWorldSpace    = mul(unity_ObjectToWorld, expandedObject).xyz;
    o.screenPos         = ComputeScreenPos(o.vertex);
    o.frustumCorrection = dot(o.vertex, CalculateFrustumCorrection());
    o.beamLength        = beamLength;   // pass down; frag skips the per-pixel bisection
    return o;
}

// --- Shared frag tail: depth clamp + integration -----------------------------
// Given a [tEntry, tExit] interval already shrunk against the shape's side walls
// and caps, clamps tExit against scene depth and returns the final additive
// colour. lightFalloffAtMid is the shape's per-unit-volume light density at the
// ray midpoint (geometric falloff * edge factor * haze * extinction); the
// caller computes it because it depends on the shape's cross-section + edge.
//
// Returns false (via discard caller) if the ray misses; here we just return the
// colour and let the caller discard when tExit <= tEntry.
fixed4 DiamondBeamIntegrate(v2f i, float3 rayOrigin, float3 rayDirection,
    float tEntry, inout float tExit, float lightFalloffAtMid,
    float3 instColorRGB, float beamIntensity)
{
    // beamSegment = (light per unit volume at midpoint) x (metres inside volume)
    float beamSegment = lightFalloffAtMid * (tExit - tEntry);
    return fixed4(instColorRGB * beamSegment * beamIntensity, 1);
}

// Depth clamp shared by both shapes. Shrinks tExit so the beam terminates at the
// nearest scene surface in front of its far cap, which is what makes it land on the
// floor and walls instead of passing through them. Mutates tExit.
//
// tExit is in beam-space t, but the scene hit reconstructed from the depth texture is
// a world-space distance (metres). Beam space is object space scaled component-wise by
// cubeLocalScale (e.g. 0.1), so beam-space t and world metres differ. We convert by
// measuring how many world metres one unit of beam-space t spans along this ray,
// then sceneT_beam = sceneDist_world / metresPerT.
void DiamondBeamDepthClamp(v2f i, float3 rayDirection, float3 cubeLocalScale,
    inout float tExit)
{
    float2 screenUV = i.screenPos.xy / i.screenPos.w;
    float  rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, screenUV);

    // rawDepth == 0 means no geometry here (sky / background): leave tExit alone.
    if (rawDepth > 0)
    {
        // Scene surface distance from the camera, in WORLD metres along the ray.
        float  sceneEyeDepth   = CorrectedLinearEyeDepth(rawDepth, i.frustumCorrection / i.screenPos.w);
        float3 cameraForwardWS = -UNITY_MATRIX_V[2].xyz;
        float3 rayDirWS        = normalize(i.vertWorldSpace - _WorldSpaceCameraPos);
        float  sceneDistWS     = sceneEyeDepth / max(dot(cameraForwardWS, rayDirWS), 1e-5);

        // World metres spanned by one unit of beam-space t. beam = object*scale,
        // so a beam-space step is (rayDirection / cubeLocalScale) in object space;
        // transform that (direction only) to world and take its length.
        float3 stepWS    = mul((float3x3)unity_ObjectToWorld, rayDirection / cubeLocalScale);
        float  metresPerT = max(length(stepWS), 1e-6);

        float sceneT = sceneDistWS / metresPerT;   // now in beam-space t
        tExit = min(tExit, sceneT);
    }
}

#endif // DIAMOND_BEAM_COMMON_INCLUDED
