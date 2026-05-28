using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ExplodeBounds : UdonSharpBehaviour
{
    void Start()
    {
        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer)
            meshRenderer.bounds = new Bounds(this.transform.position, Vector3.one * 99);
    }
}
