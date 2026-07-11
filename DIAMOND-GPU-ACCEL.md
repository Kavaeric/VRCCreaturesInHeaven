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

1. **Intensity HDR encoding in 8-bit.** Colour is SDR but brightness×beam-intensity is
   HDR (>1). Decide: bake a per-show constant scale (divide at bake, multiply in
   shader), or pack that one channel across two 8-bit channels for range. Pick during
   Phase 1 once the actual intensity range in the clip is known.
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
6. **Layout descriptor shape.** Nail the on-DiamondManager struct: frame count, row
   count, texels-per-fixture, slot→meaning map, column-wrap params. It must be readable
   by the runtime frame math (C#) AND expressible as the shader constants the unpack
   needs (`_TexelsPerFixture` + slot indices). One per texture, never per-fixture.
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
