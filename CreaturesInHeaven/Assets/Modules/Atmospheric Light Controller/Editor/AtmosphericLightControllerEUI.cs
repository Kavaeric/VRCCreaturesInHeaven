using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(AtmosphericLightController))]
public class AtmosphericLightControllerEUI : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        AtmosphericLightController controller = (AtmosphericLightController)target;

        GUILayout.Space(10);
        GUILayout.BeginVertical("box");
        GUILayout.Label("Atmospheric Light", EditorStyles.boldLabel);

        if (GUILayout.Button("Recalculate Atmospheric Light", GUILayout.Height(30)))
        {
            controller.RecalculateAtmosphericLight();
        }

        if (GUILayout.Button("Sync Sun Direction from Light", GUILayout.Height(30)))
        {
            controller.SyncSunDirectionFromLight();
        }

        GUILayout.EndVertical();
    }
}
