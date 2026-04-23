using System;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteInEditMode]
public class PopulateRandom : MonoBehaviour
{
    [Serializable]
    public struct WeightedPrefab
    {
        public GameObject prefab;
        [Min(0f)] public float weight;
    }

    [Header("Prefabs")]
    public WeightedPrefab[] prefabs;

    [Header("Placement")]
    public Vector3 boundsSize = new Vector3(10f, 0f, 10f);
    public int count = 20;
    public int seed = 0;

    [Header("Rotation")]
    public Vector3 rotation = Vector3.zero;

    [ContextMenu("Regenerate")]
    public void Regenerate()
    {
        // Clear existing children spawned by this script
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
#if UNITY_EDITOR
            DestroyImmediate(transform.GetChild(i).gameObject);
#else
            Destroy(transform.GetChild(i).gameObject);
#endif
        }

        if (prefabs == null || prefabs.Length == 0) return;

        // Build cumulative weight table
        float totalWeight = 0f;
        foreach (var entry in prefabs)
            totalWeight += Mathf.Max(0f, entry.weight);

        if (totalWeight <= 0f)
        {
            Debug.LogWarning("PopulateRandom: all prefab weights are zero.");
            return;
        }

        var rng = new System.Random(seed);
        Vector3 half = boundsSize * 0.5f;

        for (int i = 0; i < count; i++)
        {
            GameObject prefab = PickWeighted(rng, totalWeight);
            if (prefab == null) continue;

            Vector3 localPos = new Vector3(
                (float)(rng.NextDouble() * boundsSize.x - half.x),
                (float)(rng.NextDouble() * boundsSize.y - half.y),
                (float)(rng.NextDouble() * boundsSize.z - half.z)
            );

            Quaternion localRot = Quaternion.Euler(rotation);

#if UNITY_EDITOR
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, transform);
            instance.transform.SetLocalPositionAndRotation(localPos, localRot);
            instance.transform.localScale = Vector3.one;
            instance.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable;
#else
            GameObject instance = Instantiate(prefab, transform);
            instance.transform.SetLocalPositionAndRotation(localPos, localRot);
            instance.transform.localScale = Vector3.one;
#endif
        }
    }

    // Picks a prefab by weight using a single RNG draw
    private GameObject PickWeighted(System.Random rng, float totalWeight)
    {
        float pick = (float)(rng.NextDouble() * totalWeight);
        float cumulative = 0f;
        foreach (var entry in prefabs)
        {
            cumulative += Mathf.Max(0f, entry.weight);
            if (pick <= cumulative)
                return entry.prefab;
        }
        // Fallback for floating-point edge
        return prefabs[prefabs.Length - 1].prefab;
    }

    // Draw the bounding box in the scene view
    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.4f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, boundsSize);
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(PopulateRandom))]
public class PopulateRandomEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();

        PopulateRandom t = (PopulateRandom)target;

        if (GUILayout.Button("Regenerate"))
        {
            Undo.RegisterFullObjectHierarchyUndo(t.gameObject, "Regenerate PopulateRandom");
            t.Regenerate();
        }

        if (GUILayout.Button("Randomise Seed & Regenerate"))
        {
            Undo.RegisterFullObjectHierarchyUndo(t.gameObject, "Regenerate PopulateRandom");
            t.seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            t.Regenerate();
        }

        EditorGUILayout.EndHorizontal();

        // Stats readout
        int total = t.transform.childCount;
        if (total > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Population", EditorStyles.boldLabel);

            // Tally children by name, stripping Unity's "(Clone)" suffix
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (Transform child in t.transform)
            {
                string name = child.gameObject.name.Replace("(Clone)", "").Trim();
                if (counts.ContainsKey(name))
                    counts[name]++;
                else
                    counts[name] = 1;
            }

            EditorGUI.indentLevel++;
            foreach (var pair in counts)
            {
                float pct = pair.Value / (float)total * 100f;
                EditorGUILayout.LabelField(pair.Key, string.Format("{0} ({1:0.#}%)", pair.Value, pct));
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.LabelField("Total", total.ToString());
        }
    }
}
#endif
