
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class RelativeTeleport : UdonSharpBehaviour
{
    public Transform Anchor;
    public Transform ExampleTransform;

    public float Radius = 10;

    void OnEnable()
    {
        // Bring it into the anchor's local space
        Vector3 LocalPos = Anchor.InverseTransformPoint(Networking.LocalPlayer.GetPosition());
        Vector3 LocalForward = Anchor.InverseTransformDirection(Networking.LocalPlayer.GetRotation() * Vector3.forward);

        if (LocalPos.magnitude > Radius)
            LocalPos = LocalPos.normalized * Radius;

        // un-bring from the destination's world space
        Vector3 WorldPos = transform.TransformPoint(LocalPos);
        Vector3 WorldForward = transform.TransformDirection(LocalForward);

        Networking.LocalPlayer.TeleportTo(WorldPos, Quaternion.LookRotation(WorldForward));
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
        Gizmos.color = Color.magenta;
        DrawArrow(ExampleTransform.position, ExampleTransform.forward);
        Handles.DrawWireDisc(transform.position, Vector3.up, Radius);
        DrawArrow(Anchor.position, Anchor.forward, Radius * 0.5f);

        Gizmos.color = Color.cyan;
        DrawArrow(transform.position, transform.forward, Radius * 0.5f);

        // Bring it into the anchor's local space
        Vector3 LocalPos = Anchor.InverseTransformPoint(ExampleTransform.position);
        Vector3 LocalForward = Anchor.InverseTransformDirection(ExampleTransform.forward);

        if (LocalPos.magnitude > Radius)
            LocalPos = LocalPos.normalized * Radius;

        // un-bring from the destination's world space
        Vector3 WorldPos = transform.TransformPoint(LocalPos);
        Vector3 WorldForward = transform.TransformDirection(LocalForward);
        DrawArrow(WorldPos, WorldForward);
        Handles.DrawWireDisc(Anchor.position, Vector3.up, Radius);
        
    }
#endif
}
