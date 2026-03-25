
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GroupTeleport : UdonSharpBehaviour
{
    public Transform[] Slots;

    bool afterStart = false;
    void Start()
    {
        afterStart = true;
    }

    void OnEnable()
    {
        if (!afterStart) // lock so it doesn't do anything until you intentionally enable it.
            return;

        if (!Networking.LocalPlayer.IsOwner(this.gameObject))
            return;

        int playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        for (int i = 0; i < players.Length; i++)
        {
            if (!Slots[i])
                continue;

            players[i].TeleportTo(Slots[i].position, Slots[i].rotation);
        }
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
    private void OnDrawGizmos()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            if (!Slots[i])
                continue;
            Gizmos.color = Color.cyan;

            Gizmos.DrawSphere(Slots[i].position, 0.1f);

            // Draw Arrow
            Vector3 arrowTip = Slots[i].position + Slots[i].forward * 0.5f;
            Gizmos.DrawLine(Slots[i].position, arrowTip);
            Vector3 rightHead = Quaternion.LookRotation(Slots[i].forward) * Quaternion.Euler(0, 160f, 0) * Vector3.forward;
            Vector3 leftHead = Quaternion.LookRotation(Slots[i].forward) * Quaternion.Euler(0, 200f, 0) * Vector3.forward;
            Gizmos.DrawLine(arrowTip, arrowTip + rightHead * 0.12f);
            Gizmos.DrawLine(arrowTip, arrowTip + leftHead * 0.12f);
        }
    }
#endif
}
