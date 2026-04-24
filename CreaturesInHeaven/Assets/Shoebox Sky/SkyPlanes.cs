
using UdonSharp;
using UnityEditor;
using UnityEngine;

public class SkyPlanes : UdonSharpBehaviour
{
    public Material skyMaterial;

    // When enabled, plane transforms are pushed to the material every frame.
    public bool animate = false;

    public Transform plane0;
    public Transform plane1;
    public Transform plane2;
    public Transform plane3;
    public Transform plane4;
    public Transform plane5;

#if !COMPILER_UDONSHARP && UNITY_EDITOR

    private void OnDrawGizmos()
    {
        SetPlanes();
    }
#endif

    public void SetPlanes()
    {
        SetPlane(plane0, "_Plane0");
        SetPlane(plane1, "_Plane1");
        SetPlane(plane2, "_Plane2");
        SetPlane(plane3, "_Plane3");
        SetPlane(plane4, "_Plane4");
        SetPlane(plane5, "_Plane5");
    }

    void SetPlane(Transform plane, string prefix)
    {
        // Skip unassigned slots.
        if (plane == null) return;

        skyMaterial.SetVector(prefix + "Pos",      plane.position);
        skyMaterial.SetVector(prefix + "Tangent",  plane.forward);
        skyMaterial.SetVector(prefix + "Bitangent", plane.right);
        skyMaterial.SetFloat(prefix + "Size",      (plane.localScale.x +
                                                    plane.localScale.y +
                                                    plane.localScale.z) / 3.0f);
    }

    void Start()
    {
        SetPlanes();
    }

    void Update()
    {
        if (animate) SetPlanes();
    }
}
