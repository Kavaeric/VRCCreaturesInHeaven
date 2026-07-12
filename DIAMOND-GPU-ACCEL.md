# Diamond Lightshow: Texture-Baked Playback

## Context

DiamondManager drives ~420 (nice) lighting fixtures each frame by reading their
per-fixture proxy transforms (brightness, colour, zoom, focus, beam intensity) in an
Udon `Update()` loop and pushing the values into per-fixture `MaterialPropertyBlock`s.

Profiling established a hard floor: Udon boxes every `Vector3`-returning transform
read onto its heap (~550+ boxed allocs/frame at 28 bytes; callstack
`GC.Alloc ← StrongBox.get_Value ← UdonHeap.CopyHeapVariable`). Periodic GC collection
of that churn is the intermittent frame-time hitching. Boxing is per extern *call*,
not per component, and no non-boxing transform accessor exists in Udon, so the cost
is irreducible while the per-fixture read loop exists. Loop micro-opts shaved it but
can't cross the floor. See memory `udon_transform_read_boxing.md`.

The lightshow is fully pre-authored (a fixed timeline synced to one track). This
makes a bake-and-play-back approach the right fix: it relocates all per-fixture work
from the Udon CPU loop to the GPU, where it is effectively free.

## The approach

Bake the authored show to a texture (rows = fixtures, columns = frames, channels in
RGBA). At runtime:

- **Udon, per frame — O(managers), not O(fixtures):** each manager reads a **normalised
  [0,1] time** from an Animator float parameter (`animator.GetFloat(paramName)`), maps it
  to a frame column, and writes that column into its OWN slot of a shared global float
  array (`VRCShader.SetGlobalFloatArray("_LightshowFrames", …)` — confirmed Udon-exposed).
  One array write per manager per frame; no per-fixture loop. **The per-fixture Udon loop
  is deleted entirely.** Diamond takes its time input from the Animator param ONLY — it
  never references Heartache (see Module independence below).
- **Each fixture, on the GPU — free:** the beam shader and a new lamp-glow additive
  shader read their own row from the bake texture at their manager's current frame
  (`_LightshowFrames[_ShowIndex]`, two-column lerp for smoothness), unpack their channels,
  apply. Per-fixture fan-out happens in the shader, in parallel, as part of rendering that
  already runs.
