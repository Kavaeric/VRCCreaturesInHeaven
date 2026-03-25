
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

    [Tooltip("Radius to grab players at the anchor.")]
    public float GrabRadius = 25;

    [Tooltip("Clamp radius to force players into at the destination.")]
    public float ClampRadius = 10;

    bool afterStart = false;
    void Start()
    {
        afterStart = true;
    }
    
    void OnEnable()
    {
        if (!afterStart) // lock so it doesn't do anything until you intentionally enable it.
            return;

        // Bring it into the anchor's local space
        Vector3 LocalPos = Anchor.InverseTransformPoint(Networking.LocalPlayer.GetPosition());
        Vector3 LocalForward = Anchor.InverseTransformDirection(Networking.LocalPlayer.GetRotation() * Vector3.forward);

        if (LocalPos.magnitude > GrabRadius)
            return;

        if (LocalPos.magnitude > ClampRadius)
            LocalPos = LocalPos.normalized * ClampRadius;

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
        Handles.color = Color.magenta;
        DrawArrow(ExampleTransform.position, ExampleTransform.forward);
        Handles.DrawWireDisc(transform.position, Vector3.up, ClampRadius);
        DrawArrow(Anchor.position, Anchor.forward, ClampRadius * 0.5f);

        Gizmos.color = Color.cyan;
        Handles.color = Color.cyan;
        DrawArrow(transform.position, transform.forward, ClampRadius * 0.5f);

        // Bring it into the anchor's local space
        Vector3 LocalPos = Anchor.InverseTransformPoint(ExampleTransform.position);
        Vector3 LocalForward = Anchor.InverseTransformDirection(ExampleTransform.forward);

        if (LocalPos.magnitude > ClampRadius)
            LocalPos = LocalPos.normalized * ClampRadius;

        // un-bring from the destination's world space
        Vector3 WorldPos = transform.TransformPoint(LocalPos);
        Vector3 WorldForward = transform.TransformDirection(LocalForward);
        DrawArrow(WorldPos, WorldForward);
        
        Handles.DrawWireDisc(Anchor.position, Vector3.up, GrabRadius);
    }
#endif
}
