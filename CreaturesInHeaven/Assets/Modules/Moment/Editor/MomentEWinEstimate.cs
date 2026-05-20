using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

public class MomentEWinEstimate : EditorWindow
{
    [MenuItem("Tools/Moment ALV/Estimate bake size...")]
    static void Open() => GetWindow<MomentEWinEstimate>("Estimate ALV size");

    Vector3Int _dimensions = Vector3Int.zero;
    int _snapshots = 80;

    // Cell elements indexed by [shMode, bitDepth].
    VisualElement[,] _vramCells;

    // Bundle cell label triplets indexed by [shMode, bitDepth]: (lo, hi).
    (Label lo, Label hi)[,] _bundleCells;

    public void CreateGUI()
    {
        string dir = MomentAssetPaths.ScriptDir();

        // Set window icon
        string iconPath = $"{dir}/Resources/Icon Moment EWin Estimate@2x.png";
        Texture2D windowIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        titleContent = new GUIContent("Estimate ALV size", windowIcon);

        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/MomentEWinEstimate.uxml");
        if (uxml == null)
        {
            rootVisualElement.Add(new Label($"MomentEWinEstimate.uxml not found in {dir}."));
            return;
        }
        uxml.CloneTree(rootVisualElement);

        // Bind input fields.
        var dimsField  = rootVisualElement.Q<Vector3IntField>("bake-output-dimensions-field");
        var snapsField = rootVisualElement.Q<IntegerField>("bake-output-snapshots-field");

        dimsField.RegisterValueChangedCallback(evt  => { _dimensions = evt.newValue; UpdateTable(); });
        snapsField.RegisterValueChangedCallback(evt => { _snapshots  = evt.newValue; UpdateTable(); });

        // Cache VRAM cell elements. Rows = SH modes, cols = bit depths.
        _vramCells = new VisualElement[3, 2]
        {
            { rootVisualElement.Q("vram-est-L1-8bpc"),     rootVisualElement.Q("vram-est-L1-16bpc")     },
            { rootVisualElement.Q("vram-est-monoL1-8bpc"), rootVisualElement.Q("vram-est-monoL1-16bpc") },
            { rootVisualElement.Q("vram-est-monoL0-8bpc"), rootVisualElement.Q("vram-est-monoL0-16bpc") },
        };

        // Cache bundle cell label pairs.
        string[] shNames  = { "L1", "monoL1", "monoL0" };
        string[] bpcNames = { "8bpc", "16bpc" };
        _bundleCells = new (Label, Label)[3, 2];
        for (int s = 0; s < 3; s++)
        {
            for (int b = 0; b < 2; b++)
            {
                string prefix = $"bundle-est-{shNames[s]}-{bpcNames[b]}";
                _bundleCells[s, b] = (
                    rootVisualElement.Q<Label>($"{prefix}-lo"),
                    rootVisualElement.Q<Label>($"{prefix}-hi")
                );
            }
        }

        // Seed the initial snapshot value from the UXML default.
        _snapshots = snapsField.value;
        UpdateTable();
    }

    void UpdateTable()
    {
        bool valid = _dimensions.x > 0 && _dimensions.y > 0 && _dimensions.z > 0 && _snapshots > 0;

        for (int s = 0; s < 3; s++)
        {
            var shMode = (MomentALVSHMode)s;
            for (int b = 0; b < 2; b++)
            {
                var bitDepth = (MomentALVBitDepth)b;

                // VRAM table.
                var vramCell = _vramCells[s, b];
                if (vramCell != null)
                {
                    var label = vramCell.Q<Label>();
                    if (label != null)
                        label.text = valid
                            ? $"{MomentALVFormat.VramMB(_dimensions.x, _dimensions.y, _dimensions.z, _snapshots, shMode, bitDepth):F1} MB"
                            : "—";
                }

                // Bundle table.
                var (lo, hi) = _bundleCells[s, b];
                if (lo == null || hi == null) continue;

                if (!valid)
                {
                    lo.text = "—";
                    hi.text = "—";
                    continue;
                }

                double vram = MomentALVFormat.VramMB(_dimensions.x, _dimensions.y, _dimensions.z, _snapshots, shMode, bitDepth);
                lo.text = $"{vram * MomentALVFormat.BundleRatioLow:F1} MB";
                hi.text = $"{vram * MomentALVFormat.BundleHighRatio(shMode):F1} MB";
            }
        }
    }
}
