
using UdonSharp;
using UnityEditor;
using UnityEngine;

public class SkyPlanes : UdonSharpBehaviour
{
    public Material skyMaterial;

    public Transform plane0;
    public Transform plane1;

#if !COMPILER_UDONSHARP && UNITY_EDITOR

    private void OnDrawGizmos()
    {
        SetPlanes();
    }
#endif

    public void SetPlanes()
    {

        skyMaterial.SetVector("_Plane0Pos", plane0.transform.position);
        skyMaterial.SetVector("_Plane0Tangent", plane0.transform.forward);
        skyMaterial.SetVector("_Plane0Bitangent", plane0.transform.right);
        skyMaterial.SetFloat("_Plane0Size", (plane0.localScale.x +
                                                    plane0.localScale.y +
                                                    plane0.localScale.z) / 3.0f);

        skyMaterial.SetVector("_Plane1Pos", plane1.transform.position);
        skyMaterial.SetVector("_Plane1Tangent", plane1.transform.forward);
        skyMaterial.SetVector("_Plane1Bitangent", plane1.transform.right);
        skyMaterial.SetFloat("_Plane1Size", (plane1.localScale.x +
                                                    plane1.localScale.y +
                                                    plane1.localScale.z) / 3.0f);
    }

    void Start()
    {
        SetPlanes();
    }
}
