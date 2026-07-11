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

- **Udon, per frame — O(1):** read `HeartacheMusicEngine.LocalAnimationTime`, set ONE
  global shader float `_LightshowFrame`. No per-fixture loop. **The per-fixture Udon
  loop is deleted entirely.**
- **Each fixture, on the GPU — free:** the beam shader and a new lamp-glow additive
  shader read their own row from the bake texture at the current frame (two-column lerp
  for smoothness), unpack their channels, apply. Per-fixture fan-out happens in the
  shader, in parallel, as part of rendering that already runs.
- **Row identity:** each fixture's row index is a per-fixture static constant, seeded
  ONCE into its head + beam property blocks in `DiamondManager.Start()` (reusing the
  existing one-time emitter-size seed pattern). Never written per frame.

**The proxy transform system is retained unchanged as the edit-time authoring +
preview layer.** The Animator keeps running at runtime anyway — it must, because it
also drives the physical **moving-head transforms** (`Head.localRotation`), which are
native Animator→transform and were never on the Udon path. So at runtime: the Animator
drives head geometry (as today), while the shaders read baked shader-values from the
texture. The bake is an offline editor step that steps the clip frame-by-frame and
reads the same proxy values Udon reads today (boxing is irrelevant offline).

## Confirmed design decisions

- **Time domain:** bake the 92.8s rig clip (5568 frames @ 60fps). At runtime map
  the song-normalised `LocalAnimationTime` into clip-normalised space exactly as the
  Animator `_Time` param does today (the song is 261s; the rig clip covers its current
  portion). Preserves existing sync.
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
  - **Asset spec (future 3D fixtures):** the lamp lens must be a **separately-
    addressable renderer** (its own submesh/material) so it can carry its own
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

~6 scalars/fixture (3 colour + brightness + zoom + focus; round derives `_ZoomZ`
from `_ZoomX`). Beam intensity and lamp brightness may collapse into one combined
multiplier at bake time (both feed the final HDR scale). Packs into ~2 RGBA32 texels
per fixture per frame. At 420 fixtures × 5568 frames × ~2 texels × 4 bytes ≈ **~19 MB**
(before column-wrap padding). Manager-wide atmosphere (`_HazeDensity` /
`_ScatterStrength` / `_Anisotropy`) stays global uniforms, NOT baked per-fixture.

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
  row. Writes a `Texture2D` asset + a small sidecar (row count, frame count, packing)
  onto `DiamondManager` or its Definition.

### Phase 2 — Beam shader reads the texture
- Modify `Beam/DiamondBeamCommon.cginc` + both shape shaders. Add a `_LightshowTex`
  (Texture2D) + global `_LightshowFrame` (float) + per-instance `_FixtureRow`.
- In the **vertex** shader (where `_Color`/`_BeamIntensity`/`_ZoomX`/`_ZoomZ`/`_Focus`
  are already read as per-instance constants and `beamLength` is derived once), replace
  those `UNITY_ACCESS_INSTANCED_PROP` reads with a texture unpack: `Load` two adjacent
  frame columns at `(_FixtureRow, floor(frame))` and `+1`, lerp by frac, unpack. Pass
  down via existing `v2f` pattern (like `beamLength`). Use the existing depth-texture
  sample (`SAMPLE_DEPTH_TEXTURE`, `#pragma target 5.0` → `Load` available) as the
  template for adding a texture.
- Static props (`_EmitterWidth/Height`, `_CubeLocalScale`) and atmosphere globals stay
  exactly as they are.

### Phase 3 — Lamp additive glow shader
- New minimal ADDITIVE shader `Diamond/Head/DiamondLampGlow.shader`: samples the same
  `_LightshowTex` row (colour×intensity), outputs it as additive emission (`Blend One
  One`, no depth write / lit surface work). ~10 lines. Add `_FixtureRow` per-instance +
  the `_LightshowTex`/`_LightshowFrame` reads (share the beam's unpack helper in a small
  common include if practical).
- On the fixture prefab, the lamp lens gets this as a **second material/pass alongside
  its Mochie material** (Mochie = off/material look, Diamond additive = baked glow).
  The lamp lens renderer is the `HeadRenderer` reference; it carries `_FixtureRow`.
- The fixture BODY is untouched — pure Mochie, no Diamond shader, no `_FixtureRow`.

### Phase 4 — Runtime manager rewrite
- `DiamondManager.Start()`: cache `_idFixtureRow`; in the existing per-fixture loop,
  seed `_FixtureRow = i` into BOTH the beam block (existing) and head block (new
  seeding), set `_LightshowTex` global once. Keep emitter-size seed.
- `DiamondManager.Update()`: replace the entire per-fixture loop with ~3 lines: read
  `LocalAnimationTime`, map to clip frame, `VRCShader.SetGlobalFloat(_idFrame, frame)`.
  Delete `ApplyFixture`, the dirty-check arrays, the beam-read path. Keep manager-wide
  atmosphere handling (still cheap, still global).
- Wire a reference to `HeartacheMusicEngine` (read `LocalAnimationTime`), or register
  as a sequence listener. Follow `HeartacheTempoMonitor` (reads `LocalAnimationTime`
  each Update).

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
  `Moment/Editor/MomentTextureWriter.cs`, `Moment/MomentAnimatedLightVolume.cs`,
  `Moment/MomentAnimatedLightVolume.cs` `MomentALVFormat` packing math
- **Music time:** `Heartache/HeartacheMusicEngine.cs` (`LocalAnimationTime`),
  `Heartache/TempoMonitor/HeartacheTempoMonitor.cs` (consumer example)

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
- **Sync:** confirm the show tracks music time across play/pause/seek, and across
  players (owner-authoritative `SyncedAnimationTime` + local extrapolation, unchanged).
