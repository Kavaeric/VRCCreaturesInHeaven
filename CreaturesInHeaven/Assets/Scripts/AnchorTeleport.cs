
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using UdonSharp.Localization;


#if UNITY_EDITOR
using UnityEditor;
#endif

// Teleports a player from an entry zone to an exit zone, preserving their relative position and
// facing direction within the zone. Can be triggered locally or across the network.
//
// Each zone is defined by a Transform (origin + rotation), a Size (extents in local space),
// an Anchor (where the origin sits within the box, [-1 to 1] per axis), a Rotation offset, and
// a Scale multiplier. The MatchExit* toggles mirror the entry settings onto the exit zone.
public class AnchorTeleport : UdonSharpBehaviour
{
    [SerializeField] private Transform entry;
    [SerializeField] private Transform exit;
    [SerializeField] private Transform exampleTransform;

    // Anchor offsets the box relative to the transform origin, per axis in [-1 to 1] normalised space.
    // (0,0,0) = origin is at box centre; (0,-1,0) = origin is at the bottom face of the box.
    [SerializeField] private Vector3 entryAnchor = Vector3.zero;
    [SerializeField] public bool MatchExitAnchor = true;
    [SerializeField] private Vector3 exitAnchor = Vector3.zero;

    // Size of the zone box in local units (before scale is applied).
    [SerializeField] private Vector3 entrySize = Vector3.one;
    public bool MatchExitSize = true;
    [SerializeField] private Vector3 exitSize = Vector3.one;

    // Additional euler rotation applied on top of the transform's rotation.
    [SerializeField] private Vector3 entryRotation = Vector3.zero;
    public bool MatchExitRotation = true;
    [SerializeField] private Vector3 exitRotation = Vector3.zero;

    // Per-axis scale multiplier applied on top of the transform's lossy scale.
    [SerializeField] private Vector3 entryScale = Vector3.one;
    public bool MatchExitScale = true;
    [SerializeField] private Vector3 exitScale = Vector3.one;

    [SerializeField] private bool showGizmos = true;

    // Guards against OnEnable firing before Start. UdonSharp calls OnEnable before Start on
    // scene load, so without this the teleport would fire immediately on world join.
    private bool afterStart;
    void Start()
    {
        // This feels extremely cursed, Torvid.
        afterStart = true;
    }

    void OnEnable()
    {
        // Lock so it doesn't do anything until you intentionally enable it.
        if (!afterStart)
            return;

        TriggerLocal();
    }

