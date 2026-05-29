# Local patches to VPM-managed packages

This file tracks one-off patches applied to packages installed by VPM. Because VPM owns the package folders under `CreaturesInHeaven/Packages/`, any patch applied here will be **overwritten the next time VPM resolves or updates the package**. When that happens, reapply by following the instructions below.

Patched changes inside the package source carry a `// Patch (PR #XX):` comment so they're easy to spot when reading the package code.

---

## VRC Light Volumes — atlas depth-minimising packing strategy

**Package**: `red.sim.lightvolumes` (currently 2.1.3, released 17 Nov 2025)
**Upstream PR**: https://github.com/REDSIM/VRCLightVolumes/pull/84 (merged 15 May 2026, not yet in a tagged release as of writing).

### How to apply

Canonical patched copies of the affected files live under `patches/red.sim.lightvolumes/` at the repo root, mirroring the package's folder structure. To (re)apply:

1. Confirm the installed package version still lacks the fix. If it's 2.1.4 or later, check the upstream commit history; the fix may already be in. If so, delete the `patches/red.sim.lightvolumes/` folder and this section from PATCHES.md, and skip the rest of these steps.

2. Otherwise, copy everything from `patches/red.sim.lightvolumes/` over `CreaturesInHeaven/Packages/red.sim.lightvolumes/`, preserving the folder layout. Overwrite when prompted. Each patched section in the copied files is marked with a `// Patch (PR #84):` comment so it's easy to find when reading the code later.

3. Trigger a rebake on a scene that uses the Moment ALV. The generated `LightVolumeAtlas.asset`'s depth should drop significantly (in our test scene: from 694 down to ~128).

If the upstream package gets a non-trivial update before the fix lands officially, the canonical copies in `patches/` may need to be refreshed against the new version before reapplying — a `diff` between `patches/red.sim.lightvolumes/` and the freshly-installed `Packages/red.sim.lightvolumes/` will show what moved.

### Background & rationale

The 2.1.3 atlas packer (post-hoc referred to as the `MinimumVRAM` packing strategy) picks block placements by minimumising VRAM usage, which naturally extends the atlas along the Z axis (depth) because adding a Z step costs the least voxels. This produces atlases like this scene's 64×12×694.

However, when rendering into a 3D texture from a CRT, Unity slices it along the z-axis. Our Moment Animated Light Volume registers exactly such a CRT, so a 694-deep atlas means 694 drawcalls every time the CRT updates. With the existing packing strategy, adding more static light volumes would continue to increase the depth of the atlas even if said volumes are unrelated to the animated light volume.

[PR #84 by PiMaker](https://github.com/REDSIM/VRCLightVolumes/pull/84) adds an alternative `MinimumDepth` packing strategy that prefers extending atlases along X and Y instead of Z when a CRT post-processor is present. For this scene this brings the slice count down by roughly 80% with negligible VRAM cost.

### Patch changes

We're applying only the minimal packing-strategy slice of PR #84, not the full PR. The full PR also restructures `AtlasPostProcessors` from a `CustomRenderTexture[]` on `LightVolumeManager` into a `PostProcessor[]` struct array on `LightVolumeSetup`. It's a larger refactor that would leave dangling references, so we're skipping it here.

Files touched:

- `CreaturesInHeaven/Packages/red.sim.lightvolumes/Scripts/Texture3DAtlasGenerator.cs`
  - Adds `TexturePackingStrategy` enum (`MinimumVRAM`, `MinimumDepth`).
  - Adds optional `packingStrategy` parameter to `CreateAtlas` (defaults to `MinimumVRAM` for backwards compatibility).
  - Adds a `bestD` tracker and a `switch` over the strategy inside the placement-search loop.

- `CreaturesInHeaven/Packages/red.sim.lightvolumes/Scripts/LightVolumeSetup.cs`
  - `GenerateAtlas()` now inspects `LightVolumeManager.AtlasPostProcessors`; if any CRT is registered, it passes `MinimumDepth` to `CreateAtlas`. Note the upstream PR reads `AtlasPostProcessors` off `LightVolumeSetup` because it also moves the field. We read it off `LightVolumeManager`where 2.1.3 still keeps it.