- **Per-fixture static identity, seeded ONCE** into the head + beam property blocks in
  `DiamondManager.Start()` (reusing the existing one-time emitter-size seed pattern),
  never written per frame: `_FixtureRow` (this fixture's texture row) and `_ShowIndex`
  (which manager/show owns it → which array slot to read). `_LightshowTex` and the layout
  constants are also seeded per-block at Start (see Addressing model below).

**The proxy transform system is retained unchanged as the edit-time authoring +
preview layer.** The Animator keeps running at runtime anyway — it must, because it
also drives the physical **moving-head transforms** (`Head.localRotation`), which are
native Animator→transform and were never on the Udon path. So at runtime: the Animator
drives head geometry (as today), while the shaders read baked shader-values from the
texture. The bake is an offline editor step that steps the clip frame-by-frame and
reads the same proxy values Udon reads today (boxing is irrelevant offline).

## Module independence (Diamond must not depend on Heartache)

Diamond is a standalone module and must be usable without the Heartache engine.
So Diamond never references `HeartacheMusicEngine`. Instead it reads a normalised
[0,1] playback time from an Animator float parameter — the same decoupling pattern
`MomentAnimatedLightVolume` uses (`AnimTimeParameter` read via `animator.GetFloat`,
with a serialized `[Range(0,1)] float Time` fallback when no param is set).

- DiamondManager gets: an `Animator` reference + a parameter name (default `_Time`),
  and a fallback `[Range(0,1)] float Time` field (inspector-scrubbable for standalone
  testing with no driver at all).
- **Heartache integration is zero-coupling and already in place:** Heartache already
  calls `animator.SetFloat("_Time", normTime)` on the rig's Animator every frame — that
  is literally how the clip is driven today. Diamond can just read the same `_Time` param
  that already exists. Neither module references the other; anything that can drive an
  Animator float 0→1 (Heartache, a plain AnimationClip lerp, a manual scrub) drives the
  show.

## Addressing model (multi-manager, multi-fixture-type safe)

Two independent axes must NOT be conflated:

- **Render-type axis:** which shader/material a fixture uses — BeamRound, BeamRect, and
  future types (BeamRoundLite, BeamGobo, …). This is about how a fixture *renders*.
- **Show-identity axis:** which manager/show owns a fixture — which bake texture it reads,
  which frame-clock drives it. This is about what *data* drives a fixture.

The mistake to avoid is putting show-identity on the *material* (e.g. per-manager
material copies). That multiplies material count by (types × managers), breaks instancing
batches, and still can't give two managers different frame-clocks on a shared material.

**So show-identity rides on the per-fixture instance (property block) + one small global
array, completely decoupled from material type:**

| Data | Where | When set | Cost |
|---|---|---|---|
| `_FixtureRow` | per-instance block | once at Start | — |
| `_ShowIndex` | per-instance block | once at Start | — |
| `_LightshowTex` | per-instance block (`SetTexture` on block) | once at Start | — |
| layout constants (`_TexelsPerFixture`, slot indices) | per-instance block (or global) | once at Start | — |
| `_LightshowFrames[]` | **global float array** (`SetGlobalFloatArray`) | once **per manager per frame** | O(managers) |
| the shader (Round/Rect/Gobo/Lite) | the material | authoring | — |

- Each manager owns an `_ShowIndex` (0, 1, 2, …). At Start it seeds that index into every
  one of its fixtures' blocks. Each frame it writes its computed frame column into
  `_LightshowFrames[_ShowIndex]`. The shader reads `_LightshowFrames[_ShowIndex]`.
- **Per-manager independence + concurrent multi-part shows are free:** N managers = N array
  slots = N independent frame-clocks, at O(N) writes/frame total (N is tiny), regardless of
  fixture count or how many material/shader types exist.
- **`_DiamondLightshow1/2`-style named properties are NOT needed** — one global array with a
  per-fixture index scales to any number of shows without new shader properties.
- **Render-type axis is fully orthogonal:** add as many fixture shaders as you like; the
  lightshow plumbing (row + show-index on the instance, frame in the array) does not care
  which material a fixture uses. A BeamGobo and a BeamRound owned by the same manager both
  read `_LightshowFrames[_ShowIndex]` at their own `_FixtureRow`.

Note: `_LightshowTex` in a MaterialPropertyBlock (`block.SetTexture`) is per-renderer
without instantiating/breaking the shared material — this is what lets different managers
bind different bake textures while their fixtures still share one material per render-type.

## Confirmed design decisions

- **Time domain:** bake the 92.8s rig clip (5568 frames @ 60fps). At runtime read the
  normalised `_Time` Animator param (0→1 over the clip) and map to a frame column. This
  is the same param Heartache already drives; Diamond is agnostic to what sets it.
  Preserves existing sync.
- **Precision: 8-bit RGBA32.** Works because colour is SDR [0,1] and the HDR look comes
  from **intensity/brightness** multiplying it. Colour channels bake directly; the
  intensity/brightness channel needs an encoding that carries its >1 range (see Open
  items — likely a baked scale factor or a 2×8-bit pack for that one channel).
- **Moving heads:** physical `Head` rotation/position stays native Animator-driven.
  Not baked nor touched. Already fast (never boxed).
- **Lamp glow:** the fixture BODY stays entirely off-the-shelf (Mochie). Only the 
  lamp lens is Diamond-driven. The lens keeps a Mochie material for its off/material look
  (glass, Fresnel, reflections) plus a second additive Diamond pass on the same lens submesh
  that adds baked colour×intensity glow on top. The additive Diamond shader is trivial: sample
  the row, output `colour×intensity`, additive blend, nothing else. This is what lets the Udon
  per-fixture loop vanish completely (nothing left for it to drive) while keeping our shader
  minimal, since Mochie owns all material character.
  - **Asset spec (future 3D fixtures):** the lamp lens must be a separately-
    addressable renderer (its own submesh/material) so it can carry its own
    `_FixtureRow` and the additive pass. This is the `HeadRenderer` in the current data
    model — already a distinct reference on `DiamondFixtureDefinition`. The current two
    fixture models are throwaway proxy sketches; real models will be authored to this
    spec.
- **On/off:** `activeSelf` off always coincides with zero output, so it bakes as zero
  brightness/intensity. The shaders' existing zero early-out handles it. No dedicated
  channel.

## Channels to bake (per fixture, per frame)

The animated per-fixture shader values (from the shader inventory — beam props are all
in the `Props` instancing buffer):

| Channel | Source proxy (edit-time) | Consumer |
|---|---|---|
| Colour R, G, B (SDR) | `LampProps.localScale.xyz` | lamp glow additive, beam `_Color` |
| Brightness / intensity (HDR mult) | `LampProps.localPosition.y` × `BeamProps.localScale.y` | both |
| Zoom (tan half-angle) | `BeamProps.localEulerAngles.x` | beam `_ZoomX`/`_ZoomZ` |
| Focus (0-1) | `BeamProps.localPosition.y` | beam `_Focus` |

~6 scalars/fixture for the CURRENT feature set (3 colour + brightness + zoom + focus).
Beam intensity and lamp brightness may collapse into one combined multiplier at bake
time (both feed the final HDR scale). Manager-wide atmosphere (`_HazeDensity` /
`_ScatterStrength` / `_Anisotropy`) stays global uniforms, NOT baked per-fixture.

### Channel layout: fixed per bake, sized to what the show animates

The full Diamond fixture may eventually expose up to ~16 channels (add shear X/Z, XYZ
position, XYZ rotation, etc.). We do NOT reserve all 16 unconditionally, and we do NOT
do per-fixture dynamic packing. Instead:

- **The bake picks the channel set from what the clip actually animates**, fixed across
  all fixtures for that bake. A show that animates 6 channels bakes 6; a future show
  animating 12 bakes 12. The theoretical 16-channel width only ever materialises if a
  show genuinely animates all 16 (and even then it's ~37 MB — fine).
- **One global layout descriptor on DiamondManager** records the encoding: channel
  count, texels-per-fixture, and the slot→meaning map. This is the single piece of
  "how is this texture encoded" metadata — GLOBAL, not per-fixture.
- **Shader addressing stays constant per bake:** `Load(fixtureRow, frameCol *
  texelsPerFixture + slot)`. No per-fixture indirection, no dependent layout reads in
  the hot path.
- **Round vs rect is free:** they're already separate shaders. Within the fixed layout,
  the round shader reads the single `zoom` slot and ignores `zoomZ`; the rect shader
  reads both. No per-fixture table needed — the shader split does the work. (Bake can
  write `zoomZ = zoomX` for round rows so no slot looks garbage.)
- **Beamless fixtures** (null `BeamProps`): the lamp-glow shader reads only
  colour+intensity slots; it simply never reads the beam slots. Same fixed layout, fewer
  reads — no separate allocation.

**Why this over per-fixture dynamic packing:** per-fixture packing optimises the SMALL
axis (one zoom-channel difference between round and rect) for real, permanent shader
indirection cost. Per-BAKE channel selection optimises the LARGE axis (whole channels
absent from a show, e.g. no shear/focus, zeroed across all 420 fixtures at once) for
near-zero cost. True per-fixture dynamic allocation is **explicitly deferred** — revisit
only if texture size ever becomes a real problem, which at these dimensions it will not.

Size at the current 6-channel set: ~2 RGBA32 texels/fixture → 420 × 5568 × 2 × 4 bytes
≈ **~19 MB** (before column-wrap padding). Worst case at full 16 channels ≈ ~37 MB.

## Implementation status

- **Phase 1 (bake tooling): DONE.** `DiamondLightshowBaker` (Tools ▸ Diamond ▸ Bake
  lightshow…) + `DiamondLightshowFormat`. Vertical-stacked layout (col=frame,
  row=fixture×2+slot), 2 texels/fixture, per-bake HDR scales, PNG output with enforced
  data-texture import settings.
- **Phase 2 (beam shader): DONE, behind `DIAMOND_LIGHTSHOW_TEX` keyword.** Common cginc
  adds `_LightshowTex`/`_LightshowFrames[]`/`_FixtureRow`/`_ShowIndex` + a two-column-lerp
  unpack sampled in vert, passed to frag via v2f. Frag reads animated values through
  `DIAMOND_*(i)` resolver macros (texture path vs instancing-buffer path). Keyword off =
  byte-identical old behaviour.
- **Phase 4 (manager): DONE.** `DiamondManager` gains descriptor fields + time-input
  fields (Animator param, no Heartache dep). Start resolves `_textureMode` (bake texture
  present + fixture count matches), seeds `_FixtureRow`/`_ShowIndex` per block + global
  scalars, enables the keyword. Update in texture mode is O(1): write
  `_LightshowFrames[ShowIndex]` + push any animated atmosphere as globals. Falls back to
  the proxy loop when no bake is assigned.
- **VERIFIED IN-PLAY (Phases 1/2/4).** `Material.EnableKeyword` works at runtime (the
  runtime path enabled it, no edit-time hedge needed). Profiled: `DiamondManager.Update`
  now **sub-1ms** (was multi-ms with the per-fixture boxing loop), frame times stable at
  90fps in-game, GC hitches gone. Cost shifted to GPU as expected: real-time reflection
  probes ~0.5ms→2-3ms and camera render ~1-2ms→3-4ms (beams now do the texture sample +
  lerp in vert, and render into probes). Follow-up GPU trims noted below (probe culling-
  mask exclusion for beams/glow; confirm the beam intensity early-out still culls off
  fixtures).
- **Phase 3 (lamp glow): DONE, VERIFIED IN-PLAY.** `Diamond/Head/DiamondLampGlow.shader`
  — a trivial additive pass (`Blend One One`, no keyword; always texture-driven) that
  samples ONLY the colour row (drivenColour = colour×brightness, already baked) at
  `_UdonDiamondLightshowFrames[_ShowIndex]`, two-column lerp, ×`_UdonDiamondLightshowColourScale`,
  ×per-material `_GlowScale`. Reads the same `_FixtureRow`/`_ShowIndex` per-instance
  addressing as the beam. `DiamondManager.Start` now seeds those into the HEAD block and
  applies it in texture mode (previously the head was only driven by the proxy-path
  `ApplyFixture`, which doesn't run in texture mode — so lamp glow was dark until this).
  Rides as a SECOND material on the lamp lens submesh alongside its Mochie material. Global
  scalars it needs (`ColourScale`, `TexelsPerFixture`, `FrameCount`, `Frames[]`) are already
  published by the manager, so no new manager plumbing beyond the head-block seed.
- **`BeamIntensityScale` master: DONE (needs in-play verify).** Both static and animated
  now work in texture mode via a global `_UdonDiamondBeamIntensityScale`. The beam shader
  multiplies it onto the texture-recovered intensity at the vert resolution point (so it
  flows through the early-out and beam-length derivation), mirroring the proxy path's
  per-fixture `beamIntensity * BeamIntensityScale`. Manager seeds it once in Start (static
  case) and pushes it per-frame in `PushAnimatedAtmosphereGlobal` when animated. Confirmed
  NO double-application: the baker stores RAW `beam.localScale.y` (not ×scale), and the
  descriptor's `BeamScale` is the per-bake HDR peak de-scale — a distinct thing from the
  master. Lamp glow is unaffected (glow reads the colour row; master scale is beam-only).

**Core rework complete.** All five phases done and verified in-play. The Udon-boxing
floor is gone (Update sub-1ms, stable 90fps, no GC hitches). Remaining work is GPU-side
tuning (below), not the Udon architecture.

- **Edit-mode preview parity: DONE (needs in-play/edit verify).** Problem: once the
  texture path shipped, scrubbing the clip in EDIT mode stopped previewing the beams and
  lamp glow (heads still moved — Animator-driven). Cause: the shaders were on the texture
  path, but Udon (which sets `_UdonDiamondLightshowFrames`) doesn't run in edit mode, so
  there was no frame to sample. Fix: the `DIAMOND_LIGHTSHOW_TEX` keyword is now owned per
  mode — `DiamondFixtureMapPreview` forces it OFF in edit mode (shaders read the live
  proxy values it writes into the blocks), `DiamondManager.Start` forces it ON in play.
  They never run together, so the material's saved keyword state is irrelevant. Required:
  giving `DiamondLampGlow` a keyword gate + `_EmissionColor` edit path (it was texture-only
  and had no proxy fallback); extending the manager's keyword-enable to HEAD renderers via
  `sharedMaterials` (the lamp lens carries Mochie + glow, so `.sharedMaterial` singular
  missed the glow slot); and the preview disabling the keyword on beam+head materials each
  edit tick (guarded so it only toggles on a state change, no per-tick asset churn).

## Follow-up: reflection-probe cost (GPU-side, not Udon)

The rework shifted per-fixture cost to the GPU as intended. The loudest remaining cost is
real-time reflection probes: with probes OFF the scene runs ~6-10ms; the probes add the
bulk of the beam/glow render cost because each probe re-renders the beams+glow per face
per refresh. The user wants to KEEP real-time reflections (immersion) — so this is an
optimisation problem, not a "turn it off" one.

**Root cause of the probe regression (why it costs MORE than the old CPU path, same
animation):** the old CPU path called `beamRenderer.gameObject.SetActive(false)` on OFF
fixtures (`ApplyFixture`, the `if (off)` branch). An inactive GameObject is removed from
the render list ENTIRELY — no draw call, on any camera or probe. During the show a large
fraction of the 420 beams were deactivated at any moment, so the probe rendered only the
handful that were on. The texture path never deactivates anything (that's the whole point
— the CPU stops touching per-fixture state), so all 420 beams are always active, always
submitted as draws per cube face per probe. The frag side is NOT the problem: the vertex
early-out (`DiamondBeamCommon.cginc` ~L445) collapses off beams to a zero-area vertex, so
they rasterize/shade nothing. The cost is (a) draw-call submission for 420 always-active
renderers × 6 faces × N probes, and (b) the vert still runs — including its 4× Texture2D
`.Load` + lerps just to decide it's off — for all of them. Layer-based culling-mask
exclusion (option 2) removes beams from the probe render list at the source, restoring the
"these don't exist for the probe" property SetActive(false) used to provide, but statically
and for free.

Options, cheapest-effort first:

1. **Time-slice / lower probe refresh rate.** Probes on *Every Frame* are overkill —
   reflections of the show are low-frequency detail the eye won't catch at 15-20Hz. Switch
   to *Via Scripting* and refresh one probe per N frames / on a timer. Usually the single
   biggest win for the least work.
2. **Exclude the beam layer from probe culling masks.** Volumetric beams are view-dependent
   (the HG phase function literally depends on camera angle), so a reflection of a beam is
   physically meaningless anyway — excluding beams from the probe pass is a correctness fix
   as much as a perf one. Keep fixtures/floor/architecture reflecting (the immersion that
   matters). Lamp GLOW is a real emissive surface, more defensible to keep — exclude beams
   only, at least at first.
3. **Lower probe cubemap resolution.** Reflections read blurry in a hazy venue; 64-128 often
   looks identical to 256 and is a straight multiplier on probe render cost.
4. **Fewer / better-placed probes** — scene-authoring, not a perf knob.

Instinct: (1)+(2) together likely restore real-time reflections at an affordable cost.
Confirmed: the beam vertex early-out (`DiamondBeamCommon.cginc` ~L445) DOES collapse off
beams to a zero-area vertex, so the frag never runs for them — the probe cost is draw-call
+ vert, not overdraw (see root-cause above).

**Tried so far (2026-07):** beams moved to a `DiamondBeam` layer, excluded from probe
culling masks (option 2). Helped "a little bit, but not much" — consistent with the cost
being draw-call submission of 420 always-active renderers rather than beam fill. Layer
exclusion removes beam DRAWS from the probe but the rest of the scene (fixtures, glow,
architecture) still re-renders per face per refresh at whatever rate the probes use, so the
big lever is really option 1 (refresh rate), not (2). NEXT to try: time-slice the probe
refresh (option 1) — that's the untried high-value one.

### Deferred idea: pre-baked reflection flipbook (parked — revisit later)

User raised: since the whole show is pre-choreographed, could the reflections be baked to a
cubemap flipbook (analogous to the fixture-value bake) and swapped/blended per frame? Verdict
after analysis: theoretically the max saving IF beam reflections must be kept, but blocked by
two hard walls in VRChat/Unity, so PARKED, not pursued:
- **Memory wall.** One HDR cubemap (256²×6) is several MB; a flipbook over a 92s show is
  hundreds-to-thousands of frames = GB-scale. Only viable at very low res (≤64²) + very low
  fps (2-4 with blending), i.e. approximate.
- **You don't own the cubemap sampling path.** Reflective materials (Mochie, Standard) read
  Unity's engine-bound `unity_SpecCube0`; Udon can't cleanly swap it per frame. A real
  flipbook would need custom reflection shaders on everything reflective (replacing Mochie's
  reflection) — big scope jump.

Cheaper alternatives identified for when this is revisited, keyed to what reflection actually
matters:
- **Static room reflections only** (architecture/floor, not the animated show): a SINGLE
  static baked probe with the beam layer excluded — ~zero per-frame cost, robust. Loses the
  show's colour bleeding into reflections. **Likely the best real answer** since beams
  shouldn't reflect anyway (view-dependent scattering).
- **Show colour in reflections:** needs real-time OR a cheap fake — e.g. an Udon-driven
  GLOBAL reflection tint (one colour, sampled from the show's overall state) layered over a
  static probe, instead of a full cubemap flipbook. Hybrid static-probe + global-tint is far
  cheaper than a flipbook and captures most of the perceived effect.

The AskUserQuestion on "which reflection do you care about" was deferred by the user ("save
this for a later date") — so the static-probe-vs-tint decision is still open when this
resumes.

## Implementation phases

Each phase leaves the project buildable/working.

### Phase 1 — Bake tooling (offline, produces the texture)
Follow the **Moment module precedent** closely:
- `Moment/Editor/MomentEWinBaker.cs` — the `AnimationMode.StartAnimationMode()` /
  `SampleAnimationClip(go, clip, t)` / deferred-read-by-one-tick loop. Reuse this
  sampling structure.
- `Moment/Editor/MomentTextureWriter.cs` — GUID-preserving native `.asset` texture
  writes (`LoadAssetAtPath` + in-place `SetPixels`/`Apply`, no PNG), column-wrap
  packing, 2048-cap handling (`MomentALVFormat` math).
- New: `Diamond/Editor/Bakery/DiamondLightshowBaker.cs` (+ UXML window, matching
  Moment's window style). Steps the rig clip via the existing
  `Seq 01 lighting rig bake.controller` (already has `m_TimeParameterActive: 0` for
  AnimationMode driving), reads each fixture's `LampProps`/`BeamProps` proxies (same
  channel mapping DiamondManager reads), packs into the RGBA32 texture using the
  fixture index `i` from `DiamondManagerDefinition.BakeFixtures`' crawl order as the
  row.
- **Channel-set detection:** before sampling, scan the clip's curve bindings
  (`AnimationUtility.GetCurveBindings`) for the proxy paths to decide which channels are
  actually animated (prior art: the beam-intensity strip work in memory
  `anim_clip_curve_stripping.md` did exactly this binding scan). Build the fixed
  per-bake layout from the animated set.
- **Output:** a `Texture2D` asset + the **global layout descriptor** written onto
  `DiamondManager` (or its Definition): frame count, row count, texels-per-fixture, the
  slot→meaning map, and packing params (column-wrap, etc.). This descriptor is what both
  the shader (via seeded constants) and the runtime frame math consult.

### Phase 2 — Beam shader reads the texture
- Modify `Beam/DiamondBeamCommon.cginc` + both shape shaders. Add per-instance
  `_LightshowTex` (Texture2D), `_FixtureRow`, `_ShowIndex` (from the property block); a
  global float array `_LightshowFrames[]`; and the layout constants (`_TexelsPerFixture`
  + the slot indices this shader needs).
- Resolve the frame first: `float frame = _LightshowFrames[_ShowIndex];` — this fixture's
  manager's current column (see Addressing model).
- In the **vertex** shader (where `_Color`/`_BeamIntensity`/`_ZoomX`/`_ZoomZ`/`_Focus`
  are already read as per-instance constants and `beamLength` is derived once), replace
  those `UNITY_ACCESS_INSTANCED_PROP` reads with a texture unpack: `Load` two adjacent
  frame columns at `(_FixtureRow, floor(frame) * _TexelsPerFixture + slot)` and the next
  frame, lerp by frac, unpack. Pass down via existing `v2f` pattern (like `beamLength`).
  Use the existing depth-texture sample (`SAMPLE_DEPTH_TEXTURE`, `#pragma target 5.0` →
  `Load` available) as the template for adding a texture.
- **Round shader reads the single `zoom` slot; rect reads `zoom` + `zoomZ`.** Each shader
  hard-codes its own subset of the fixed layout — no per-fixture indirection.
- Static props (`_EmitterWidth/Height`, `_CubeLocalScale`) and atmosphere globals stay
  exactly as they are.

### Phase 3 — Lamp additive glow shader
- New minimal ADDITIVE shader `Diamond/Head/DiamondLampGlow.shader`: samples the same
  `_LightshowTex` row at `_LightshowFrames[_ShowIndex]` (colour×intensity), outputs it as
  additive emission (`Blend One One`, no depth write / lit surface work). ~10 lines. Add
  per-instance `_LightshowTex`/`_FixtureRow`/`_ShowIndex` + the `_LightshowFrames[]` read
  (share the beam's unpack helper in a small common include if practical).
- On the fixture prefab, the lamp lens gets this as a **second material/pass alongside
  its Mochie material** (Mochie = off/material look, Diamond additive = baked glow).
  The lamp lens renderer is the `HeadRenderer` reference; it carries `_FixtureRow`.
- The fixture BODY is untouched — pure Mochie, no Diamond shader, no `_FixtureRow`.

### Phase 4 — Runtime manager rewrite
- `DiamondManager.Start()`: cache property IDs (`_idFixtureRow`, `_idShowIndex`,
  `_idLightshowTex`, layout). In the existing per-fixture loop, seed into BOTH the beam
  block (existing) and head block (new seeding): `_FixtureRow = i`, `_ShowIndex` (this
  manager's array slot), `_LightshowTex` (this manager's bake texture, via
  `block.SetTexture`), and the layout constants from the descriptor. Keep emitter-size
  seed. Every one of these is a one-time write; none recur per frame.
- `DiamondManager.Update()`: replace the entire per-fixture loop with a few lines: read
  normalised time `t = (_hasTimeParam ? animator.GetFloat(TimeParameter) : Time)`, map to
  clip frame, then write this manager's slot of the shared global array —
  `_frames[ShowIndex] = frame; VRCShader.SetGlobalFloatArray(_idLightshowFrames, _frames);`
  (`_frames` is a small static-length float[] sized to the max manager count; a manager
  writes only its own slot). Delete `ApplyFixture`, the dirty-check arrays, the beam-read
  path. Keep manager-wide atmosphere handling (still cheap).
- Show-identity + time fields: `int ShowIndex` (this manager's array slot; assigned per
  manager — inspector field or auto-assigned at Start), plus time input mirroring
  `MomentAnimatedLightVolume`: `Animator AnimatorSource`, `string TimeParameter = "_Time"`,
  `[Range(0,1)] float Time`. Resolve `_hasTimeParam` in `Start()`. **No Heartache
  reference** — Diamond reads the Animator param only.
- **Shared frame array coordination:** `_LightshowFrames[]` is one global array all
  managers write into (each its own slot). Sizing/ownership needs a tiny convention — see
  Open items.

### Phase 5 — Cleanup
- Retire `DiamondFixtureDriver.cs` (already stale — writes nonexistent
  `_SpreadX`/`_SpreadZ`).
- Edit-mode preview (`DiamondFixtureMapPreview`) stays as-is (authoring path). It reads
  proxies live; unaffected by runtime changes.

## Critical files

- **New:** `Diamond/Editor/Bakery/DiamondLightshowBaker.cs` (+ `.uxml`),
  `Diamond/Head/DiamondLampGlow.shader` (additive glow pass for the lamp lens)
- **Modify:** `Diamond/Beam/DiamondBeamCommon.cginc`,
  `Diamond/Beam/DiamondBeamRect.shader`, `Diamond/Beam/DiamondBeamRound.shader`,
  `Diamond/DiamondManager.cs`, `Diamond/DiamondManagerDefinition.cs` (store bake
  sidecar / row assignment)
- **Reference (reuse patterns):** `Moment/Editor/MomentEWinBaker.cs`,
  `Moment/Editor/MomentTextureWriter.cs`, `Moment/MomentAnimatedLightVolume.cs`
  (the Animator-float-param time input + `MomentALVFormat` packing math)
- **Time input:** an Animator float param (default `_Time`), read via
  `animator.GetFloat`. NO Heartache dependency. Heartache happens to drive this same
  param today (`animator.SetFloat("_Time", …)`), but Diamond neither knows nor cares.

## Open items to resolve during implementation

1. **Intensity HDR encoding in 8-bit. RESOLVED → per-bake constant scale.** Colour is
   SDR but brightness×beam-intensity is HDR (>1). The baker measures the actual peak of
   the combined intensity multiplier across all fixtures × all frames during sampling,
   stores `intensity / peak` in [0,1] in the texture, and writes `peak` into the layout
   descriptor (`IntensityScale`). The shader multiplies back by `_IntensityScale`. Exact
   within 8-bit quantization, no 2-channel packing, one scalar of metadata. (Rejected the
   2×8-bit pack: needless shader complexity when a single per-bake scale recovers the
   range losslessly-enough — 8-bit mantissa over the real dynamic range is fine for
   emissive glow, which is what the intensity feeds.) The measured peak is logged at bake
   so we can sanity-check it. NOTE: colour (`LampProps.localScale.xyz`) is assumed SDR
   [0,1]; the baker clamps/warns if a colour channel exceeds 1 so an HDR colour authoring
   mistake is caught rather than silently clipped.
2. **Row-index stability.** Row = crawl-order `i`. Fragile to fixture reordering until
   stage-3 identity→index lands. Bake row from the same crawl as `BakeFixtures`;
   consider keying via `SceneIds[i]` for re-bake robustness.
3. **Bake iteration speed.** In editor play mode, keep using the proxy/Animator path
   (no bake needed) so tweaks are instant; only run the bake for actual VR builds.
   Confirm the runtime path can fall back to proxy-driven when no bake texture is
   assigned, so the editor stays live.
4. **Frame interpolation vs snap.** Two-column lerp is planned (smooth at 90fps render
   over 60fps bake). Confirm no channel (e.g. hard strobes) suffers from lerping across
   an intended instant cut — may want a "snap" flag or per-channel nearest.
5. **Channel-set detection reliability.** The bake infers the animated channel set from
   the clip's curve bindings. Decide the exact rule: a channel is "in" if ANY fixture
   animates its proxy path, else it's dropped and the shader uses the fixture's static
   rest value. Confirm the binding scan matches the proxy channel mapping precisely (the
   same path/attribute care the `anim_clip_curve_stripping` work needed), and that a
   dropped channel's static fallback is correct. Also decide whether the channel set is
   auto-detected or an explicit per-bake checklist (auto + override is safest).
6. **Layout descriptor shape. RESOLVED for the current 6-channel set → a fixed 2-texel
   packing on `DiamondManagerDefinition`.** For the current channel set the packing is
   fixed at **2 RGBA32 texels per fixture per frame**:
   - **Texel 0:** `(colourR, colourG, colourB, intensity01)` — SDR colour + scaled HDR
     intensity (see #1). One `Load` gives the lamp-glow shader everything it needs.
     `intensity01 = brightness × beamIntensity / IntensityScale`, i.e. the *combined*
     multiplier the runtime computes as `brightness` (head) and `beamIntensity ×
     BeamIntensityScale` (beam) — see below on how the two consumers differ.
   - **Texel 1:** `(zoom, focus, 0, 0)` — beam-only. `zoom` and `focus` stored raw
     (both are small positives: zoom is tan(half-angle), focus is 0-1). Round shader
     reads `zoom` only; rect derives `zoomZ = zoom`. Beamless fixtures ignore texel 1.
   - Descriptor fields written onto `DiamondManagerDefinition` (build-stripped) and
     mirrored to the runtime `DiamondManager` as plain serialized fields the shader-seed
     reads: `LightshowTex` (Texture2D), `FrameCount`, `FixtureCount`, `TexelsPerFixture`
     (=2 now), `IntensityScale` (float), `TexColumns`/`FramesPerColumn` (column-wrap, if
     the 5568-frame width needs wrapping under the 16384 texel cap — see note).
   - **Column-wrap:** width = `FrameCount × TexelsPerFixture`. At 5568 frames × 2 =
     11136 texels wide — UNDER the 16384 RGBA32 max width, so **no wrap needed at current
     scale**; a single row-per-fixture layout works. Keep the wrap math as a guarded path
     for future longer shows but the common case is flat (rows=fixtures, cols=frames×2).
   **RESOLVED — head and beam share the driven colour; only the beam has a separate
   scalar.** From `ApplyFixture`: head `_EmissionColor = colour × brightness`; beam
   `_Color = colour × brightness` (identical) and `_BeamIntensity = beamIntensity ×
   BeamIntensityScale`. So the per-fixture baked data is just:
   - `drivenColour = colour × brightness` (HDR RGB, shared by head + beam) → texel 0 RGB,
     scaled by a per-bake `ColourScale` (peak of drivenColour across the show).
   - `beamIntensity` (raw per-fixture beam scalar) → texel 1 B, scaled by a per-bake
     `BeamIntensityScaleBake` (peak of beamIntensity across the show).
   `BeamIntensityScale` (manager-wide) stays a **global uniform**, NOT baked — matching
   the plan's rule that manager-wide values stay global. Final beam intensity in-shader =
   `beamIntensity01 × BeamIntensityScaleBake × _BeamIntensityScale(global)`. This keeps
   head and beam correct with one shared colour and one beam-only scalar, no conflation.
7. **Shared frame-array coordination (`_LightshowFrames[]`).** One global array, one slot
   per manager (`ShowIndex`). Decide:
   - **Slot assignment:** explicit inspector `ShowIndex` per manager, or auto-assigned at
     Start (e.g. a registration counter). Explicit is simpler and deterministic; auto
     avoids manual collisions. Must be unique per concurrent manager.
   - **Array sizing:** `SetGlobalFloatArray` sets the whole array — a manager can't write
     just its slot into the GPU global without holding the full array. So either (a) a
     single coordinator writes the whole array each frame (managers report their frame to
     it), or (b) each manager keeps a full-length `_frames[]`, writes its slot, and calls
     `SetGlobalFloatArray` (last writer each frame wins the array contents — WRONG unless
     all managers share one `_frames[]` instance). **(a) is cleaner** — a tiny shared
     registry the managers push their frame into, one `SetGlobalFloatArray` per frame
     total. Resolve this before Phase 4.
   - **Single-manager fast path:** with exactly one show, `ShowIndex = 0` and a length-1
     array (or even a plain `SetGlobalFloat` fallback) — keep the common case trivial.
   - **Whether concurrent multi-part shows are even a requirement yet** (vs sequential
     parts sharing one clock). If sequential-only, this collapses to one slot and the
     coordination question mostly disappears — worth confirming before building the
     registry.

## Verification

- **Bake correctness:** after Phase 1, scrub a few known frames; compare texture-sampled
  values against the live proxy values at the same clip time (read both in an editor
  test, assert equality within 8-bit tolerance).
- **Visual A/B:** run the show with the old proxy-driven path vs the new texture path
  and confirm they look identical (colour, intensity, zoom, focus, on/off timing).
  Moving heads should be unchanged (Animator still drives them).
- **Perf:** profile `UdonBehaviour.ManagedUpdate` — expect the per-fixture GC.Alloc
  baseline (~550 boxes/frame) to drop to near zero, and ManagedUpdate to fall to O(1).
  Confirm the intermittent frame-time hitching is gone. Test in-game (not just
  ClientSim), as before.
- **Sync:** confirm the show tracks the Animator `_Time` param across play/pause/seek.
  Since Heartache drives that param (unchanged), cross-player sync is inherited from
  Heartache's existing owner-authoritative time — Diamond adds no networking of its own.
- **Standalone:** confirm Diamond plays without Heartache — drive the Animator `_Time`
  param from a plain AnimationClip lerp (or scrub the inspector `Time` fallback) and
  verify the show runs, proving no hard Heartache dependency.