    // Sends the teleport to all clients in the instance.
    public void NetworkTrigger()
    {
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(TriggerLocal));
    }

    // Teleports the local player if they are currently inside the entry zone.
    public void TriggerLocal()
    {
        Vector3 playerPos = Networking.LocalPlayer.GetPosition();

        // Transform the player's world position into the entry zone's local space and check bounds.
        Quaternion entryRot = entry.rotation * Quaternion.Euler(entryRotation);
        Vector3 entryLocal = Quaternion.Inverse(entryRot) * (playerPos - entry.position);

        // Don't teleport if the player is not inside the entry zone.
        Vector3 scaledEntrySize = Vector3.Scale(entrySize, entryScale);
        if (!IsInsideBox(entryLocal, scaledEntrySize, entryAnchor))
        {
            BoxCenterAndHalf(scaledEntrySize, entryAnchor, out Vector3 dbgCenter, out Vector3 dbgHalf);
            Debug.Log("AnchorTeleport: Player not teleported.");
            Debug.Log($"    Player position: {entryLocal:F4}");
            Debug.Log($"    Bounding box: {(dbgCenter - dbgHalf).ToString("F4")} to {(dbgCenter + dbgHalf).ToString("F4")}");
            return;
        }

        Networking.LocalPlayer.TeleportTo(
            RemapPosition(playerPos),
            Quaternion.LookRotation(RemapForward(Networking.LocalPlayer.GetRotation() * Vector3.forward))
        );
    }

    // Maps a world position from the entry zone's local space to the exit zone's local space.
    // The player's position within the entry box is preserved proportionally in the exit box,
    // then clamped so they always land inside the exit zone even if the zones differ in size.
    private Vector3 RemapPosition(Vector3 worldPos)
    {
        Quaternion entryRot = entry.rotation * Quaternion.Euler(entryRotation);
        Vector3 entryScaleWorld = Vector3.Scale(entry.lossyScale, entryScale);
        Quaternion exitRot = exit.rotation * Quaternion.Euler(MatchExitRotation ? entryRotation : exitRotation);
        Vector3 exitScaleWorld = Vector3.Scale(exit.lossyScale, MatchExitScale ? entryScale : exitScale);
        Vector3 resolvedExitAnchor = MatchExitAnchor ? entryAnchor : exitAnchor;
        Vector3 resolvedExitSize = Vector3.Scale(MatchExitSize ? entrySize : exitSize, MatchExitScale ? entryScale : exitScale);

        // Convert to entry-local space, divide out entry scale to get a normalised 0-1 position,
        // then multiply by exit scale to land in exit-local space.
        Vector3 entryLocal = Quaternion.Inverse(entryRot) * (worldPos - entry.position);
        Vector3 normalizedPos = new Vector3(entryLocal.x / entryScaleWorld.x, entryLocal.y / entryScaleWorld.y, entryLocal.z / entryScaleWorld.z);
        Vector3 exitLocal = Vector3.Scale(normalizedPos, exitScaleWorld);

        // exitLocal is already in exit-rotation-local space, so clamp directly without a round-trip through world space.
        BoxCenterAndHalf(resolvedExitSize, resolvedExitAnchor, out Vector3 exitCenter, out Vector3 exitHalf);
        Vector3 clamped = new Vector3(
            Mathf.Clamp(exitLocal.x - exitCenter.x, -exitHalf.x, exitHalf.x),
            Mathf.Clamp(exitLocal.y - exitCenter.y, -exitHalf.y, exitHalf.y),
            Mathf.Clamp(exitLocal.z - exitCenter.z, -exitHalf.z, exitHalf.z)
        );
        return exit.position + exitRot * (clamped + exitCenter);
    }

    // Rotates a world-space forward vector from entry-zone orientation into exit-zone orientation,
    // so the player's facing direction is preserved relative to the zone they're entering.
    private Vector3 RemapForward(Vector3 worldForward)
    {
        Quaternion entryRot = entry.rotation * Quaternion.Euler(entryRotation);
        Quaternion exitRot = exit.rotation * Quaternion.Euler(MatchExitRotation ? entryRotation : exitRotation);
        return exitRot * (Quaternion.Inverse(entryRot) * worldForward);
    }

    // Given a scaled box size and an anchor (-1 to 1 per axis), returns the box center offset and
    // half-extents.
    // e.g. Anchor (0, 0, 0) centres the box at the object's origin.
    //      Anchor (0, -1, 0) aligns the bottom face of the box with the origin.
    private static void BoxCenterAndHalf(Vector3 scaledSize, Vector3 anchor, out Vector3 center, out Vector3 half)
    {
        center = -Vector3.Scale(anchor, scaledSize) * 0.5f;
        half = new Vector3(Mathf.Abs(scaledSize.x), Mathf.Abs(scaledSize.y), Mathf.Abs(scaledSize.z)) * 0.5f;
    }

    // Returns true if rotLocal (a point already in the box's local space) falls within the box
    // defined by scaledSize and anchor.
    private bool IsInsideBox(Vector3 rotLocal, Vector3 scaledSize, Vector3 anchor)
    {
        BoxCenterAndHalf(scaledSize, anchor, out Vector3 center, out Vector3 half);
        Vector3 rel = rotLocal - center;
        return Mathf.Abs(rel.x) <= half.x && Mathf.Abs(rel.y) <= half.y && Mathf.Abs(rel.z) <= half.z;
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR

    void DrawArrow(Vector3 position, Vector3 forward, float length = 1.0f)
    {
        Gizmos.DrawSphere(position, 0.1f);

        forward = forward.normalized * length;

        Vector3 arrowTip = position + forward;
        Gizmos.DrawLine(position, arrowTip);
        Vector3 rightHead = Quaternion.LookRotation(forward) * Quaternion.Euler(0, 160f, 0) * Vector3.forward;
        Vector3 leftHead = Quaternion.LookRotation(forward) * Quaternion.Euler(0, 200f, 0) * Vector3.forward;
        Gizmos.DrawLine(arrowTip, arrowTip + rightHead * 0.12f * length);
        Gizmos.DrawLine(arrowTip, arrowTip + leftHead * 0.12f * length);
    }

    private void OnDrawGizmos()
    {
        if (!showGizmos)
            return;

        Quaternion entryRot = entry.rotation * Quaternion.Euler(entryRotation);
        Quaternion exitRot = exit.rotation * Quaternion.Euler(MatchExitRotation ? entryRotation : exitRotation);
        Vector3 resolvedExitAnchor = MatchExitAnchor ? entryAnchor : exitAnchor;
        Vector3 scaledEntrySize = Vector3.Scale(entrySize, entryScale);
        Vector3 scaledExitSize = Vector3.Scale(MatchExitSize ? entrySize : exitSize, MatchExitScale ? entryScale : exitScale);

        BoxCenterAndHalf(scaledEntrySize, entryAnchor, out Vector3 entryBoxCenter, out Vector3 entryBoxHalf);
        BoxCenterAndHalf(scaledExitSize, resolvedExitAnchor, out Vector3 exitBoxCenter, out Vector3 exitBoxHalf);
        Vector3 entryBoxSize = entryBoxHalf * 2f;
        Vector3 exitBoxSize = exitBoxHalf * 2f;

        // Entry zone
        Gizmos.color = Color.cyan;
        if (exampleTransform != null)
            DrawArrow(exampleTransform.position, exampleTransform.forward);
        DrawArrow(entry.position, entryRot * Vector3.forward, Mathf.Abs(scaledEntrySize.z) * 0.5f);
        Gizmos.matrix = Matrix4x4.TRS(entry.position, entryRot, Vector3.one);
        Gizmos.DrawSphere(Vector3.zero, Mathf.Min(entryBoxSize.x, entryBoxSize.y, entryBoxSize.z) * 0.01f);
        Gizmos.DrawWireCube(entryBoxCenter, entryBoxSize);
        Gizmos.color = Color.cyan * new Color(1f, 1f, 1f, 0.1f);
        Gizmos.DrawCube(entryBoxCenter, entryBoxSize);
        Gizmos.matrix = Matrix4x4.identity;

        // Exit zone
        Gizmos.color = new Color (1f, 0.3f, 0f);
        DrawArrow(exit.position, exitRot * Vector3.forward, Mathf.Abs(scaledExitSize.z) * 0.5f);
        Gizmos.matrix = Matrix4x4.TRS(exit.position, exitRot, Vector3.one);
        Gizmos.DrawSphere(Vector3.zero, Mathf.Min(exitBoxSize.x, exitBoxSize.y, exitBoxSize.z) * 0.01f);
        Gizmos.DrawWireCube(exitBoxCenter, exitBoxSize);
        Gizmos.color = new Color (1f, 0.3f, 0f, 0.1f);
        Gizmos.DrawCube(exitBoxCenter, exitBoxSize);
        Gizmos.matrix = Matrix4x4.identity;

        // Line connecting entry and exit zone centres
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(
            entry.position + entryRot * entryBoxCenter,
            exit.position + exitRot * exitBoxCenter
        );

        // Preview where ExampleTransform would land
        if (exampleTransform != null)
        {
            Vector3 worldPos = RemapPosition(exampleTransform.position);
            Vector3 worldForward = RemapForward(exampleTransform.forward);
            Gizmos.color = new Color(1f, 0.3f, 0f);
            DrawArrow(worldPos, worldForward);
        }
    }
#endif
}

