# Diamond fixture manager: restructuring plan

**Status:** accepted and in progress. Stage 1 (manager runtime + driver replacement) is
complete and profiler-verified. Stage 2 (editor tooling) and Stage 3 (FixtureMap / stable
identity) remain. `DiamondFixtureDriver` is disabled at runtime but not yet deleted — it's
still read by edit-time tooling (see the retirement note below).

## Background

Profiling the lobby and sequence 1 (VRChat debug menu, then the in-editor Unity Profiler)
showed the world is significantly **main-thread bound**, not GPU bound — the beam shader
rewrite left the render thread healthy (~2.6 ms) while the main thread sat worst-in-class
at ~17 ms despite the fewest draw calls of any world compared (see BENCH.csv).

Primary culprit was `GameObject.Deactivate` being called on every `DiamondFixtureDriver.Update()`:  `SetActive(false)` was called every frame on every dark beam, and at ~600 light fixtures this
dominated the main thread.

This was fixed with a per-frame dirty-check and an `activeSelf` guard on the toggle, which reduced
main thread average frame time to ~12 ms.

Main thread frame time continues to be problematic, however, as even with the guard,
`UdonBehaviour.ManagedUpdate()` dispatches continue to eat up about ~8ms per frame for the
number of fixtures. While the dirty-check made each call's *body* cheap, Udon still pays to
*enter* hundreds of behaviours every frame.

Additionally, despite the number of calls being reduced, this optimisation only works when only a
small fraction of fixtures are being driven. Rapidly changing numbers of driven fixtures results in
fluctuating main thread processing time, which show up as main thread frame time rapidly oscillating
between ~9 ms to ~16 ms. For the end user this reads as a very unstable framerate.

VR headset adaptive sync prevents this from being a significant motion sickness problem, but this
means that there is no percieved framerate improvement as 1% lows can remain identical to without the
optimisation.

The only way to address this properly is to reduce the number of per-frame Udon dispatches.

The fix is a central manager: one UdonBehaviour with one `Update()` that drives
all fixtures. Beyond performance, a manager root turns out to be the natural home for several
existing and future functionality that currently sit awkwardly or have no clear home, such as
manager-wide atmosphere (haze) control, FixtureMap data for editor windows, multi-manager organisation,
presets, and future DMX/MIDI control.

## Runtime model

The model is **data-oriented**: one manager behaviour owns the per-fixture object graph as
parallel arrays and does every per-frame work in a single `Update()` loop. In ECS terms:

- **Manager = the World + System.** Owns the arrays (components), runs the single `Update()`
  loop (the system), and owns all scheduling.
- **Fixture = an entity = an array index.** Not a runtime object; just row `i` across the
  parallel arrays.
- **The parallel arrays = components.** Structure-of-arrays layout of object references —
  fixture root, head, renderers, and proxy transforms — one aligned array per channel.

The data-oriented layout also provides the ideal substrate for future speculative performance
work. Time-slicing, frame-budgeting, batching, or event-driven update models will need the manager
to see and control the whole fixture set as data rather than delegate to autonomous per-fixture
objects.

### Manager responsibilities

The manager is a **monolithic single-source-of-truth replacement for 600 per-fixture drivers**.
Its one runtime responsibility is to bridge the gap that `MaterialPropertyBlock` cannot be
animated directly without breaking instancing: it reads animated values (from proxy
transforms) and writes them into each fixture's property block.

Per-fixture data has four distinct owners, and the manager holds only the first:

1. **Object graph → manager arrays.** The references the loop and tooling touch: fixture root,
   head, head renderer, beam renderer, each proxy transform, plus the scene-identity string
   (GlobalObjectId) the FixtureMap keys on. This is FixtureMap's data, promoted from "a JSON
   file a UI reads" to "the live arrays the runtime loop iterates" — the same crawl, a broader
   consumer.
2. **Animated values → proxy transforms.** Brightness, spread, beam intensity, head rotation,
   *and emission colour* (see below). Keyed by animation clips, read in the loop each frame.
   Never stored in script.
3. **Authoring intent → `DiamondFixtureDefinition`.** Defaults, starting positions, and
   profile-derived constraints. These constraints are **editor-UI clamps only** — they bound
   the inspector slider but a clip can key past them to overdrive a fixture, so the runtime
   loop never consults them. Stripped at build; never reaches runtime.
4. **Bounds → baked at edit time.** The culling AABB is set once, not per frame, so the
   worst-case scalars that size it (`MaxSpreadTan`, `MaxBeamLength`, `MaxHaze…`, `CubeLocalScale`,
   …) are read at bake time and written straight to each `Renderer.bounds`. They are not a
   runtime array.

