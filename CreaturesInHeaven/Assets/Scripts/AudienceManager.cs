
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

#if UNITY_EDITOR
using UnityEditor;
#endif

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AudienceManager : UdonSharpBehaviour
{
    [SerializeField] private float lobbyYMin = -100f;
    [SerializeField] private float lobbyYMax = 100f;

    public bool WatchingAnimation { get; private set; }

    void Update()
    {
        float y = Networking.LocalPlayer.GetPosition().y;
        WatchingAnimation = y < lobbyYMin || y > lobbyYMax;
    }
}

#if !COMPILER_UDONSHARP && UNITY_EDITOR
[CustomEditor(typeof(AudienceManager))]
public class AudienceManagerEditor : Editor
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
#endif
