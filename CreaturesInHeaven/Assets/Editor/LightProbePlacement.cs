
#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using UnityEngine.AI;
using Unity.AI.Navigation;

// I cleaned it up a bit -Torvid
// This code is public domain as far as I'm concerned

// Added Use Existing NavMesh option -Hiyu

public class LightProbePlacement : EditorWindow
{
    bool useExistingNavMesh = false;
    float mergeDistance = 1;
    LightProbeGroup probeGroup;
    bool mergeProbes = true;
    int numberOfProbesPlaced = 0;

    [MenuItem("Tools/Torvid/Generate Light Probes")]
    static void Init()
    {
        EditorWindow window = GetWindow(typeof(LightProbePlacement));
        window.Show();
    }

    void PlaceProbes()
    {
        GameObject navmeshObj = new GameObject("navmesh");

        if (!useExistingNavMesh)
        {
            EditorUtility.DisplayProgressBar("Baking Navmesh", "Baking Navmesh", 0);

            NavMeshSurface surface = navmeshObj.AddComponent<NavMeshSurface>();

            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(surface.agentTypeID);

            float originalRadius = settings.agentRadius;

            settings.agentRadius = 0.2f;

            surface.BuildNavMesh();

            settings.agentRadius = originalRadius;
        }

        NavMeshTriangulation navMesh = NavMesh.CalculateTriangulation();

        List<Vector3> newProbes = new List<Vector3>();
        int probeListLength = navMesh.vertices.Length;

        //  Add lower probe
        for (int i = 0; i < probeListLength; i++)
        {
            newProbes.Add(navMesh.vertices[i] + Vector3.up * 0.015f);
        }

        if (mergeProbes)
        {
            EditorUtility.DisplayProgressBar("Merging Nearby Probes", "Merging Nearby Probes", 0);
            float mergeDistanceSquared = mergeDistance * mergeDistance;

            for (int i = 0; i < probeListLength; i++)
            {
                Vector3 pos0 = newProbes[i];

                for (int j = i + 1; j < probeListLength; j++)
                {
                    Vector3 pos1 = newProbes[j];
                    Vector3 d = pos0 - pos1;
                    if (d.x * d.x + d.y * d.y + d.z * d.z <= mergeDistanceSquared)
                    {
                        newProbes.RemoveAt(j);
                        j--;
                        probeListLength--;
                    }
                }
            }
        }

        //  Add upper probe
        float YOffset = 2.25f - 0.015f;
        for (int i = 0; i < probeListLength; i++)
        {
            newProbes.Add(newProbes[i] + Vector3.up * YOffset);
        }

        probeGroup.probePositions = newProbes.ToArray();
        numberOfProbesPlaced = newProbes.Count;

        DestroyImmediate(navmeshObj);
        EditorUtility.ClearProgressBar();
    }

    void OnGUI()
    {
        GUI.enabled = probeGroup;
        if (GUILayout.Button("Place Probes"))
        {
            PlaceProbes();
            EditorUtility.ClearProgressBar();
        }
        GUI.enabled = true;

        useExistingNavMesh = EditorGUILayout.Toggle("Use Existing NavMesh", useExistingNavMesh);
        mergeProbes = EditorGUILayout.Toggle("Merge Probes", mergeProbes);

        GUI.enabled = mergeProbes;
        mergeDistance = EditorGUILayout.FloatField("Merge distance", mergeDistance);
        GUI.enabled = true;

        probeGroup = (LightProbeGroup)EditorGUILayout.ObjectField("Light Probe Group", probeGroup, typeof(LightProbeGroup), true);

        if (!probeGroup)
        {
            EditorGUILayout.HelpBox("Probe object does not have a Light Probe Group attached to it", MessageType.Error);
        }
        if (useExistingNavMesh)
        {
            EditorGUILayout.HelpBox("Please make sure that you have generated a navmesh before using the script.", MessageType.Info);
        }

        if (numberOfProbesPlaced > 0)
        {
            EditorGUILayout.HelpBox($"{numberOfProbesPlaced} probes placed.", MessageType.Info);
        }
    }
    void OnInspectorUpdate()
    {
        Repaint();
    }
}
#endif