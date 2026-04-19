
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Teleports all players inside the entry zone to assigned slots when triggered.
//
// When NetworkTeleport() is called, it forwards to the owner via a network event.
// The owner collects players in the zone, assigns them to slots in player-list order,
// and broadcasts TeleportPlayerToSlot() with the assignment array to all clients.
// Each client finds their own player ID in the array and teleports themselves.
//
// Thanks to Occala (https://github.com/Occala) for the architecture,
// because I don't know a damn thing about networking.
//
// The entry zone is defined by a Transform (origin + rotation), a Size (extents in local
// space), and an Anchor (where the origin sits within the box, [-1 to 1] per axis).

public enum ArrangedTeleportOnEnableMode
{
    Manual,
    OnEnableNetwork
}

public enum ArrangedTeleportRotationMode
{
    Preserve,
    AlignToSlot,
    Relative,
}

public enum ArrangedTeleportOverflowMode
{
    Ignore,
    TeleportToSlotIndex,
    Wrap,
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ArrangedTeleport : UdonSharpBehaviour
{
    [SerializeField] private ArrangedTeleportOnEnableMode onEnableMode = ArrangedTeleportOnEnableMode.Manual;

    [SerializeField] private Transform entry;

    // Anchor offsets the box relative to the transform origin, per axis in [-1 to 1] normalised space.
    // (0,0,0) = origin is at box centre; (0,-1,0) = origin is at the bottom face of the box.
    [SerializeField] private Vector3 entryAnchor = Vector3.zero;

    // Size of the zone box in local units.
    [SerializeField] private Vector3 entrySize = Vector3.one;

    [SerializeField] private ArrangedTeleportRotationMode rotationMode = ArrangedTeleportRotationMode.AlignToSlot;

    [SerializeField] private ArrangedTeleportOverflowMode overflowMode = ArrangedTeleportOverflowMode.Ignore;

    // Used by TeleportToSlotIndex mode. Supports negative indexing: -1 = last slot, -2 = second-last, etc.
    [SerializeField] private int overflowSlotIndex = -1;

    [SerializeField] private Transform[] teleportSlots;

    [SerializeField] private Transform[] exampleTransforms;

    [SerializeField] private bool showGizmos = true;

    private bool afterStart;
    void Start()
    {
        // If entry transform is not set, default to the object's transform.
        if (entry == null) entry = transform;

        // Guards against OnEnable firing before Start. UdonSharp calls OnEnable before Start on
        // scene load, so without this the teleport would fire immediately on world join.
        afterStart = true;
    }

    void OnEnable()
    {
        if (!afterStart)
            return;

        // Without the IsOwner check, the teleport will fire once for every single player in the instance.
        // This results in NetworkTeleport(), and subsequently RequestTeleport(), being called once for
        // each player; or in other words if there were 8 players, he players would get teleported 8 times each.
        //
        // This is bad.
        //
        // So we only have the owner call NetworkTeleport(). One call, one teleport for each player.
        // This isn't a problem for a manual call (like a button press) since only one player would physically
        // be able to press the button at a time.
        //
        // By the way, when you see me write a whole essay in the comments like this, it's because I've spent a
        // disproportionate amount of time trying to debug it.
        if (onEnableMode == ArrangedTeleportOnEnableMode.OnEnableNetwork && Networking.IsOwner(gameObject))
            NetworkTeleport(); 
    }

    // Forwards to the owner to determine slot assignment.
    public void NetworkTeleport()
    {
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestTeleport));
    }

    // Owner-only: collects in-zone players, assigns slots, broadcasts the assignment.
    [NetworkCallable]
    public void RequestTeleport()
    {
        VRCPlayerApi[] allPlayers = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(allPlayers);

        // First pass: count in-zone players so we can size the arrays.
        int inZone = 0;
        foreach (VRCPlayerApi player in allPlayers)
            if (Utilities.IsValid(player) && IsInsideEntryZone(player.GetPosition()))
                inZone++;

        int[] playerIds   = new int[inZone];
        int[] slotIndices = new int[inZone];
        int filled = 0;

        Debug.Log($"[ArrangedTeleport] RequestTeleport: Requesting teleport to assign {inZone} players to {teleportSlots.Length} slots.");

        // Resolved overflow slot index (supports negative indexing).
        int resolvedOverflow = ((overflowSlotIndex % teleportSlots.Length) + teleportSlots.Length) % teleportSlots.Length;

        foreach (VRCPlayerApi player in allPlayers)
        {
            if (!Utilities.IsValid(player) || !IsInsideEntryZone(player.GetPosition()))
            {
                Debug.Log($"[ArrangedTeleport] RequestTeleport: Player {player.playerId} {player.displayName} is not valid or is not inside the entry zone.");
                continue;
            }

            int slotIndex;
            if (filled < teleportSlots.Length)
            {
                Debug.Log($"[ArrangedTeleport] RequestTeleport: Slot {filled} is available. Attempting to assign player {player.playerId} {player.displayName} to slot {filled}.");
                slotIndex = filled;
            }
            else
            {
                switch (overflowMode)
                {
                    case ArrangedTeleportOverflowMode.Wrap:
                        slotIndex = filled % teleportSlots.Length;
                        break;
                    case ArrangedTeleportOverflowMode.TeleportToSlotIndex:
                        slotIndex = resolvedOverflow;
                        break;
                    default:
                        // "Ignore" mode: don't assign the player to a slot.
                        continue;
                }
            }

            playerIds[filled]   = player.playerId;
            slotIndices[filled] = slotIndex;
            filled++;
        }

        Debug.Log($"[ArrangedTeleport] RequestTeleport: Teleporting {filled} players to slots:");
        for (int i = 0; i < filled; i++)
        {
            Debug.Log($"    Player {playerIds[i]} to slot {slotIndices[i]}.");
        }

        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(TeleportPlayerToSlot), playerIds, slotIndices);
    }

    // All clients: each client finds their own player ID and teleports to their assigned slot.
    [NetworkCallable]
    public void TeleportPlayerToSlot(int[] playerIdArray, int[] slotIndexArray)
    {
        int localId = Networking.LocalPlayer.playerId;

        Debug.Log($"[ArrangedTeleport] TeleportPlayerToSlot: Finding player {localId} in array.");

        for (int i = 0; i < playerIdArray.Length; i++)
        {
            if (localId != playerIdArray[i])
            {
                Debug.Log($"    Player {localId} is not the player in array at index {i}.");
                continue;
            }

            int slot = slotIndexArray[i];
            Debug.Log($"    Teleporting player {localId} to slot {slot}.");
            Networking.LocalPlayer.TeleportTo(teleportSlots[slot].position, SlotRotation(slot));
            return;
        }

        Debug.Log($"[ArrangedTeleport] TeleportPlayerToSlot: Player {localId} not found in array.");
    }

    // Returns the rotation the local player should have after teleporting to slot i.
    private Quaternion SlotRotation(int slotIndex)
    {
        switch (rotationMode)
        {
            case ArrangedTeleportRotationMode.Preserve:
                return Networking.LocalPlayer.GetRotation();
            case ArrangedTeleportRotationMode.Relative:
                // Rotate the player's current forward into the slot's frame relative to the entry zone.
                Vector3 localForward = Quaternion.Inverse(entry.rotation) * (Networking.LocalPlayer.GetRotation() * Vector3.forward);
                return Quaternion.LookRotation(teleportSlots[slotIndex].rotation * localForward);
            default: // AlignToSlot
                return teleportSlots[slotIndex].rotation;
        }
    }

    // Returns true if worldPos falls within the entry zone box.
    private bool IsInsideEntryZone(Vector3 worldPos)
    {
        Vector3 local = Quaternion.Inverse(entry.rotation) * (worldPos - entry.position);
        BoxCenterAndHalf(entrySize, entryAnchor, out Vector3 center, out Vector3 half);
        Vector3 rel = local - center;
        return Mathf.Abs(rel.x) <= half.x && Mathf.Abs(rel.y) <= half.y && Mathf.Abs(rel.z) <= half.z;
    }

    // Given a box size and an anchor (-1 to 1 per axis), returns the box center offset and half-extents.
    // e.g. Anchor (0, 0, 0) centres the box at the object's origin.
    //      Anchor (0, -1, 0) aligns the bottom face of the box with the origin.
    private static void BoxCenterAndHalf(Vector3 size, Vector3 anchor, out Vector3 center, out Vector3 half)
    {
        center = -Vector3.Scale(anchor, size) * 0.5f;
        half = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR

    private Transform EntryTransform => entry != null ? entry : transform;

    void DrawArrow(Vector3 position, Vector3 forward, float length = 0.5f)
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

    // Draws a flat marker at the position. Used to indicate a slot position in the editor.
    void DrawSlotMarker(Vector3 position, Quaternion rotation, float size = 0.3f)
    {
        float nub = size * 0.6f;
        // Pentagon corners in local XZ space (Y=0): back-left, back-right, front-right, nub-tip, front-left
        Vector3 bl = new(-size, 0,  -size);
        Vector3 br = new( size, 0,  -size);
        Vector3 fr = new( size, 0,   size);
        Vector3 ft = new(    0, 0,  size + nub);
        Vector3 fl = new(-size, 0,   size);

        // Transform to world space
        bl = position + rotation * bl;
        br = position + rotation * br;
        fr = position + rotation * fr;
        ft = position + rotation * ft;
        fl = position + rotation * fl;

        Gizmos.DrawLineList(new Vector3[]
        {
            bl, br,
            br, fr,
            fr, ft,
            ft, fl,
            fl, bl,
        });
    }

    // Assigns a unique colour to this component based on its instance ID,
    // so multiple ArrangedTeleport gizmos in the scene are easy to distinguish.
    private Color GetColor(float alpha = 1f)
    {
        float hue = (GetInstanceID() * 0.618034f % 1f + 1f) % 1f;
        Color c = Color.HSVToRGB(hue, 0.8f, 1f);
        c.a = alpha;
        return c;
    }

    private void OnDrawGizmos()
    {
        if (!showGizmos)
            return;

        Color col = GetColor();
        Color colFaint = GetColor(0.15f);

        BoxCenterAndHalf(entrySize, entryAnchor, out Vector3 boxCenter, out Vector3 boxHalf);
        Vector3 boxSize = boxHalf * 2f;

        // Entry zone
        Gizmos.color = col;
        DrawArrow(EntryTransform.position, EntryTransform.rotation * Vector3.forward, Mathf.Abs(entrySize.z) * 0.5f);
        Gizmos.matrix = Matrix4x4.TRS(EntryTransform.position, EntryTransform.rotation, Vector3.one);
        Gizmos.DrawWireCube(boxCenter, boxSize);
        Gizmos.color = colFaint;
        Gizmos.DrawCube(boxCenter, boxSize);
        Gizmos.matrix = Matrix4x4.identity;

        if (teleportSlots == null)
            return;

        // Slots
        Gizmos.color = col;
        for (int i = 0; i < teleportSlots.Length; i++)
        {
            if (teleportSlots[i] == null)
                continue;

            bool hasExample = exampleTransforms != null && i < exampleTransforms.Length && exampleTransforms[i] != null;

            // Always draw the slot marker using the slot's own orientation.
            DrawSlotMarker(teleportSlots[i].position, teleportSlots[i].rotation);

            if (hasExample)
            {
                // Draw example transform in the entry zone.
                Transform ex = exampleTransforms[i];
                DrawArrow(ex.position, ex.forward, 0.35f);

                // Draw a preview arrow at the slot showing predicted facing direction.
                Vector3 previewForward;
                if (rotationMode == ArrangedTeleportRotationMode.Preserve)
                    previewForward = ex.forward;
                else if (rotationMode == ArrangedTeleportRotationMode.Relative)
                    previewForward = teleportSlots[i].rotation * (Quaternion.Inverse(entry.rotation) * ex.forward);
                else
                    previewForward = teleportSlots[i].forward;

                DrawArrow(teleportSlots[i].position, previewForward);
            }

            Handles.Label(teleportSlots[i].position + Vector3.up * 0.25f, $"Slot {i}");
        }
    }
#endif
}

