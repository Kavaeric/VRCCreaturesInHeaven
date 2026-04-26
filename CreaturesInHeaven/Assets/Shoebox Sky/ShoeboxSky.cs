
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ShoeboxSky : UdonSharpBehaviour
{
    public Material skyMaterial;

    private const int PLANE_COUNT = 10;
    private ShoeboxSkyImposter[] _planes;

#if !COMPILER_UDONSHARP && UNITY_EDITOR
    private void OnDrawGizmos()
    {
        SetPlanes();
    }
#endif

    void Start()
    {
        _planes = GetComponentsInChildren<ShoeboxSkyImposter>();
        SetPlanes();
    }

    void Update()
    {
        if (_planes == null) return;
        for (int i = 0; i < _planes.Length && i < PLANE_COUNT; i++)
        {
            if (_planes[i].animate)
                SetPlane(_planes[i], i);
        }
    }

    public void SetPlanes()
    {
        if (skyMaterial == null) return;

        ShoeboxSkyImposter[] planes = GetComponentsInChildren<ShoeboxSkyImposter>();
        for (int i = 0; i < PLANE_COUNT; i++)
        {
            if (i < planes.Length)
                SetPlane(planes[i], i);
            else
                ClearPlane(i);
        }
    }

    void SetPlane(ShoeboxSkyImposter plane, int index)
    {
        string prefix = "_Plane" + index;
        Transform t = plane.transform;

        skyMaterial.SetVector(prefix + "Pos",        t.position);
        skyMaterial.SetVector(prefix + "Tangent",    t.forward);
        skyMaterial.SetVector(prefix + "Bitangent",  t.right);
        skyMaterial.SetFloat( prefix + "Size",       (t.localScale.x + t.localScale.y + t.localScale.z) / 3f);
        skyMaterial.SetTexture(prefix + "Texture",   plane.texture);
        skyMaterial.SetFloat( prefix + "Scroll",     plane.scroll);
        skyMaterial.SetVector(prefix + "Texture_ST", new Vector4(plane.tiling.x, plane.tiling.y, plane.offset.x, plane.offset.y));
    }

    void ClearPlane(int index)
    {
        // Clear the slot so no ghost texture bleeds through when a plane is removed.
        skyMaterial.SetTexture("_Plane" + index + "Texture", null);
    }
}