The only genuinely-baked per-fixture *runtime* datum is `bool[] symmetricBeam` (round vs rect:
the loop needs it to decide whether to write `_SpreadZ`). One bit per fixture.

Storing the **complete** object graph upfront (not just the loop's strict minimum of proxies +
renderers) is deliberate: it's exactly what stage 3's FixtureMap fold needs anyway, so the
arrays are built once at their eventual full width rather than churned twice — and having every
reference indexed in lockstep makes the driver work a lookup instead of a re-crawl.

### Animation input: proxy transforms

Udon prohibits animating UdonBehaviour fields directly. The only way a Unity animation clip
can feed a value into runtime script is to key some object — here, proxy `Transform`s —
and have script read it back.

So the animation input is a per-fixture set of proxy transforms, keyed by clips:
`LampProps.localPosition.y` = brightness, `BeamProps.localEulerAngles.x` = spread,
`BeamProps.localScale.y` = beam intensity, `Head.localRotation` directly. The manager is the sole
**reader** of these: its `Update()` loops the arrays, reads each fixture's proxy values, and
applies them to that fixture's `MaterialPropertyBlock`s.

**Emission colour** currently has no proxy and is not animatable; it should get its own proxy
transform channel so it becomes just another animated input read in the loop, rather than
static config the manager has to bake and hold. Wiring this is a stage-1 prerequisite.

Because the proxy transforms are the fixed, animator-driven input, they double as a
correctness oracle: the manager's output for a given clip can be validated against the proxy
values the clip produces.

### Scope of the performance win

- The single `Update()` loop means **one** `ManagedUpdate` dispatch per frame instead of one
  per fixture — this should remove the ~8 ms dispatch cost and the oscillation floor it created.
- The per-active-fixture body work (reading transforms, writing property blocks) is
  irreducible in this refactor — N lit fixtures touch N renderers in a frame. Should flattening this be desired, it will have to be done through other optimisations like time-slicing, batching, or
  event-driven models that this work will enable, though out of scope for the time being.
- GameObject count and Animator cost are unaffected: the proxy transforms remain and are
  still keyed, so the animator does the same work; only the Udon reader count drops.

## Architecture

- **`DiamondManager`** (new, runtime UdonSharpBehaviour) — the World/System. Marks a manager
  root, owns the parallel fixture arrays, runs the single `Update()` loop, holds manager-wide
  runtime atmosphere values, and is the eventual entry point for presets / DMX / MIDI.
- **`DiamondManagerDefinition`** (new, editor MonoBehaviour, stripped at build) — the
  authoring/serialisation layer. Manager metadata (`DisplayName`), fixture map data, authored manager-wide
  values, and the bake step that crawls fixtures and populates the manager's arrays at edit time. This is
  where the "entities don't have inspectors — tooling projects a view onto the data" work
  lives (see stage 3).

There is no per-fixture runtime behaviour. Each fixture's *object graph* (references to its
root, head, renderers, and proxy transforms, plus its scene identity and the `symmetricBeam`
flag) lives as a row in the manager's arrays, and all per-fixture *logic* runs in the manager's loop.
`DiamondFixtureDefinition` remains as the per-fixture edit-time authoring component, unchanged
in spirit; the manager bake reads from it (and from the material) to compute the edit-time bounds,
rather than a sibling runtime behaviour holding baked copies.

> Migration note: this replaces the current per-fixture `DiamondFixtureDriver` runtime
> behaviour, which is retired once the manager loop reaches playback parity (stage 1+2).

## Staged migration

Ordered so each stage is independently testable, and so the highest-risk assumption —
"the manager can drive a real animation clip correctly" — is proven before any tooling is
touched. Stages 1 and 2 form a single indivisible milestone (an empty manager holding arrays has
no observable pass/fail; the test *is* a fixture lighting correctly from the arrays).

~~### Stage 0 — Colour keying prerequisite~~ 

[X] **Emission colour proxy.** Emission colour is currently static and unanimatable. Give it
    its own proxy transform channel so the manager reads it in the loop like any other animated value,
    rather than baking/holding it as per-fixture data.

### Stage 1 — Manager runtime + driver replacement

Build the manager's arrays and the per-entity read/apply loop together.

[X] **Arrays (object graph).** The manager holds one aligned array per reference channel:
    `Transform[] lampProps`, `Transform[] beamProps`, `Transform[] heads`,
    `Renderer[] headRenderers`, `Renderer[] beamRenderers` (plus the fixture root and
    scene-identity string, needed by stage 3), and the one baked runtime scalar
    `bool[] symmetricBeam`. Parallel arrays (not an array of a `[Serializable]` config struct)
    because Udon can't read fields off `[Serializable]` class/struct array elements. Emission
    colour and the animated channels are **not** stored — they come from proxies.
