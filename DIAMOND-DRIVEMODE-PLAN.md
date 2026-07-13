# Diamond DriveMode refactor — plan

Status: **IMPLEMENTED & VERIFIED** in `DiamondManager.cs` (Layout B — see "File
layout" below). Author confirmed in-editor: compiles; LiveProxy drives fixtures
(with the expected ManagedUpdate churn — that path IS the original per-fixture
proxy loop); BakedTexture runs the GPU path as before; editor scrub preview
unaffected. This spec now doubles as the record of what shipped and why. It made
the live-proxy vs baked-texture render path an **explicit author choice** on
`DiamondManager`, replacing the old silent auto-detect + auto-fallback.

Note: the ManagedUpdate cost under LiveProxy is by design, not a regression —
LiveProxy is the "fast enough for ~dozen lights, instant to iterate" option; its
cost is the reason BakedTexture exists. Making the choice explicit was the goal,
not making LiveProxy cheap.

Addresses open item #4 from the post-bake code review ("dual proxy-vs-texture
render paths read as a silent fallback; should be a stated intent"). Coupled
with (but does not resolve) the bake-pipeline UX rethink (item #2), which the
author is handling separately.

---

## Goal

1. The author declares how a show is driven, per manager, in the inspector:
   `LiveProxy` (U# per-fixture array update — instant to iterate, fine for
   ~dozen lights) or `BakedTexture` (GPU-sampled lookup texture — fast at scale,
   needs a re-bake step).
2. A `BakedTexture` manager whose bake is missing or stale emits an error.
   It does not silently fall back to the slow path.
3. Reorganise the file so the two paths live in physically separate, clearly
   labelled units. Seam that already exists in the code, made explicit.
   This keeps the door open for a future backends (e.g. DMX) without
   touching the other two, and make it easier to debug in future.

Non-goals (this pass): editor-preview mode selector (**now done — see Phase 2
below**), the bake-pipeline UX, multi-manager `ShowIndex` coordination.

---

## The mode enum

```csharp
public enum DiamondDriveMode { LiveProxy, BakedTexture }
public DiamondDriveMode DriveMode = DiamondDriveMode.LiveProxy;
```

- Default `LiveProxy`: a freshly-added manager with no bake just works, matching
  the "fast enough for small shows, no bake step needed" case. `BakedTexture` is
  the opt-in for scale.
- Replaces the private inferred `_textureMode` bool. `_textureMode` is deleted;
  every `if (_textureMode)` becomes `if (DriveMode == DiamondDriveMode.BakedTexture)`
  (or moves into the mode-specific `Start`/`Update` half, where the check is
  implicit).

### Stale-bake handling (decided: loud error, beams dark)

The old auto-detect *inferred* texture mode from `LightshowTex != null && count
match && frames > 0`. That inference now becomes **validation of a declared
intent**, not a mode switch:

```csharp
// In Start(), after StartShared():
if (DriveMode == DiamondDriveMode.BakedTexture)
{
    if (!ValidateBake())
    {
        Debug.LogError("[Diamond] " + name + ": DriveMode=BakedTexture but the bake " +
            "is missing or stale (no LightshowTex, or LightshowFixtureCount != fixture " +
            "count, or LightshowFrameCount <= 0). Beams will NOT light. Re-bake this " +
            "manager, or set DriveMode to LiveProxy.");
        return;   // no silent fallback: author declared baked intent
    }
    StartBakedTexture();
}
else
{
    StartLiveProxy();
}
```

`ValidateBake()` is exactly today's `_textureMode` predicate, lifted verbatim:

```csharp
private bool ValidateBake()
{
    int count = LampProps == null ? 0 : LampProps.Length;
    return LightshowTex != null
        && LightshowFixtureCount == count
        && LightshowFrameCount > 0;
}
```

Behavioural delta from today: a stale bake used to silently run the proxy path;
now it errors and beams stay dark. That's the intended change — the failure is
visible against a stated intent instead of a silent downgrade.

---

## File layout — RESOLVED: Layout B (single file, sectioned)

**Layout A (partial class across three files) is dead.** UdonSharp enforces
**class name == filename** for every UdonSharpBehaviour. A partial spread across
`DiamondDriverDiLET.cs` + `DiamondDriverLiveProxy.cs` + `DiamondManager.cs` gives
three files all declaring `DiamondManager` — only the one literally named
`DiamondManager.cs` satisfies the rule; the others error, and they can't all be
named `DiamondManager.cs` in one folder. This is a hard U# limitation, confirmed
in-editor (the driver stubs threw the class/filename mismatch error). C# accepts
partials fine; U# does not. The two empty driver stub files were deleted.

Calling into *separate* driver UdonSharpBehaviours was also rejected: Udon has no
interface/virtual dispatch, and cross-behaviour calls go through SendCustomEvent /
per-call VM dispatch — which reintroduces exactly the per-fixture dispatch cost the
whole DiamondManager rewrite killed. Wrong tool for the hot path.

**Shipped: Layout B.** One `DiamondManager.cs`, hard-separated into three regions
by banner comments, same method names as planned:

```
DiamondManager.cs
  --- Shared ---
    fields + DiamondDriveMode enum + DriveMode field
    Start()               dispatch: StartShared -> (validate) -> StartBakedTexture / StartLiveProxy
    ValidateBake()
    StartShared()         IDs, _black, blocks, cached lamp objects, dirty-check arrays,
                          static per-fixture emitter + static atmosphere seeding
    ResolveAtmosphere()   (unchanged; shared by both paths + editor preview)
    Update()              dispatch: DriveMode -> UpdateBakedTexture (if _bakeValid) / UpdateLiveProxy
  === BakedTexture (DiLET) region ===
    StartBakedTexture()   bake globals, _frames alloc, time source, per-fixture row/show-slot
                          seeding (own loop, re-applies the shared blocks), keyword enable
    UpdateBakedTexture()  O(1): publish frame index + PushAnimatedAtmosphereGlobal
    PushAnimatedAtmosphereGlobal()
    EnableLightshowKeyword() / SetLightshowKeywordOn()
  === LiveProxy region ===
    StartLiveProxy()      empty hook (StartShared did all its setup); kept for symmetry
    UpdateLiveProxy()     the per-fixture proxy-read loop (was Update's body)
    ApplyAnimatedManagerChannels() / IsLightOff() / ApplyFixture()
```

No new files, no Udon risk. The logical seam + explicit `DriveMode` dispatch —
the actual point of #4 — are fully delivered; only the physical file split was
lost, and that split was never achievable under U# anyway.

### Notable structural deltas from the original plan

- `_textureMode` (inferred bool) → deleted. Replaced by `DriveMode` (author enum)
  + `_bakeValid` (a *validation-passed* guard, not a mode selector). A BakedTexture
  manager with a failed bake leaves `_bakeValid` false and Update does nothing.
- The per-fixture `_FixtureRow`/`_ShowIndex` seeding moved OUT of the shared Start
  loop into its own loop in `StartBakedTexture` (re-applying the same persistent
  blocks StartShared built). One extra O(N) loop at Start only — negligible.
- `StartLiveProxy` is an intentionally-empty hook: all proxy-path setup already
  rides in StartShared's loop. Kept so the two paths read symmetrically and a
  future backend has an obvious init seam.

---

## Method-by-method move map

Nothing here is rewritten; it's lifted as-is into named halves. Line numbers
are from the current `DiamondManager.cs` (791 lines).

| Current code | Goes to | Notes |
|---|---|---|
| Fields (all, lines 36–286) | `DiamondManager.cs` (shared) | Add `DriveMode` enum + field. Delete `_textureMode`. |
| `Start()` top half: IDs, `_black`, array alloc, per-fixture static seeding of emitter/atmosphere/`_lampObjects` (290–439 minus the `_textureMode` bits) | `StartShared()` | The `if (_textureMode)` per-fixture seeding of `_FixtureRow`/`_ShowIndex` (413–438) moves OUT into `StartBakedTexture` — see below. |
| `Start()` texture branch: globals seed, `_frames` alloc, time-source resolve (333–356), keyword enable (444–445), per-fixture `_FixtureRow`/`_ShowIndex` seed (413–438) | `StartBakedTexture()` | Runs only after `ValidateBake()` passes. |
| `Update()` texture block (541–557) | `UpdateBakedTexture()` | |
| `Update()` proxy loop (559–653) | `UpdateLiveProxy()` | `ApplyAnimatedManagerChannels` call stays at its head. |
| `PushAnimatedAtmosphereGlobal` (478–495) | baked half | |
| `EnableLightshowKeyword` / `SetLightshowKeywordOn` (506–526) | baked half | |
| `ApplyAnimatedManagerChannels` (674–722) | proxy half | |
| `IsLightOff` (730–736) | proxy half | |
| `ApplyFixture` (742–805) | proxy half | |
| `ResolveAtmosphere` (463–472) | shared | Called by both halves + editor preview. Unchanged. |

### `Start()` skeleton after the split

```csharp
public void Start()
{
    StartShared();                                  // IDs, _black, arrays, static seeding

    if (DriveMode == DiamondDriveMode.BakedTexture)
    {
        if (!ValidateBake()) { Debug.LogError(/* … */); return; }
        StartBakedTexture();
    }
    else
    {
        StartLiveProxy();                           // may be empty if proxy needs no extra init
    }
}
```

Note: the proxy path currently does all its per-fixture setup inside the shared
loop (property blocks, `_lampObjects`, dirty-check arrays are all allocated in
`StartShared`'s loop regardless of mode). `StartLiveProxy()` may end up empty or
near-empty — that's fine; keep it as an explicit hook so the symmetry is legible
and a future backend has an obvious place to init. Alternatively, the
dirty-check cache arrays (`_lastBrightness` etc.) are ONLY read by the proxy
path — moving their allocation into `StartLiveProxy` would save that memory in
baked mode. Minor; author's call whether to bother. Flagged, not assumed.

### `Update()` skeleton after the split

```csharp
public void Update()
{
    if (LampProps == null) return;
    if (DriveMode == DiamondDriveMode.BakedTexture) UpdateBakedTexture();
    else                                            UpdateLiveProxy();
}
```

---

## Optional: baker auto-sets DriveMode

`DiamondLightshowBaker.WriteDescriptor` (baker line 394) already writes the bake
fields to the manager via `SerializedObject`. It *could* also set
`DriveMode = BakedTexture` in the same block, so that baking auto-declares
intent — the author bakes, and the manager flips to the baked path without a
second manual step.

**Trade-off:** convenient, but it means baking silently changes a mode the
author set. Could surprise someone who baked to *inspect* the texture but wanted
to keep iterating on the proxy path. Recommendation: **do NOT auto-set** in this
pass — keep `DriveMode` purely author-controlled, and let the bake-pipeline UX
rethink (item #2) decide whether bake-sets-mode belongs in a redesigned bake
button flow. Flagged here so it's on record. (If we do add it later: enum-backed
serialized fields set via `SerializedObject` use `.enumValueIndex`, not
`.intValue` — noted so the baker's `SetInt` helper isn't misused for it.)

---

## Verification checklist (author, in editor)

After I write the code and you create/move the files:

1. **Compiles.** U# accepts the layout (partial or sectioned) with no errors.
2. **LiveProxy still works.** A manager set to `LiveProxy` with no bake drives
   fixtures exactly as before (scrub the clip, beams + lamps animate).
3. **BakedTexture still works.** A baked manager set to `BakedTexture` runs the
   GPU path, sub-1ms Update, as today.
4. **Stale bake errors loudly.** Set a baked manager to `BakedTexture`, then
   invalidate the bake (clear `LightshowTex` or change fixture count) → console
   shows the `[Diamond]` error and beams stay dark (no silent proxy fallback).
5. **Editor preview unaffected.** `DiamondFixtureMapPreview` still lights up on
   scrub in edit mode (it forces the keyword off and drives the proxy preview
   regardless of `DriveMode` — unchanged this pass).

---

## What I need from you (editor-side)

- **First:** the `partial class` compile test (trivial 2-file U# behaviour) to
  pick Layout A vs B.
- **If Layout A:** create two empty U# scripts via the Create menu —
  `DiamondManager.BakedTexture.cs`, `DiamondManager.LiveProxy.cs` — so their
  `.meta`/GUIDs exist. I fill them.
- **If Layout B:** nothing to create; I edit `DiamondManager.cs` in place.
- Either way: add the `DriveMode` field's value in the inspector on existing
  managers after the recompile (existing baked managers → set `BakedTexture`;
  they'll default to `LiveProxy` on the enum's first serialization otherwise).

---

# Phase 2 — editor-preview mode selector

Status: **IMPLEMENTED, pending in-editor verify.** Was the deferred "runtime
first" non-goal above; now built. Adds a second, editor-only enum so the author
picks what the scene-view preview renders **independently** of the runtime
`DriveMode` — most usefully, to preview a manager's *baked* output off-play and
verify the bake matches the live proxies (bake-accuracy check).

## The preview enum

```csharp
public enum DiamondPreviewMode { LiveProxy, BakedTexture }
public DiamondPreviewMode PreviewMode = DiamondPreviewMode.LiveProxy;   // on DiamondManager
```

- Lives on `DiamondManager`, next to `DriveMode` (author choice was: enum on the
  manager, not a global previewer toggle — per-manager, serialized, inspector-
  visible, mirrors `DriveMode`'s home). Editor-only: read solely by
  `DiamondFixtureMapPreview`; the runtime behaviour never touches it.
- **No `Auto` mode** (author's call): `PreviewMode` does NOT follow `DriveMode`.
  The author states outright what the scene view renders. That full decoupling is
  the feature — preview `BakedTexture` while shipping `LiveProxy` (or vice versa)
  to verify a bake. Default `LiveProxy`: it always works (no bake needed) and
  matches the old always-proxy editor preview, so existing managers are unchanged.
- Top-level enum for the same U# reason as `DiamondDriveMode` (no nested types),
  though this one is only ever read by editor C# — the enum type still has to be
  visible to both the behaviour file and the editor assembly.

## What the baked preview does (mirrors the runtime baked path in editor C#)

Udon doesn't run in edit mode, so nothing seeds the bake globals off-play — the
old previewer always forced the keyword OFF and drove the proxy path. The baked
preview re-implements `StartBakedTexture` + `UpdateBakedTexture` in editor C#:

- **Per manager, once per tick** (`PushBakeGlobals`): binds `_UdonDiamondLightshowTex`
  + the packing globals (`…TexelsPerFixture / …ColourScale / …BeamScale / …FrameCount`),
  the `_UdonDiamondBeamIntensityScale` master, and writes this manager's frame
  column into its `ShowIndex` slot of a shared `_frames[DIAMOND_MAX_SHOWS]` (=16)
  array, from the manager's serialized `Time`. Same globals, same names, same
  frame math as the runtime.
- **Per fixture** (`PreviewBaked`): enables `DIAMOND_LIGHTSHOW_TEX` (opposite of
  the proxy preview), writes `_FixtureRow` (the fixture's index in the manager's
  `LampProps`) + `_ShowIndex` + emitter dims into the per-fixture block, and
  seeds static atmosphere per-block. The shader then samples the bake exactly as
  in play.
- **Fixture → row** (`FixtureRow`): the definition doesn't store its bake index,
  so it's found by matching `def.LampProps` against `manager.LampProps` (linear
  scan, edit-time scale). Not found → fixture left on defaults.

## Stale-bake behaviour differs from runtime (deliberately)

Runtime `BakedTexture` + bad bake = loud error, beams dark (Phase 1). The
**preview** instead falls back to the proxy preview and logs a one-shot
`LogWarning` per manager (latched in `_staleBakeLogged`, pruned when the bake
goes valid again). Rationale: the editor is a WYSIWYG surface, not the ship
target — a visible fallback while you scrub beats a black stage. `BakeIsValid`
re-uses the exact `ValidateBake` predicate.

## Files touched

- `DiamondManager.cs` — added `DiamondPreviewMode` enum + `PreviewMode` field
  (+ doc comments). No runtime-path change; the field is inert at runtime.
- `Editor/DiamondFixtureMapPreview.cs` — restructured: `OnEditorUpdate` now
  resolves each fixture's manager, routes to `PreviewProxy` (the old body,
  extracted verbatim) or `PreviewBaked` (new) by `manager.PreviewMode`. Manager
  cache (`_managerCache`, formerly `_atmoCache`) now also carries the resolved
  preview mode + a globals-pushed latch. New helpers: `PreviewBaked`,
  `PushBakeGlobals`, `FixtureRow`, `BakeIsValid`.

## Verification checklist (author, in editor)

1. **Compiles**, no new errors. (`DiamondFixtureMapPreview` is editor-only C#,
   not U# — normal C# rules; the only diagnostic is a cosmetic "use switch
   expression" hint, left as-is.)
2. **`PreviewMode = LiveProxy`** → scene view previews as before (proxy scrub).
3. **`PreviewMode = BakedTexture` (valid bake)** → scene view shows the baked
   sample when scrubbing the manager's `Time` slider (this is new — before,
   editor always previewed proxy).
4. **Force `PreviewMode = BakedTexture` on a `LiveProxy`-shipping manager** →
   preview shows the bake; toggle back to `LiveProxy` → proxy. Compare the two
   visually to confirm the bake matches (the whole point).
5. **Bake-accuracy sanity:** with a valid bake, `LiveProxy` vs `BakedTexture`
   preview should look the same at a given `Time`. A visible mismatch = a bake
   bug this preview now surfaces.
6. **Stale bake:** force `BakedTexture`, clear `LightshowTex` (or break fixture
   count) → console shows the one-shot `[Diamond]` fallback warning and preview
   drops to proxy (not dark). Re-baking clears the latch.
7. **Multiple managers:** two managers with distinct `ShowIndex` previewing baked
   at once each sample their own show (shared `_frames` array, per-slot writes).

## Open / deferred (Phase 2)

- **Preview `Time` source in editor.** The baked preview reads `manager.Time`
  (the serialized slider) since the animator param isn't driven off-play. If you
  want the editor timeline / an animation-window scrub to drive it, that's a
  separate hook — flagged, not built.
- **Comment drift in `DiamondLampGlow.shader`** (line ~78: "edit-preview path
  below is used when the keyword is off"). Still functionally correct — the
  instancing buffer is declared outside the `#ifdef`, so per-block
  `_FixtureRow`/`_ShowIndex` reach the baked branch — but the comment now
  under-describes it (editor can drive the baked branch too). Cosmetic; left
  untouched this pass.
