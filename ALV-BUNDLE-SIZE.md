# ALV AssetBundle Compression Ratio Tests

Measuring real LZ4 bundle sizes for each ALV format combination to replace the
flat `0.315` ratio from VRCLightVolumes with per-format conservative estimates.

## Background

`LightVolumeEditor.SizeInBundle()` in VRCLightVolumes uses a hardcoded `0.315`
ratio (bundle size / VRAM size). This was likely measured from real baked L1 +
RGBAHalf data. It is not per-format and significantly underestimates bundle size
for most formats, in the worst case by more than 3×.

## Test methodology

Each format variant was baked into its own AssetBundle (LZ4, active build
target) and the file size read back directly. Two data sets were tested:

**Noise**: random SH values in `[-1, 1]` from a seeded RNG. Maximum entropy;
worst case for the compressor. Ratios are guaranteed upper bounds, and real data
is expected to consistently match or beat noise compression ratios.

**Gaussian blobs**: N=8 sinusoidally-pulsing point lights combined with N=3
static shadow blockers (multiplicative Gaussian dark zones). Produces smooth
spatial gradients with lit blobs and stretches of near-zero values, which is
similar to real-world lighting patterns.

Volume sizes tested:

| Dims | Snapshots | Total voxels |
|---|---|---|
| 4×4×4 | 64 | 4,096 |
| 8×8×8 | 64 | 32,768 |
| 16×16×16 | 16 | 65,536 |
| 32×32×32 | 32 | 1,048,576 |
| 40×10×80 | 200 | 6,400,000 |

The final volume size was also used as it was a likely target dimension and length
for one of the main lighting scenarios in the Creatures in Heaven animation world
project.

## Result set 1: White noise pattern

| Format | 4³×64 | 8³×64 | 16³×16 | 32³×32 | 40×10×80×200 | Avg |
|---|---|---|---|---|---|---|
| L1 + 8bpc | 1.038 | 1.017 | 1.015 | 1.013 | 1.013 | 1.019 |
| L1 + 16bpc | 0.877 | 0.865 | 0.864 | 0.863 | 0.863 | 0.866 |
| MonoL1 + 8bpc | 0.835 | 0.776 | 0.773 | 0.768 | 0.768 | 0.784 |
| MonoL1 + 16bpc | 0.770 | 0.741 | 0.740 | 0.738 | 0.737 | 0.745 |
| MonoL0 + 8bpc | 0.720 | 0.636 | 0.632 | 0.627 | 0.627 | 0.648 |
| MonoL0 + 16bpc | 0.649 | 0.606 | 0.606 | 0.594 | 0.594 | 0.610 |

Ratios are stable once the texture is large enough to amortise bundle header
overhead (roughly above 8³ voxels). Small-volume figures are slightly elevated.

## Result set 2: Gaussian blob

| Format | 4³×64 | 8³×64 | 16³×16 | 32³×32 | 40×10×80×200 |
|---|---|---|---|---|---|
| L1 + 8bpc | 0.183 | 0.438 | 0.677 | 0.483 | 0.120 |
| L1 + 16bpc | 0.711 | 0.776 | 0.828 | 0.772 | 0.571 |
| MonoL1 + 8bpc | 0.223 | 0.455 | 0.705 | 0.495 | 0.123 |
| MonoL1 + 16bpc | 0.659 | 0.815 | 0.934 | 0.912 | 0.608 |
| MonoL0 + 8bpc | 0.242 | 0.461 | 0.775 | 0.597 | 0.111 |
| MonoL0 + 16bpc | 0.705 | 0.734 | 0.846 | 0.792 | 0.477 |

Results are now strongly size- and geometry-dependent, unlike the stable noise
ratios. The 40×10×80×200 result is the most meaningful for this project.

## Key findings

### 8bpc formats benefit dramatically from spatial structure.

L1+8bpc's compression ratio dropped from above 1.0 (noise) to approx. 0.12 for the
realistic scenario, nearly 9× better. It's believed that 8-bit quantisation maps a
range of near-zero float values to the same byte (`0x00`), creating long literal runs
that LZ4 compresses nearly perfectly. Float16 faithfully resolves smooth gradients
into distinct bit patterns, so 16bpc compresses much less (0.86 to 0.57 for L1+16bpc).

Or in simpler terms, imagine a run of values like:

| 0.008, 0.004, 0.003, 0.003, 0.001, 0.001

With a lower bit depth these values are more likely to round to a similar value, in this
case 0. Thus, once the compressor gets to it, it sees the same number repeated six times,
making it easy pickings for compression.


### The 0.315 ratio is far too optimistic

Even for L1 + 16bpc, the format it was presumably derived from, the noise worst-case is
0.863, and the Gaussian realistic estimate is 0.571. Both are well above 0.315; the
final bundle size being nearly twice as large as the estimate would lead one to believe.

While it is not unrealistic, and a number of tests have yielded compression ratios
far exceeding 0.315, the range is far too wide to confidently give 0.315 as even a
general suggestion for a compression ratio.


### MonoL1+16bpc barely compress at small sizes

This is likely a generator artefact: small volumes get tiny blockers that don't
create enough dark space, but it illustrates that smooth float16 gradients do
not reliably compress well.


## Conclusion

The wide range of values makes it difficult to justify using the flat 0.315 value from
VRCLightVolumes as a blanket bundle size ratio. At the same time, said range makes it
hard to conceive of a single value to cover all scenarios; nonetheless it is still
useful to give the user a ballpark of what to expect so they can make informed decisions
prior to starting a long bake.

The ideal solution may be to show a range rather than a single number: there is an increase
in code complexity but the overall logic is relatively simple:

 - In most cases with most formats, the expected compression ratio will likely land in
 between 0.5 and 0.9.
 - L1 + 8bpc is an exception in which it may compress to 1.0. However, that figure was
 for worst-case random noise, which should never happen especially in realistic
 bake sizes.
 - MonoL0 bakes are likely to compress more aggressively from the get-go.

Simplifying to a lookup table we get:

| Format         | Lower bound | Upper bound |
|----------------|-------------|-------------|
| L1 + 8bpc      | 0.5         | 0.9         |
| L1 + 16bpc     | 0.5         | 0.9         |
| MonoL1 + 8bpc  | 0.5         | 0.9         |
| MonoL1 + 16bpc | 0.5         | 0.9         |
| MonoL0 + 8bpc  | 0.5         | 0.7         |
| MonoL0 + 16bpc | 0.5         | 0.7         |

Where all formats will compress between 0.5 to 0.9, except for MonoL0 modes which will
compress somewhere between 0.5 to 0.7.
