using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Generic help window driven by an AtelierHelpPage asset.
// Always spawns a fresh instance so multiple pages can be open side by side.
// Usage: AtelierHelpWindow.Open(myHelpPage);
public class AtelierHelpWindow : EditorWindow
{
    // Opens a new window for the given page. Each call creates a fresh instance.
    public static void Open(AtelierHelpPage page)
    {
        if (page == null) return;
        var win = CreateWindow<AtelierHelpWindow>(page.Title);
        win.minSize = new Vector2(320, 200);
        win.Populate(page);
        win.Show();
    }

    void Populate(AtelierHelpPage page)
    {
        rootVisualElement.styleSheets.Add(
            AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/Modules/Atelier/AtelierUI.uss"));

        var scroll = new ScrollView();
        scroll.AddToClassList("p-2");
        scroll.AddToClassList("col");
        rootVisualElement.Add(scroll);

        // Title
        var title = new Label(page.Title);
        title.AddToClassList("font-bold");
        title.AddToClassList("text-lg");
        title.style.whiteSpace = WhiteSpace.Normal;
        scroll.Add(title);

        // Add a gap between the title and the body
        var titleGap = new VisualElement();
        titleGap.AddToClassList("h-2");
        scroll.Add(titleGap);

        // Body
        if (!string.IsNullOrEmpty(page.Body))
        {
            scroll.Add(MakeSpacer(1));
            var body = new Label(page.Body);
            body.enableRichText = true;
            body.style.whiteSpace = WhiteSpace.Normal;
            scroll.Add(body);
        }

        // Images
        if (page.Images != null)
        {
            foreach (var entry in page.Images)
            {
                if (entry.Image == null) continue;

                scroll.Add(MakeSpacer(2));

                var img = new Image { image = entry.Image };
                img.scaleMode = ScaleMode.ScaleToFit;
                scroll.Add(img);

                if (!string.IsNullOrEmpty(entry.Caption))
                {
                    scroll.Add(MakeSpacer(0.5f));
                    var caption = new Label(entry.Caption);
                    caption.AddToClassList("text-sm");
                    caption.AddToClassList("text-muted");
                    caption.style.whiteSpace = WhiteSpace.Normal;
                    scroll.Add(caption);
                }
            }
        }
    }

    static VisualElement MakeSpacer(float units)
    {
        // 1 unit = 4px to match the Atelier spacing scale.
        var spacer = new VisualElement();
        spacer.style.height = units * 4f;
        spacer.style.flexShrink = 0;
        return spacer;
    }
}
