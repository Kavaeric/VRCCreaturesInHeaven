# Diamond DriveMode refactor — plan

Status: **planned, not yet implemented.** This is the spec for making the
live-proxy vs baked-texture render path an **explicit author choice** on
`DiamondManager`, replacing today's silent auto-detect + auto-fallback.

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

Non-goals (this pass): editor-preview mode selector (deferred — "runtime
first"), the bake-pipeline UX, multi-manager `ShowIndex` coordination.

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

## File layout

**OPEN RISK — must verify in Unity first.** The clean version of this uses
`partial class DiamondManager` across three files. **UdonSharp's support for
`partial class` is uncertain** — the U# compiler has historically processed one
source file per behaviour, and partial classes have been unreliable across
versions. There is no existing `partial class` anywhere in this project to lean
on.

**Verification step (author, in editor) before committing to a layout:** create
a trivial two-file `partial class` UdonSharpBehaviour, drop it on an object, and
confirm it compiles + runs in play mode. If it works, use **Layout A**. If U#
rejects it, use **Layout B** (single file, sectioned). The logic is identical
either way — only the physical file boundary differs.

### Layout A — partial class (preferred, IF U# supports it)

```
DiamondManager.cs               // shared: all fields, enum, Start() skeleton,
                                //   Update() dispatch, ResolveAtmosphere,
                                //   ValidateBake, StartShared
DiamondManager.BakedTexture.cs  // partial: StartBakedTexture, UpdateBakedTexture,
                                //   PushAnimatedAtmosphereGlobal,
                                //   EnableLightshowKeyword, SetLightshowKeywordOn
DiamondManager.LiveProxy.cs     // partial: StartLiveProxy, UpdateLiveProxy,
                                //   ApplyAnimatedManagerChannels, IsLightOff,
                                //   ApplyFixture
```

All three declare `public partial class DiamondManager : UdonSharpBehaviour`
(the base list on every part, or per U#'s rules — verify). Fields stay in the
main file; the partials only add methods.

**Files the author creates in-editor:** `DiamondManager.BakedTexture.cs` and
`DiamondManager.LiveProxy.cs` (new U# scripts — must be created via the Unity
Editor's Create menu so the `.meta`/GUID is generated, per the project's U#
constraint). I write the contents; author creates the empty files first (or
creates + I fill via edit — either works as long as the asset exists).

### Layout B — single file, sectioned (fallback if no partial support)

Keep one `DiamondManager.cs`, but hard-separate the three regions with banner
comments and the same method names (`StartShared` / `StartBakedTexture` /
`StartLiveProxy`, `UpdateBakedTexture` / `UpdateLiveProxy`). No new files. Less
tidy on disk, but the *logical* seam and the explicit `DriveMode` dispatch —
the actual point of #4 — are fully preserved. This is a safe fallback that
loses only the physical file split.

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
