
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class MatchLocalPlayerPosition : UdonSharpBehaviour
{
    [SerializeField] private Vector3 offset = Vector3.zero;
    private bool afterStart;
    void Start()
    {
        // Guards against OnEnable firing before Start. UdonSharp calls OnEnable before Start on
        // scene load, so without this the teleport would fire immediately on world join.
        // Doesn't make it feel any less cursed, though, Torvid.
        afterStart = true;
    }

    void OnEnable()
    {
        if (!afterStart)
            return;

        transform.SetPositionAndRotation(Networking.LocalPlayer.GetPosition() + offset, transform.rotation);

        Debug.Log($"[MatchLocalPlayerPosition] Moved object {name} to player position: {transform.position}");
    }
}