#if !COMPILER_UDONSHARP && UNITY_EDITOR
[CustomEditor(typeof(AnchorTeleport))]
public class AnchorTeleportEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Teleport zones", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entry"),
            new GUIContent("Entry", "The transform that defines the centre/rotation of the entry zone."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("exit"),
            new GUIContent("Exit", "The transform that defines the centre/rotation of the exit zone."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("exampleTransform"),
            new GUIContent("Example Transform", "A preview transform shown in the editor. Drag any object here to see where it would land in the exit zone."));

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Bounding box anchors", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entryAnchor"),
            new GUIContent("Entry anchor", "The anchor offset of the entry zone, in [-1 to 1] normalised space per axis. (0,0,0) centres the box at the origin; (0,-1,0) aligns the bottom face with the origin."));
        if (Mathf.Approximately(serializedObject.FindProperty("entryAnchor").vector3Value.y, -1f))
            EditorGUILayout.HelpBox("Entry anchor Y is -1, suggesting this teleport zone is aligned to the floor. In VRChat, the player origin can be slightly below the ground (-0.005), which may place them just outside the zone and block the teleport. It's recommended to add a little bit of padding to the y-height of the bounding box to account for this gap.", MessageType.Warning);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("MatchExitAnchor"),
            new GUIContent("Match exit anchor", "Whether to match the exit anchor to the entry anchor."));
        if (!((AnchorTeleport)target).MatchExitAnchor)
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exitAnchor"),
                new GUIContent("Exit anchor", "The anchor offset of the exit zone, in [-1 to 1] normalised space per axis."));

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Zone size", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entrySize"),
            new GUIContent("Entry size", "Size of the entry zone box in local units (before scale is applied)."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("MatchExitSize"),
            new GUIContent("Match exit size", "Whether to match the exit size to the entry size."));
        if (!((AnchorTeleport)target).MatchExitSize)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exitSize"),
                new GUIContent("Exit size", "Size of the exit zone box in local units (before scale is applied)."));
                EditorGUILayout.HelpBox("If the exit zone size is smaller than the entry zone size, the player's position will be clamped to within the bounds of the exit zone.", MessageType.Info);
        }

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Zone rotation", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entryRotation"),
            new GUIContent("Entry rotation", "Additional euler rotation applied on top of the entry transform's rotation."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("MatchExitRotation"),
            new GUIContent("Match exit rotation", "Whether to match the exit rotation offset to the entry rotation offset."));
        if (!((AnchorTeleport)target).MatchExitRotation)
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exitRotation"),
                new GUIContent("Exit rotation", "Additional euler rotation applied on top of the exit transform's rotation."));

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Zone scale", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entryScale"),
            new GUIContent("Entry scale", "Per-axis scale multiplier applied on top of the entry transform's lossy scale."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("MatchExitScale"),
            new GUIContent("Match exit scale", "Whether to match the exit scale multiplier to the entry scale multiplier."));
        if (!((AnchorTeleport)target).MatchExitScale)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exitScale"),
                new GUIContent("Exit scale", "Per-axis scale multiplier applied on top of the exit transform's lossy scale."));
            EditorGUILayout.HelpBox("Scaling the exit zone will scale the space between the anchor and the player's position (and players between each other).", MessageType.Info);
        }

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("showGizmos"),
            new GUIContent("Show gizmos", "Whether to show the gizmos in the editor."));

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
