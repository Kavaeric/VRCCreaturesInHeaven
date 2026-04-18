
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Teleports all players inside the entry zone to assigned slots when triggered.
// Uses a PlayerObject (ArrangedTeleportSlot) to teleport each player from their own client,
// which is required because TeleportTo can only be called on the local player.
//
// The manager syncs the assignment table (player ID → position/rotation) to all clients,
// then sends a network event to each player's ArrangedTeleportSlot to dispatch the teleport.
// Each slot reads its own entry from the manager and teleports the local player.
//
// The entry zone is defined by a Transform (origin + rotation), a Size (extents in local
// space), and an Anchor (where the origin sits within the box, [-1 to 1] per axis).

public enum ArrangedTeleportOnEnableMode
{
    Manual,
    OnEnableNetwork
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

    [SerializeField] private Transform[] slots;

    // Reference to the ArrangedTeleportSlot component on the PlayerObject template.
    // Used by FindComponentInPlayerObjects to get each player's slot instance at runtime.
    [SerializeField] private ArrangedTeleportSlot slotTemplate;

    [SerializeField] private bool showGizmos = true;


    // Guards against OnEnable firing before Start. UdonSharp calls OnEnable before Start on
    // scene load, so without this the teleport would fire immediately on world join.
    private bool afterStart;
    void Start()
    {
        afterStart = true;
    }

    void OnEnable()
    {
        if (!afterStart)
            return;

        if (onEnableMode == ArrangedTeleportOnEnableMode.OnEnableNetwork)
            TeleportNetwork();
        // ArrangedTeleportOnEnableMode.Manual: do nothing.
    }

    // Collects all players inside the entry zone, takes ownership of each player's slot,
    // writes the target position/rotation, and calls RequestSerialization on each slot.
    // Remote players teleport via OnDeserialization on their slot. The local player is
    // teleported directly since OnDeserialization doesn't fire for the owner.
    public void TeleportNetwork()
    {
        int playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);

        int slotIndex = 0;
        for (int i = 0; i < players.Length; i++)
        {
            if (slotIndex >= slots.Length)
                break;

            if (!IsInsideEntryZone(players[i].GetPosition()))
                continue;

            if (slots[slotIndex] == null)
            {
                Debug.Log($"ArrangedTeleport: Slot {slotIndex} transform is unassigned. Skipping.");
                slotIndex++;
                continue;
            }

            ArrangedTeleportSlot playerSlot = (ArrangedTeleportSlot)Networking.FindComponentInPlayerObjects(players[i], slotTemplate);
            if (playerSlot == null)
            {
                Debug.Log($"ArrangedTeleport: Could not find ArrangedTeleportSlot for player {players[i].displayName}. Skipping.");
                slotIndex++;
                continue;
            }

            Networking.SetOwner(Networking.LocalPlayer, playerSlot.gameObject);
            playerSlot.targetPosition = slots[slotIndex].position;
            playerSlot.targetRotation = slots[slotIndex].rotation;
            playerSlot.RequestSerialization();

            // OnDeserialization won't fire for the owner, so teleport the local player directly.
            if (players[i] == Networking.LocalPlayer)
                players[i].TeleportTo(slots[slotIndex].position, slots[slotIndex].rotation);

            Debug.Log($"ArrangedTeleport: Assigned {players[i].displayName} to slot {slotIndex}.");
            slotIndex++;
        }

        Debug.Log($"ArrangedTeleport: Assigned {slotIndex} player(s).");
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

    void DrawArrow(Vector3 position, Vector3 forward, float length = 0.5f)
    {
        Gizmos.DrawSphere(position, 0.1f);

        forward = forward.normalized * length;

        Vector3 arrowTip = position + forward;
        Gizmos.DrawLine(position, arrowTip);
        Vector3 rightHead = Quaternion.Euler(0, 160f, 0) * Quaternion.LookRotation(forward) * Vector3.forward;
        Vector3 leftHead = Quaternion.Euler(0, 200f, 0) * Quaternion.LookRotation(forward) * Vector3.forward;
        Gizmos.DrawLine(arrowTip, arrowTip + rightHead * 0.12f * length);
        Gizmos.DrawLine(arrowTip, arrowTip + leftHead * 0.12f * length);
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
        if (!showGizmos || entry == null)
            return;

        Color col = GetColor();
        Color colFaint = GetColor(0.15f);

        // Entry zone box
        BoxCenterAndHalf(entrySize, entryAnchor, out Vector3 boxCenter, out Vector3 boxHalf);
        Vector3 boxSize = boxHalf * 2f;
        Gizmos.color = col;
        DrawArrow(entry.position, entry.rotation * Vector3.forward, Mathf.Abs(entrySize.z) * 0.5f);
        Gizmos.matrix = Matrix4x4.TRS(entry.position, entry.rotation, Vector3.one);
        Gizmos.DrawWireCube(boxCenter, boxSize);
        Gizmos.color = colFaint;
        Gizmos.DrawCube(boxCenter, boxSize);
        Gizmos.matrix = Matrix4x4.identity;

        // Slots
        if (slots == null)
            return;

        Gizmos.color = col;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null)
                continue;

            DrawArrow(slots[i].position, slots[i].forward);
            Handles.Label(slots[i].position + Vector3.up * 0.25f, $"Slot {i}");
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

        EditorGUILayout.LabelField("Teleport behaviour", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("onEnableMode"),
            new GUIContent("Auto-teleport behaviour",
                "What to do when this object is enabled at runtime.\n\n" +
                "Manual: Nothing (call TeleportNetwork yourself).\n\n" +
                "OnEnableNetwork: On enable, teleport all players in the entry zone to their assigned slots."));

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Entry zone", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entry"),
            new GUIContent("Entry", "The transform that defines the centre and rotation of the entry zone."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entryAnchor"),
            new GUIContent("Anchor", "The anchor offset of the entry zone, in [-1 to 1] normalised space per axis. (0,0,0) centres the box at the origin; (0,-1,0) aligns the bottom face with the origin."));
        if (Mathf.Approximately(serializedObject.FindProperty("entryAnchor").vector3Value.y, -1f))
            EditorGUILayout.HelpBox("Anchor Y is -1, suggesting this zone is aligned to the floor. In VRChat, the player origin can be slightly below the ground (-0.005), which may place them just outside the zone. It's recommended to add a little padding to the Y height to account for this.", MessageType.Warning);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entrySize"),
            new GUIContent("Size", "Size of the entry zone box in local units."));

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Slots", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("slotTemplate"),
            new GUIContent("Slot template", "The ArrangedTeleportSlot component on the PlayerObject template. Used to find each player's slot instance at runtime."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("slots"),
            new GUIContent("Slots",
                "One Transform per player slot. Players inside the entry zone are assigned to slots in player-list order. " +
                "If there are more players in the zone than slots, excess players are not teleported."));

        int slotCount = serializedObject.FindProperty("slots").arraySize;
        if (slotCount == 0)
            EditorGUILayout.HelpBox("No slots assigned. Players will not be teleported.", MessageType.Warning);

        if (serializedObject.FindProperty("slotTemplate").objectReferenceValue == null)
            EditorGUILayout.HelpBox("Slot template is not assigned. Drag the ArrangedTeleportSlot component from your PlayerObject template here.", MessageType.Error);

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("showGizmos"),
            new GUIContent("Show gizmos", "Whether to show the entry zone and slot positions in the scene view."));

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
