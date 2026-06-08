using UnityEngine;

// Helpers for keeping the MomentAnimatedLightVolume parallel flipbook arrays in lockstep.
//
// The runtime stores each flipbook's data as one array per field (FlipbookTextures, FlipbookSnapshotY,
// ...) rather than an array of structs, because UdonSharp cannot compile field access on elements of a
// serializable-class array. These helpers grow/shrink all the arrays together so index i is always
// valid across every field. Editor-only; the runtime never resizes the arrays.
public static class MomentFlipbookArrays
{
    // Number of flipbooks currently stored (length of the texture array, which is the canonical count).
    public static int Count(MomentAnimatedLightVolume alv) =>
        alv.FlipbookTextures != null ? alv.FlipbookTextures.Length : 0;

    // Ensures every parallel array is at least `length` long, preserving existing data and defaulting
    // new entries to single-column / MonoL1 / Depth8 (matching the runtime fallbacks).
    public static void EnsureLength(MomentAnimatedLightVolume alv, int length)
    {
        if (Count(alv) >= length && alv.FlipbookSnapshotX != null && alv.FlipbookSnapshotX.Length >= length)
            return;

        Resize(ref alv.FlipbookTextures, length);
        Resize(ref alv.FlipbookSnapshotX, length, 0);
        Resize(ref alv.FlipbookSnapshotY, length, 0);
        Resize(ref alv.FlipbookSnapshotsPerColumn, length, 1);
        Resize(ref alv.FlipbookNumColumns, length, 1);
        Resize(ref alv.FlipbookNumSnapshots, length, 0);
        Resize(ref alv.FlipbookSHMode, length, (int)MomentALVSHMode.MonoL1);
        Resize(ref alv.FlipbookBitDepth, length, (int)MomentALVBitDepth.Depth8);
    }

    // Sets the exact flipbook count, growing or trimming every parallel array to match.
    public static void SetCount(MomentAnimatedLightVolume alv, int count)
    {
        count = Mathf.Max(0, count);
        Resize(ref alv.FlipbookTextures, count);
        Resize(ref alv.FlipbookSnapshotX, count, 0);
        Resize(ref alv.FlipbookSnapshotY, count, 0);
        Resize(ref alv.FlipbookSnapshotsPerColumn, count, 1);
        Resize(ref alv.FlipbookNumColumns, count, 1);
        Resize(ref alv.FlipbookNumSnapshots, count, 0);
        Resize(ref alv.FlipbookSHMode, count, (int)MomentALVSHMode.MonoL1);
        Resize(ref alv.FlipbookBitDepth, count, (int)MomentALVBitDepth.Depth8);
    }

    static void Resize(ref Texture3D[] array, int length)
    {
        if (array == null) array = new Texture3D[0];
        if (array.Length != length) System.Array.Resize(ref array, length);
    }

    // Resizes an int array, filling any newly-added slots with `fill` (Array.Resize zero-fills, which
    // is wrong for fields whose sensible default isn't 0, e.g. snapshotsPerColumn).
    static void Resize(ref int[] array, int length, int fill)
    {
        int oldLen = array != null ? array.Length : 0;
        if (array == null) array = new int[0];
        if (array.Length != length) System.Array.Resize(ref array, length);
        for (int i = oldLen; i < length; i++) array[i] = fill;
    }
}
