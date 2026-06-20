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

        _BeamCutoffThreshold ("Beam Cutoff Threshold", Float) = 0.0001
        _BeamLengthMax ("Beam Length Max (metres)", Float) = 50
        _CubeLocalScale ("Cube Local Scale (compensation)", Vector) = (1, 1, 1, 0)
        _Color ("Color", Color) = (1, 1, 1, 1)
        _BeamIntensity ("Intensity", Float) = 1.0
        _HazeDensity ("Haze Density (1/m)", Float) = 0.05
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

            v2f vert(appdata v) { return DiamondBeamVert(v); }

            // Intersects a ray with the circular cone surface
            //   x^2 + z^2 = (R0 + s*y)^2   (valid for y in [0, beamLength])
            // and folds the (up to two) hits into [tEntry, tExit]. Solves one
            // quadratic a*t^2 + b*t + c = 0.
            //
            // r0   = emitter radius (R0)
            // s    = spread (tan half-angle), already softened by the caller.
            void FoldConeIntoInterval(float3 rayOrigin, float3 rayDirection,
                float r0, float s, inout float tEntry, inout float tExit)
            {
                float ox = rayOrigin.x,    oy = rayOrigin.y,    oz = rayOrigin.z;
                float dx = rayDirection.x, dy = rayDirection.y, dz = rayDirection.z;

                // R(t) = r0 + s*(oy + t*dy) = (r0 + s*oy) + s*dy*t
                float k  = r0 + s * oy;   // R at t = 0
                float kd = s * dy;        // dR/dt

                // x^2 + z^2 - R^2 = 0
                //   (ox + t dx)^2 + (oz + t dz)^2 - (k + t kd)^2 = 0
                float a = dx*dx + dz*dz - kd*kd;
                float b = 2.0 * (ox*dx + oz*dz - k*kd);
                float c = ox*ox + oz*oz - k*k;

                // Near-linear case (ray almost parallel to the cone wall):
                // |a| ~ 0 reduces to b*t + c = 0. Treat as a single grazing
                // crossing; fold it as both entry and exit don't apply, so use
                // the half-space test like the planar walls.
                if (abs(a) < 1e-7)
                {
                    if (abs(b) < 1e-12) return;   // ray runs along the wall: no constraint
                    float t = -c / b;
                    // Outward radial motion at the hit decides entry vs exit.
                    // d/dt (x^2+z^2-R^2) = 2(a t + ... ) -> sign of b here.
                    if (b > 0) tExit  = min(tExit,  t);
                    else       tEntry = max(tEntry, t);
                    return;
                }

                float disc = b*b - 4.0*a*c;
                if (disc < 0)
                {
                    // No real roots. Either the ray misses the cone entirely
                    // (outside, interval stays empty after caps) or it's fully
                    // inside the infinite double-cone. For a>0 (typical: spread
                    // shallow enough that radial terms dominate) a miss means
                    // outside -> collapse the interval so the pixel discards.
                    if (a > 0) { tEntry = 1e20; tExit = -1e20; }
                    return;
                }

                float sq = sqrt(disc);
                float t0 = (-b - sq) / (2.0*a);
                float t1 = (-b + sq) / (2.0*a);
                if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; }

                if (a > 0)
                {
                    // Ray enters the cone at t0, exits at t1: intersect interval.
                    tEntry = max(tEntry, t0);
                    tExit  = min(tExit,  t1);
                }
                else
                {
                    // a < 0: the solid cone is the OUTSIDE of the root span
                    // [t0, t1] (the ray dips through the apex region). The y-cap
                    // planes (folded by the caller) clip to the +Y nappe, so in
                    // practice the relevant segment is the far side; keep the
                    // exit-side root as the exit bound.
                    tExit = min(tExit, t1);
                }
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

                // Build the camera ray in BEAM SPACE (t is in world metres).
                float3 cameraObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayOrigin    = cameraObject * cubeLocalScale;
                float3 rayDirection = normalize(i.vertBeamSpace - rayOrigin);

                float tEntry = -1e20;
                float tExit  =  1e20;

                // Widen the cone wall by the diffusion rate so it tracks the
                // smoothstep softness in the falloff math below.
                float diffusionRateWall = _EdgeSoftness * (0.02 + _HazeDensity);
                float spreadSoft = spreadX + diffusionRateWall;

                // Circular cone side wall (softened radius rate).
                FoldConeIntoInterval(rayOrigin, rayDirection, r0, spreadSoft, tEntry, tExit);

                // Near cap (y = 0, normal -Y) and far cap (y = beamLength, +Y).
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(0, -1, 0), 0,           tEntry, tExit);
                FoldPlaneIntoInterval(rayOrigin, rayDirection,
                    float3(0,  1, 0), -beamLength, tEntry, tExit);

                if (tExit <= tEntry) discard;

                DiamondBeamDepthClamp(i, rayDirection, tExit);
                if (tExit <= tEntry) discard;

                // --- Cross-section density falloff (circular) --------------
                float tMid          = (tEntry + tExit) * 0.5;
                float3 beamMidpoint = rayOrigin + rayDirection * tMid;
                float  distance     = beamMidpoint.y;                       // metres from emitter

                float diffusionRate = _EdgeSoftness * (0.02 + _HazeDensity);
                float softness      = diffusionRate * distance;

                // Circular cross-section: radius grows by spread*d. Add softness
                // to the effective radius so the halo dilutes brightness too.
                float radius      = r0 + spreadX * distance + softness;
                float crossArea   = UNITY_PI * radius * radius;
                float emitterArea = UNITY_PI * r0 * r0;
                float geometricFalloff = emitterArea / max(crossArea, 1e-6);

                // Soft edges: radial distance to the geometric cone wall.
                float geomRadius   = r0 + spreadX * distance;
                float lateral      = sqrt(beamMidpoint.x * beamMidpoint.x + beamMidpoint.z * beamMidpoint.z);
                float distFromWall = geomRadius - lateral;
                float edgeFactor   = smoothstep(-max(softness, 1e-4), max(softness, 1e-4), distFromWall);

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
