// Architectural glass shader by Kavaeric
// Mimics the coloured, opaque-like edges of soda-lime plate glass commonly
// used in architectural applications. Since this doesn't use GrabPass or
// any kind of refractive effect, it's cheap as chips to run.

// Currently doesn't sample the main directional light because I'm not actually
// using a single one in this project. If this gets published or used elsewhere
// that should probably be added.

Shader "ArchGlass/ArchGlass" {
    Properties {
        _Smoothness("Smoothness", Range(0,1)) = 1
        _TintColor("Tint colour", Color) = (1,1,1,1)
        _TintOpacity("Tint opacity", Range(0,1)) = 0.1
        _ReflStrength("Reflection strength", Range(0,1)) = 1
        _EdgeMix("Edge mix", Range(0,1)) = 1
        _EdgeDiffuse("Edge diffuse strength", Range(0,1)) = 0.3
        _EdgeDispersion("Edge dispersion", Range(0,1)) = 0.5
        [HideInInspector] BAKERY_META_ALPHA_ENABLE ("Enable Bakery alpha meta pass", Float) = 1.0
    }

    SubShader {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }

        Blend One OneMinusSrcAlpha
        ZWrite Off
        // Double-sided so the opaque edge is visible through the pane from the front.
        // Back-facing pane (non-edge) fragments are discarded early in the frag.
        Cull Off

        Pass {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "UnityPBSLighting.cginc"

            // Project ambient source. Light Volumes replace Unity's SH probes here, so
            // the opaque edge is lit from the same volumes as the rest of the scene.
            // Thanks, Mochie! Though should probably remove this dependency later down the line.
            // I'm just lazy.
            #include "../../Mochie/Common/LightVolumes.cginc"

            float _Smoothness;
            float4 _TintColor;
            float _TintOpacity;
            float _ReflStrength;
            float _EdgeMix;
            float _EdgeDiffuse;
            float _EdgeDispersion;

            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;

                // GlassEdge vertex colour baked in Blender: white = short edge of the
                // plate (opaque green rim), black = the rest of the pane.
                float4 color : COLOR;
            };

            struct v2f {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float4 color : COLOR;
            };

            // Box projection function. For compatibility with box projection reflection probes
            // which, given this is architectural glass, is kind of important.
            float3 BoxProjection(float3 dir, float3 worldPos, float4 probePos, float3 boxMin, float3 boxMax){
                #ifdef UNITY_SPECCUBE_BOX_PROJECTION
                    UNITY_BRANCH
                    if (probePos.w > 0){
                        // Distance along dir to each box face; nearest positive is the hit.
                        float3 factors = ((dir > 0 ? boxMax : boxMin) - worldPos) / dir;
                        float scalar = min(min(factors.x, factors.y), factors.z);
                        // Re-aim from the probe centre toward that hit point.
                        dir = dir * scalar + (worldPos - probePos.xyz);
                    }
                #endif
                return dir;
            }

            v2f vert (appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                // Cull Off draws back faces too. We only want them where they form the
                // see-through edge; the back of the flat pane body is redundant. Discard
                // those here, before the probe and Light Volume samples, so the extra
                // back-face fragments cost only rasterization, not the glass math.
                float edgeMask = i.color.r * _EdgeMix;
                if (!isFrontFace && edgeMask <= 0.0)
                    discard;

                // View direction from the surface toward the camera, in world space.
                // Back faces have normals pointing away from the camera, so flip them
                // to shade correctly (Fresnel, reflection, SH all use this normal).
                float3 normalDir = normalize(i.worldNormal) * (isFrontFace ? 1.0 : -1.0);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);

                // Mirror the view ray about the surface normal to get the reflection ray.
                float3 reflDir = reflect(-viewDir, normalDir);

                // Anchor the reflection to the probe's box (the room) rather than
                // treating the environment as infinitely distant.
                reflDir = BoxProjection(reflDir, i.worldPos, unity_SpecCube0_ProbePosition,
                                        unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);

                // Rougher surfaces read from higher (blurrier) probe mip levels.
                float perceptualRoughness = 1.0 - _Smoothness;
                float mip = perceptualRoughness * UNITY_SPECCUBE_LOD_STEPS;

                // Fresnel (Filament-style Schlick).
                // Use abs() not saturate() so that the function works with backfaces as well.
                float NdotV = abs(dot(normalDir, viewDir));

                // f0 = reflectance at normal incidence. ~0.04 is the standard value
                // for common dielectrics (glass/plastic); f90 = 1 at grazing.
                float f0 = 0.04;

                // Roughness-aware f90: rough surfaces shouldn't get a full-bright rim,
                // so cap the grazing reflectance by smoothness.
                float f90 = saturate(_Smoothness + f0);

                // Schlick: lerp(f0, f90, (1 - NdotV)^5).
                float fresnel = f0 + (f90 - f0) * pow(1.0 - NdotV, 5.0);

                // Artistic reflection dial. Scaling the Fresnel term itself (rather than
                // just reflCol) is deliberate: fresnel is the lerp weight for the whole
                // reflection layer, so this pulls back the alpha ramp as well as the
                // colour. Without that, dimming only the colour would still leave the
                // pane ramping to near-opaque coverage at grazing angles and hiding
                // whatever is behind it. Physically 1.0 is correct for bare glass; below
                // that stands in for AR coatings and for the fact that a stack of panes
                // in a transparent queue compounds coverage in a way one real window
                // never does.
                fresnel *= _ReflStrength;

                // Sample the active reflection probe. Glass is dispersive (IOR varies by
                // wavelength), which shows as coloured fringing at grazing angles and edges.
                float3 reflCol;
                float dispersion = edgeMask * fresnel * _EdgeDispersion;
                UNITY_BRANCH
                if (dispersion > 0.0){
                    // Split the reflection direction per channel: blue bends most, red
                    // least. Offsetting along viewDir spreads the sampled directions by
                    // a tiny wavelength-dependent angle, so a high-contrast reflection
                    // lands on slightly different texels per channel -> spectral fringe.
                    float3 rDir = reflDir + viewDir * dispersion;
                    float3 bDir = reflDir + viewDir * -dispersion;
                    float r = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, rDir,    mip), unity_SpecCube0_HDR).r;
                    float g = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflDir, mip), unity_SpecCube0_HDR).g;
                    float b = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, bDir,    mip), unity_SpecCube0_HDR).b;
                    reflCol = float3(r, g, b);
                }
                else {
                    reflCol = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflDir, mip), unity_SpecCube0_HDR);
                }

                // The transmission layer: what the glass does to light passing through
                // it, before surface reflection is added on top. Premultiplied to
                // (color*coverage, coverage) so colour is gated by its own coverage.
                float4 tintLayer = float4(_TintColor.rgb * _TintOpacity, _TintOpacity);

                // The edge colouration is a transmission effect (absorption seen through
                // the glass body), so it follows Fresnel inverted: strongest head-on
                // where transmission dominates, weakest at grazing angles where surface
                // reflection takes over. Weight the composite mask by (1 - fresnel).
                float edgeBlend = edgeMask * (1.0 - fresnel);

                // Treat the opaque edge as a real surface, so let the room's baked ambient
                // light it. Sample the Light Volume SH at this fragment and evaluate it
                // along the surface normal, then modulate the absorption colour by it.
                // The SH result only survives where edgeBlend > 0 (the rim), so the pane
                // body (the fragment that stacks in overdraw) skips the sample entirely.
                float4 transmission = tintLayer;
                UNITY_BRANCH
                if (edgeBlend > 0.0) {
                    float3 shL0, shL1r, shL1g, shL1b;
                    LightVolumeSH(i.worldPos, shL0, shL1r, shL1g, shL1b);
                    float3 edgeAmbient = LightVolumeEvaluate(normalDir, shL0, shL1r, shL1g, shL1b);
                    float3 edgeCol = _TintColor.rgb * lerp(1.0, edgeAmbient, _EdgeDiffuse);
                    float4 edgeLayer = float4(edgeCol, 1.0); // opaque, already premultiplied
                    transmission = lerp(tintLayer, edgeLayer, edgeBlend);
                }

                // Surface reflection sits on top of transmission regardless of what's
                // behind, so it applies to edge and face alike. Fresnel weights it:
                // 0 = all transmission (face-on), 1 = all reflection (grazing).
                float4 reflLayer = float4(reflCol, 1.0);
                float4 outCol = lerp(transmission, reflLayer, fresnel);

                // Already premultiplied, so this feeds Blend One OneMinusSrcAlpha directly.
                return outCol;
            }
            ENDCG
        }

        // Bakery alpha meta pass: tells Bakery how opaque the surface is per-texel while
        // baking, so light passes through the near-clear pane body but is occluded at the
        // dense (opaque) edges, matching the real shader's coverage. Requires Bakery
        // Light Probe Mode L1/L2. See Bakery/examples/shaders/Baked_Alpha_meta.shader.
        Pass
        {
            Name "META_BAKERY"
            Tags { "LightMode" = "Meta" }
            Cull Off
            CGPROGRAM
            #pragma vertex vert_archglassmt
            #pragma fragment frag_customMeta

            #include "UnityStandardMeta.cginc"
            #include "../../Bakery/BakeryMetaPass.cginc"

            float4 _TintColor;
            float _TintOpacity;
            float _EdgeMix;

            // Stock BakeryMetaInput/v2f_bakeryMeta carry no vertex colour, but our edge
            // coverage lives in the GlassEdge vertex mask, so extend both to pass it.
            struct ArchGlassMetaInput
            {
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float4 color : COLOR;
            };

            struct v2f_archglassMeta
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float4 color : COLOR;
            };

            v2f_archglassMeta vert_archglassmt (ArchGlassMetaInput v)
            {
                v2f_archglassMeta o;
                // Same lightmap-space clip position the stock Bakery meta vert uses.
                o.pos = float4(((v.uv1.xy * unity_LightmapST.xy + unity_LightmapST.zw)*2-1) * float2(1,-1), 0.5, 1);
                o.uv = v.uv0;
                o.color = v.color;
                return o;
            }

            float4 frag_customMeta (v2f_archglassMeta i) : SV_Target
            {
                // Bakery asks for transparency when the .w control flag is set.
                if (unity_MetaFragmentControl.w)
                {
                    // Coverage the shader actually presents: near-clear pane body
                    // (_TintOpacity) rising to fully opaque on the GlassEdge mask.
                    float edgeMask = i.color.r * _EdgeMix;
                    float alpha = lerp(_TintOpacity, 1.0, edgeMask);
                    return alpha;
                }

                // Regular Unity meta pass: report albedo (the tint) for GI colour bleed.
                UnityMetaInput o;
                UNITY_INITIALIZE_OUTPUT(UnityMetaInput, o);
                o.Albedo = _TintColor.rgb;
                return UnityMetaFragment(o);
            }
            ENDCG
        }
    }
}
