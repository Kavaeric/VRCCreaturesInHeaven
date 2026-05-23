# Diamond × Bakery Integration

Adds Bakery light support to Diamond fixtures for use with Moment ALV baking.
The design keeps Diamond self-contained: all Bakery-specific code is guarded with
`#if BAKERY_INCLUDED` and lives under `Assets/Modules/Diamond/Editor/Bakery/`.

---

## Workflow

A separate bake scene holds the light rig. Diamond fixtures live there (copied or
instanced from the main scene). Running "Add Bakery lights" on selected fixtures
sets them up; the rig is then animated and baked with the Moment baker as normal.
Bakery lights never enter the main scene.

---

## Changes to `DiamondFixtureProfile`

New fields added to the existing profile ScriptableObject:

```
BakeryLightType   BakeryLight       // Point, Spot, Mesh
float             BakeryBrightnessScale   // multiplier applied on top of PropsTransform brightness
Vector3           BakeryLightOffset       // X/Y/Z positional offset of Bakery light from the Head pivot
// Area lights only:
Vector3           BakeryMeshLightSize     // X/Y/Z extents of the added mesh for mesh light
```

**BakeryLightType enum:**
- `Point`: standard point light.
- `Spot`: point light configured as spot (for now, same as Point).
- `Mesh`: mesh (area) light using a cube primitive. May be extended to support other shapes later.

---

## `DiamondBakeryDriver`

A MonoBehaviour (not Udon) added as a sibling to `FixtureDriver` and
`FixtureDefinition` on the fixture root. Mirrors the animated state to the
Bakery light each editor update.

The script is guarded with `#if BAKERY_INCLUDED` to ensure Diamond still works
without Bakery as a dependency.

**Inspector fields:**
```
BakeryLight   Light              // the Bakery light component to drive
Transform     PropsTransform     // same PropsTransform as FixtureDriver
float         BrightnessScale    // copied from profile at setup time; editable after
```

`DiamondFixtureDefinition` is read at setup time by the action script only, to pull
the profile values into `DiamondBakeryDriver`'s own fields. `DiamondFixtureDefinition`
is never modified and carries no Bakery-specific state.

**Per-update logic:**
- If `PropsTransform.gameObject.activeSelf == false`, disable the light.
- Otherwise, set `Light.intensity = PropsTransform.localScale.x * BrightnessScale`.
- Spread (localScale.y) → not mapped to Bakery for now (no clear Bakery analogue).

The Bakery light GameObject is parented under `Head`, so pan/tilt are inherited
for free from any animated movement of the fixture head object.

---

## "Add Bakery lights to selected" action

Menu item: `Tools/Diamond/Add Bakery lights to selected`

Operates on all selected GameObjects that have a `DiamondFixtureDefinition`.
For each fixture:

1. Read the fixture's `DiamondFixtureProfile` for light type, size, and offset.
2. Create a child GameObject under `Head` named `"Bakery Light"`.
3. Based on `BakeryLightType`:
   - **Point / Spot**: add an empty GameObject offset by `BakeryLightOffset` and 
     with a `BakeryPointLight` component.
   - **Mesh**: add a cube `MeshFilter`/`MeshRenderer` (using Unity's built-in cube),
     scaled to `BakeryMeshLightSize`, offset by `BakeryLightOffset`, plus a `BakeryLightMesh`
     component.
4. Add `DiamondBakeryDriver` as a sibling to `FixtureDefinition` on the fixture root.
5. Wire `DiamondBakeryDriver.Light`, `PropsTransform`, and `BrightnessScale` from
   the profile.
6. Register the whole operation with `Undo`.

Fixtures that already have a `DiamondBakeryDriver` are skipped with a warning.

## "Remove Bakery lights from selected" action

Menu item: `Remove Bakery lights from selected`

Operates on all selected GameObjects that have a `DiamondFixtureDefinition`.

Strips all the selected fixtures of their Bakery components and GameObjects, returning
them to a clean slate.