#if !COMPILER_UDONSHARP && UNITY_EDITOR
[CustomEditor(typeof(ArrangedTeleport))]
public class ArrangedTeleportEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // --- Teleport behaviour -----------------------------------------------------
        EditorGUILayout.LabelField("Teleport behaviour", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("onEnableMode"),
            new GUIContent("Auto-teleport behaviour",
                "What to do when this object is enabled at runtime.\n\n" +
                "Manual: Nothing (call NetworkTeleport yourself).\n\n" +
                "OnEnableNetwork: On enable, teleport all players in the entry zone to their assigned slots."));

        if (serializedObject.FindProperty("onEnableMode").enumValueIndex == (int)ArrangedTeleportOnEnableMode.Manual)
            EditorGUILayout.HelpBox("This ArrangedTeleport will not do anything until NetworkTeleport() is called.", MessageType.None);
        if (serializedObject.FindProperty("onEnableMode").enumValueIndex == (int)ArrangedTeleportOnEnableMode.OnEnableNetwork)
            EditorGUILayout.HelpBox("On enable, this ArrangedTeleport will teleport all players in the entry zone.", MessageType.None);

        EditorGUILayout.Space(8f);

        // --- Entry zone -------------------------------------------------------------
        EditorGUILayout.LabelField("Entry zone", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entry"),
            new GUIContent("Entry", "The transform that defines the centre and rotation of the entry zone. If left empty, defaults to this object's transform."));
        if (serializedObject.FindProperty("entry").objectReferenceValue == null)
            EditorGUILayout.HelpBox("No entry transform assigned. This object's transform will be used as the entry zone origin.", MessageType.None);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entryAnchor"),
            new GUIContent("Anchor", "The anchor offset of the entry zone, in [-1 to 1] normalised space per axis. (0,0,0) centres the box at the origin; (0,-1,0) aligns the bottom face with the origin."));
        if (Mathf.Approximately(serializedObject.FindProperty("entryAnchor").vector3Value.y, -1f))
            EditorGUILayout.HelpBox("Anchor Y is -1, suggesting this zone is aligned to the floor. In VRChat, the player origin can be slightly below the ground (-0.005), which may place them just outside the zone. It's recommended to add a little padding to the Y height to account for this.", MessageType.Warning);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entrySize"),
            new GUIContent("Size", "Size of the entry zone box in local units."));

        EditorGUILayout.Space(8f);

        // --- Slots ------------------------------------------------------------------
        EditorGUILayout.LabelField("Slots", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("teleportSlots"),
            new GUIContent("Teleport slots",
                "One Transform per player slot. Players inside the entry zone are assigned to slots in player-list order."));

        int slotCount = serializedObject.FindProperty("teleportSlots").arraySize;
        if (slotCount == 0)
            EditorGUILayout.HelpBox("No slots assigned. Players will not be teleported.", MessageType.Warning);

        EditorGUILayout.Space(4f);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("rotationMode"),
            new GUIContent("Rotation mode",
                "How to determine the player's rotation after teleporting.\n\n" +
                "Preserve: The player keeps their current rotation.\n\n" +
                "AlignToSlot: The player is rotated to match the slot's rotation.\n\n" +
                "Relative: The player will be rotated the same amount as the slot is rotated from the ArrangedTeleport object's rotation."));

        if (serializedObject.FindProperty("rotationMode").enumValueIndex == (int)ArrangedTeleportRotationMode.Preserve)
            EditorGUILayout.HelpBox("The player's world rotation will be preserved.", MessageType.None);
        if (serializedObject.FindProperty("rotationMode").enumValueIndex == (int)ArrangedTeleportRotationMode.AlignToSlot)
            EditorGUILayout.HelpBox("The player's rotation will be aligned to the slot's rotation.", MessageType.None);
        if (serializedObject.FindProperty("rotationMode").enumValueIndex == (int)ArrangedTeleportRotationMode.Relative)
            EditorGUILayout.HelpBox("The player will be rotated the same amount as the slot is rotated from the ArrangedTeleport object's rotation.", MessageType.None);

        EditorGUILayout.Space(4f);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowMode"),
            new GUIContent("Overflow mode",
                "What happens when more players are being teleported than there are slots.\n\n" +
                "Ignore: Extra players are not teleported.\n\n" +
                "TeleportToSlotIndex: Extra players will be sent to the specified slot index.\n\n" +
                "Wrap: Extra players wrap back to the first slot and continue filling from there."));

        if (serializedObject.FindProperty("overflowMode").enumValueIndex == (int)ArrangedTeleportOverflowMode.TeleportToSlotIndex)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowSlotIndex"),
                new GUIContent("Overflow slot index",
                    "The slot index to send overflow players to, with 0 being the first slot. Supports negative indexing: -1 is the last slot, -2 the second-last, and so on."));
            EditorGUILayout.HelpBox($"Excess players will be teleported to the slot at index {serializedObject.FindProperty("overflowSlotIndex").intValue}.", MessageType.None);
        }

        if (serializedObject.FindProperty("overflowMode").enumValueIndex == (int)ArrangedTeleportOverflowMode.Ignore)
            EditorGUILayout.HelpBox("Excess players will not be teleported.", MessageType.None);
        if (serializedObject.FindProperty("overflowMode").enumValueIndex == (int)ArrangedTeleportOverflowMode.Wrap)
            EditorGUILayout.HelpBox("Excess players will wrap back to the first slot and continue filling from there.", MessageType.None);

        EditorGUILayout.Space(8f);

        // --- Debug ------------------------------------------------------------------
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("showGizmos"),
            new GUIContent("Show gizmos", "Whether to show the entry zone and slot positions in the scene view."));

        // Keep exampleTransforms length in sync with teleportSlots.
        var exProp = serializedObject.FindProperty("exampleTransforms");
        if (exProp.arraySize != slotCount)
            exProp.arraySize = slotCount;

        EditorGUILayout.PropertyField(exProp,
            new GUIContent("Example transforms",
                "One optional Transform per slot. Place a transform in the scene to preview where a player standing there would face after teleporting."));

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
