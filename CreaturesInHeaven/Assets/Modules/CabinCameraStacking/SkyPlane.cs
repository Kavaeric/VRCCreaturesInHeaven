using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SkyPlane : MonoBehaviour
{
    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.9f, 0.6f, 0.6f, 0.5f);
        Gizmos.matrix = Matrix4x4.TRS(this.transform.position, this.transform.rotation, this.transform.localScale);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 1.01f);
        Gizmos.color = new Color(0.9f, 0.6f, 0.6f, 0.05f);
        Gizmos.DrawCube(Vector3.zero, Vector3.one * 1.01f);
        Gizmos.matrix = Matrix4x4.identity;
    }
}