[X] **Loop.** One `Update()` iterates `i`, reads the proxy transforms for fixture `i`, and
    applies them to the head/beam `MaterialPropertyBlock`s. Per-fixture dirty-check and
    `SetActive` guard are retained, cached in per-fixture state arrays (e.g.
    `bool[] cacheValid`, `float[] lastBrightness`, …), so an unchanged fixture skips its
    property-block writes and beam toggle.
[X] **Bounds (bake-time, not runtime).** The culling AABB is set once, so compute it at bake
    time — reading the worst-case scalars from `Definition`/material then — and write it to each
    `Renderer.bounds`. No bounds scalars live on the manager at runtime.
[X] **Populate (interim).** A minimal edit-time bake that crawls the manager root for fixtures and
    fills the arrays. Can be crude at this stage — the point is to get data in so the loop can
    be tested; the robust identity/index scheme is stage 3's concern.
[X] **Exit criterion (the whole point of this milestone):** an existing animation clip plays
    through the manager with visually correct lighting, validated against the proxy-transform
    values the clip produces. Then verify in the profiler that `ManagedUpdate` drops to ~1.

**Result (measured).** `UdonBehaviour.ManagedUpdate()` block, driving sequence 1:

- Before: **~7.3 ms across ~570 instances** (per-fixture drivers).
- After collapsing to one loop: 5.35 ms in 9 instances — dispatch gone, but the cost
  *relocated* into the manager's single `Update()`, where a per-frame `GC.Alloc` spike appeared.
- Root cause of the spike: the string-keyed `MaterialPropertyBlock.SetColor/SetFloat` overloads
  marshal their string every call. Fixed by caching IDs once via `VRCShader.PropertyToID` and
  using the `int` overloads. Final: **2.41 ms**, alloc spike gone.

Two-part lesson worth keeping: collapsing dispatch *relocated* the cost rather than removing it;
the actual reduction came from killing per-frame allocation. The architecture change was the
prerequisite that made the allocation fix reachable at all — with 570 separate behaviours there
was no single place to cache the IDs.

### Stage 2 — Editor / inspector tooling

The custom inspectors and property windows (`DiamondEWinFixtureProperties`, etc.) read and
write fixture state from the manager's arrays by index. Expected to be a small change: these
tools already read material and object properties directly off each fixture object; the added
work is resolving a selected fixture to its manager index and reading/writing there. Verify
per-fixture editing still works.

> **Driver retirement note.** `DiamondFixtureDriver`'s *runtime* role is already gone — it's
> disabled on the fixtures, which is the state the 2.41 ms result was measured in, so there is
> no remaining runtime cost to chase. But the component is **not deleted**, because five
> edit-time subsystems still read fields/helpers that live only on it:
>
> - `DiamondEInsFixtureDefinition` (inspector sliders) — reads `LampProps`/`BeamProps`/`Head`.
> - `DiamondFixtureMapPreview` (scene preview) — reads refs + `EmitterSize`/`SymmetricBeam`.
> - `DiamondEWinFixtureMap`, `DiamondEWinFixtureProperties` (map/property windows) — driver lookup.
> - `DiamondFixtureBoundsGizmo` — reads the driver's bounds scalars (`MaxSpreadTan`,
>   `MaxShear*`, `MaxHazeDensity`, `CubeLocalScale`, `SafeCubeLocalScale()`), which were
>   *inlined* into `Definition.ComputeBeamBounds` rather than exposed as fields.
> - `DiamondBakeryDriver` (`#if BAKERY_INCLUDED`) — reads `EmissionColor`/`EmitterSize`/`BeamRenderer`.
>
> Deleting the driver means repointing all of these to `DiamondFixtureDefinition` (and
> re-exposing the bounds scalars it needs). That's the substance of Stage 2 plus the Bakery
> path — deferred deliberately so the runtime win could be banked without risking the authoring
> and bake tooling.

### Stage 3 — FixtureMap centralisation + stable identity

Fold FixtureMap data onto the manager, and solve index stability: the main new sharp edge
of the SoA layout. Once fixture `i` is described by N parallel arrays, they must stay
index-aligned forever; adding/removing/reordering a fixture must move every array in
lockstep. The fixture map already hit this and stores group members by `GlobalObjectId`, not
index, because regen reassigns indices. So:

- Give each fixture a **stable identity** (GlobalObjectId / GUID) and a stable identity→index
  mapping; rebuild the arrays atomically from that on bake.
