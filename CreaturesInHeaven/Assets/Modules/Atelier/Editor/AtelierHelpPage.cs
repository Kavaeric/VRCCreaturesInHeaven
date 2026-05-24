using UnityEngine;

// Authored as a ScriptableObject asset: right-click > Create > Atelier/Help Page.
// Reference it from any editor script and pass to AtelierHelpWindow.Open().
[CreateAssetMenu(menuName = "Atelier/Help Page", fileName = "HelpPage")]
public class AtelierHelpPage : ScriptableObject
{
    public string Title;

    [TextArea(4, 20)]
    public string Body;

    public ImageEntry[] Images;

    [System.Serializable]
    public struct ImageEntry
    {
        public Texture2D Image;
        public string Caption;
    }
}
