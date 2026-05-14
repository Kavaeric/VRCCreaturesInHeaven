using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(HeartacheAudienceManager))]
public class HeartacheEUIAudienceManager : Editor
{
    private void OnSceneGUI()
    {
        float yMin = serializedObject.FindProperty("lobbyYMin").floatValue;
        float yMax = serializedObject.FindProperty("lobbyYMax").floatValue;

        float size = 20f;

        Color fill = new(1f, 0.5f, 0f, 0.1f);
        Color outline = new(1f, 0.5f, 0f, 0.8f);

        Handles.DrawSolidRectangleWithOutline(
            new Vector3[] {
                new(-size, yMin, -size),
                new( size, yMin, -size),
                new( size, yMin,  size),
                new(-size, yMin,  size),
            },
            fill, outline
        );

        Handles.DrawSolidRectangleWithOutline(
            new Vector3[] {
                new(-size, yMax, -size),
                new( size, yMax, -size),
                new( size, yMax,  size),
                new(-size, yMax,  size),
            },
            fill, outline
        );

        Handles.color = outline;
        Handles.Label(new Vector3(-size, yMin, -size), $"Lobby Y min ({yMin})");
        Handles.Label(new Vector3(-size, yMax, -size), $"Lobby Y max ({yMax})");
    }
}