- Store the FixtureMap either as an **associated sidecar file** keyed by identity, or
  **serialised directly** into a manager script. (Open decision — see below.)

## Manager-wide atmosphere (folds into the loop, post-milestone)

Haze density, anisotropy (Henyey–Greenstein *g*), and scatter strength are properties of the
room's *air*, not the individual fixture, but today they live per-material (`_HazeDensity`,
`_ScatterStrength`, `_Anisotropy`). In the data-oriented model they become manager-level values
applied uniformly across all fixtures — physically correct and nearly free (one value shared
across all `i`). Two ways to feed them, mapping to "does this ever change *during* playback?":

- **Flat, set once at Start.** A serialized float on the manager, applied across the manager at
  startup. Cheapest; right if atmosphere is a static per-scene property.
- **Animatable (proxy on the manager).** Give the manager its *own* proxy transforms and read them in
  the loop's shared (non-per-fixture) section, applying across all fixtures that frame. Same
  pattern as per-fixture channels, one level up — and the DMX/MIDI-friendly path, since
  external control just writes the proxy.

A hazier climax is exactly the kind of thing this world would want, so the animatable path is
the more future-proof default — but starting flat and upgrading to a proxy later is trivial,
so the choice can be deferred.

- **Bounds implication.** The culling AABB is sized from worst-case haze/scatter. If the manager
  drives atmosphere at runtime, the bake-time bounds (stage 1) must be sized to the manager's
  **maximum allowed** atmosphere, or a raised haze spills past the AABB and gets culled.

## Future hooks (noted, not designed here)

The data-oriented substrate is the enabler for these; none are designed yet:

- **Time-slicing / frame-budgeting** — the manager owns the loop and a cursor, so it can process
  a range of fixtures per frame to flatten the body-work spike.
- **Batching** — SoA layout lets the manager see across fixtures to group shared work.
- **Event-driven updates** — manager-level indices (by group/state) let a change touch only the
  affected fixtures.
- **Multiple managers** — each manager is one World with one `Update`; switching = enable/disable.
- **Groups** — `DiamondFixtureGroupDefinition` (just a name today) becomes an addressable
  index-subset into the manager arrays, for selective control / presets / DMX channel mapping.
- **Presets / DMX / MIDI** — external input driving fixture state through the manager (e.g. by
  writing the proxy transforms it reads, or a future per-fixture override channel). The manager is
  the single entry point.

## Key risks

- **UdonSharp script creation.** New runtime U# scripts must be *created inside the Unity
  Editor* (UdonSharp generates the program asset). `.cs` content can be authored ahead of
  time, but the script asset itself has to be created in Unity — editing a `.cs` on disk
  alone won't wire it up.
- **Udon array ergonomics.** No generics / `List<T>` at runtime; per-element extern calls;
  cannot read fields off `[Serializable]` array elements (hence parallel arrays — which is
  what SoA wants anyway). The manager will be a wall of parallel arrays; the care goes into the
  edit-time code that populates them.
- **Index stability (stage 3).** Parallel arrays must stay index-aligned across
  add/remove/reorder. Needs a stable identity→index scheme; naive re-crawl reassigns indices
  and desyncs the arrays. This is the biggest new correctness hazard.
- **Body-work spike is not solved here.** This refactor removes dispatch cost, not the
  per-active-fixture work. The oscillation under many simultaneous fixtures only fully
  flattens once the time-slicing / event work lands on top.

## Open questions

- **Emission-colour proxy layout.** Emission colour needs a proxy channel (stage-1
  prerequisite). Which transform property carries it — a spare channel on an existing proxy
  (`LampProps` has free slots: `localScale.xyz` was reserved for RGB colour), or a dedicated
  proxy object? Bundling as `localScale` keys RGB as one Vector3; separate channels key each
  component independently.
- **Atmosphere scope: manager or group?** If two haze zones in one manager are ever wanted (e.g.
  a hazy room and a clear one, or godrays with different scatter than a stage rig in the same
  scene), atmosphere may want to live on *groups* rather than the manager root. Worth deciding
  before the atmosphere work hardens.
- **FixtureMap storage.** Sidecar file keyed by fixture identity, or serialised directly into
  a manager script? Sidecar keeps the scene light and diffs cleanly; in-script keeps everything
  in one place but bloats the manager object.

## Suggested stopping point

The Stage 1 milestone delivers the dispatch-cost win and proves the ECS model against real
animation playback. Land that and verify the profiler drop before touching tooling; stages 2,
3, atmosphere, and the future hooks are incremental on top and can be sequenced later.
