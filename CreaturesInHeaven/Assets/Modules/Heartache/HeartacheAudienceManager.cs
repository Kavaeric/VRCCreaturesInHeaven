
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HeartacheAudienceManager : UdonSharpBehaviour
{
    [SerializeField] private float _lobbyYMin = -100f;
    [SerializeField] private float _lobbyYMax = 100f;

    public bool WatchingAnimation { get; private set; }

    void Update()
    {
        float y = Networking.LocalPlayer.GetPosition().y;
        WatchingAnimation = y < _lobbyYMin || y > _lobbyYMax;
    }
}
