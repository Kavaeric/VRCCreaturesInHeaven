
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class RelativeTeleport : UdonSharpBehaviour
{
    public Transform Entry;
    public Transform Exit;
    public Transform ExampleTransform;

    //[Tooltip("Radius to grab players at the anchor.")]
    //public float GrabRadius = 25;
    //
    //[Tooltip("Clamp radius to force players into at the destination.")]
    //public float ClampRadius = 10;

    bool afterStart = false;
    void Start()
    {
        afterStart = true;
    }
    
    void OnEnable()
    {
        if (!afterStart) // lock so it doesn't do anything until you intentionally enable it.
            return;

        TriggerLocal();
    }

    public void _NetworkTrigger()
    {
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(TriggerLocal));
    }

    public void TriggerLocal()
    {
        // Bring it into the anchor's local space
        Vector3 LocalPos = Entry.InverseTransformPoint(Networking.LocalPlayer.GetPosition());
        Vector3 LocalForward = Entry.InverseTransformDirection(Networking.LocalPlayer.GetRotation() * Vector3.forward);

        //if (LocalPos.magnitude > GrabRadius)
        //    return;

        // player is outside the teleport region, do nothing.
        if (LocalPos.x < -1 || LocalPos.y < -1 || LocalPos.z < -1 || LocalPos.x > 1 || LocalPos.y > 1 || LocalPos.z > 1)
            return;

        
        //if (LocalPos.magnitude > ClampRadius)
        //    LocalPos = LocalPos.normalized * ClampRadius;

        // un-bring from the destination's world space
        Vector3 WorldPos = Exit.TransformPoint(LocalPos);
        Vector3 WorldForward = Exit.TransformDirection(LocalForward);

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
        //Handles.color = Color.magenta;
        DrawArrow(ExampleTransform.position, ExampleTransform.forward);
        DrawArrow(Entry.position, Entry.forward, Entry.localScale.z * 0.5f);
        Gizmos.matrix = Entry.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(1, 0, 1));
        Gizmos.matrix = Matrix4x4.identity;
        //Handles.DrawWireDisc(Entry.position, Vector3.up, GrabRadius);

        Gizmos.color = Color.cyan;
        //Handles.color = Color.cyan;
        DrawArrow(Exit.position, Exit.forward, Exit.localScale.z * 0.5f);

        // Bring it into the anchor's local space
        Vector3 LocalPos = Entry.InverseTransformPoint(ExampleTransform.position);
        Vector3 LocalForward = Entry.InverseTransformDirection(ExampleTransform.forward);

        LocalPos = new Vector3(Mathf.Clamp(LocalPos.x, -1, 1), Mathf.Clamp(LocalPos.y, -1, 1), Mathf.Clamp(LocalPos.z, -1, 1));
        //if (LocalPos.magnitude > ClampRadius)
        //    LocalPos = LocalPos.normalized * ClampRadius;

        // un-bring from the destination's world space
        Vector3 WorldPos = Exit.TransformPoint(LocalPos);
        Vector3 WorldForward = Exit.TransformDirection(LocalForward);
        DrawArrow(WorldPos, WorldForward);

        Gizmos.matrix = Exit.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(1,0,1));
        Gizmos.matrix = Matrix4x4.identity;
        //Handles.DrawWireDisc(Exit.position, Vector3.up, Exit.localScale.z);
    }
#endif
}
