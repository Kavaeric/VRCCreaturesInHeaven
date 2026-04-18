
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// PlayerObject script for ArrangedTeleport. One instance exists per player.
// The manager takes ownership, writes the synced target, and calls RequestSerialization.
// OnDeserialization fires on the owning player's client when the data arrives and teleports them.
// The manager handles the local player directly since OnDeserialization won't fire for the owner.

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ArrangedTeleportSlot : UdonSharpBehaviour
{
    [UdonSynced] public Vector3 targetPosition;
    [UdonSynced] public Quaternion targetRotation;

    public override void OnDeserialization()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer == null)
            return;

        Debug.Log($"ArrangedTeleportSlot: OnDeserialization for {localPlayer.displayName}, teleporting to {targetPosition}.");
        localPlayer.TeleportTo(targetPosition, targetRotation);
    }
}
