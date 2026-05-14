
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using UnityEditor;
using VRC.Udon;

public enum MatchLocalPlayerPositionMode
{
    Manual,
    OnEnable,
    Continuous,
}

public class MatchLocalPlayerPosition : UdonSharpBehaviour
{
    [SerializeField] private MatchLocalPlayerPositionMode mode = MatchLocalPlayerPositionMode.OnEnable;
    [SerializeField] private Vector3 offset = Vector3.zero;
    [SerializeField] private HeartacheAudienceManager audienceManager;

    private bool afterStart;
    void Start()
    {
        // Guards against OnEnable firing before Start. UdonSharp calls OnEnable before Start on
        // scene load, so without this the object would snap immediately on world join.
        afterStart = true;
    }

    void OnEnable()
    {
        if (!afterStart)
            return;

        if (mode == MatchLocalPlayerPositionMode.OnEnable)
            SnapToPlayer();
    }

    public override void PostLateUpdate()
    {
        if (mode == MatchLocalPlayerPositionMode.Continuous)
            SnapToPlayer();
    }

    // Called by ArrangedTeleport (or any other behaviour) after the local player is teleported.
    public void OnPostTeleport()
    {
        SnapToPlayer();
    }

    public void SnapToPlayer()
    {
        if (audienceManager != null && !audienceManager.WatchingAnimation)
            return;

        transform.SetPositionAndRotation(Networking.LocalPlayer.GetPosition() + offset, transform.rotation);
    }
}

#if !COMPILER_UDONSHARP && UNITY_EDITOR
[CustomEditor(typeof(MatchLocalPlayerPosition))]
public class MatchLocalPlayerPositionEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Behaviour", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("mode"),
            new GUIContent("Mode",
                "When to snap this object to the local player's position.\n\n" +
                "Manual: Nothing (call SnapToPlayer yourself).\n\n" +
                "OnEnable: Snap once when this object is enabled.\n\n" +
                "Continuous: Snap every frame."));

        var modeVal = (MatchLocalPlayerPositionMode)serializedObject.FindProperty("mode").enumValueIndex;
        switch (modeVal)
        {
            case MatchLocalPlayerPositionMode.Manual:
                EditorGUILayout.HelpBox("This object will not move until SnapToPlayer() is called.", MessageType.None);
                break;
            case MatchLocalPlayerPositionMode.OnEnable:
                EditorGUILayout.HelpBox("This object will snap to the local player's position when enabled.", MessageType.None);
                break;
            case MatchLocalPlayerPositionMode.Continuous:
                EditorGUILayout.HelpBox("This object will follow the local player's position every frame.", MessageType.None);
                break;
        }

        EditorGUILayout.Space(8f);

        EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("audienceManager"),
            new GUIContent("Audience Manager", "If assigned, SnapToPlayer() will do nothing while the player is not watching the animation."));

        EditorGUILayout.Space(8f);

        EditorGUILayout.LabelField("Position", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("offset"),
            new GUIContent("Offset", "World-space offset applied on top of the player's position."));

        serializedObject.ApplyModifiedProperties();
    }

    private void OnSceneGUI()
    {
        var t = (MatchLocalPlayerPosition)target;
        Vector3 offset = serializedObject.FindProperty("offset").vector3Value;

        // Sphere marks where the player origin will be: object position minus the offset.
        Vector3 playerPos = t.transform.position - offset;

        Handles.color = new Color(0.4f, 0.8f, 1f, 0.4f);
        Handles.SphereHandleCap(0, playerPos, Quaternion.identity, 0.2f, EventType.Repaint);
        Handles.color = new Color(0.4f, 0.8f, 1f, 0.8f);
        Handles.DrawDottedLine(t.transform.position, playerPos, 4f);
    }
}
#endif
