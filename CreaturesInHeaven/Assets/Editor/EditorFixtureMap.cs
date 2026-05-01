using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Graphical fixture map window. Displays fixtures loaded from a FixtureMap.json
// (produced by GenerateFixtureMap) as labelled nodes on a 2D canvas.
//
// Open via: Tools > Fixture Map
public class EditorFixtureMap : EditorWindow
{
    // --- Data --------------------------------------------------------

    [Serializable]
    private struct FixtureEntry
    {
        public string          name;
        public string          sceneObject;
        public FixturePosition position;
        public FixturePosition size;  // width (X, long axis) and depth (Y, short axis) in metres
    }

    [Serializable]
    private struct FixturePosition
    {
        public float x;
        public float y;
    }

    [Serializable]
    private struct ThemeColour
    {
        public float r, g, b, a;
        public Color ToColor() => new Color(r, g, b, a);
    }

    [Serializable]
    private struct Theme
    {
        public ThemeColour node;
        public ThemeColour nodeOutline;
        public ThemeColour nodeSelected;
        public ThemeColour nodeOutlineSel;
        public ThemeColour label;

        public static Theme Default() => new Theme
        {
            node           = new ThemeColour { r = 0.90f, g = 0.85f, b = 1.00f, a = 0.50f },
            nodeOutline    = new ThemeColour { r = 1.00f, g = 1.00f, b = 1.00f, a = 1.00f },
            nodeSelected   = new ThemeColour { r = 0.90f, g = 0.75f, b = 1.00f, a = 0.85f },
            nodeOutlineSel = new ThemeColour { r = 0.80f, g = 0.60f, b = 1.00f, a = 1.00f },
            label          = new ThemeColour { r = 1.00f, g = 1.00f, b = 1.00f, a = 0.75f },
        };
    }

    private const string ThemePath = "Assets/Editor/EditorFixtureMapTheme.json";

    // --- State -------------------------------------------------------

    private List<FixtureEntry>         _fixtures       = new();
    private List<UnityEngine.Object>   _fixtureObjects = new();  // resolved scene objects, parallel to _fixtures (null if unresolved)
    private string                     _mapPath        = "";
    private Theme                      _theme          = Theme.Default();

    // Canvas layout — recomputed when fixtures load or canvas resizes.
    private Vector2 _canvasSize;
    private float   _scale;
    private Vector2 _offset;  // canvas-space origin (maps fixture 0,0 to this pixel)

    // Visual element refs
    private IMGUIContainer _canvas;
    private Label          _pathLabel;
    private Label          _aspectReadout;

    // Node appearance
    private const float NodeMinSize = 8f;   // minimum node dimension in pixels when size data is absent or tiny
    private const float NodePadding = 48f;  // minimum margin from canvas edge to outermost node centre
    private float _nodeAspectAdjustment = 0.05f;

    // --- Lifecycle ---------------------------------------------------

    [MenuItem("Tools/Fixture Map")]
    private static void Open() => GetWindow<EditorFixtureMap>("Fixture Map");

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        _canvas?.MarkDirtyRepaint();
    }

    public void CreateGUI()
    {
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/EditorFixtureMap.uxml");
        uxml.CloneTree(rootVisualElement);

        _pathLabel = rootVisualElement.Q<Label>("map-path-label");

        _canvas = rootVisualElement.Q<IMGUIContainer>("canvas");
        _canvas.onGUIHandler = DrawCanvas;
        _canvas.RegisterCallback<GeometryChangedEvent>(_ => RecalculateLayout());

        rootVisualElement.Q<Button>("load-btn").clicked   += PromptLoad;
        rootVisualElement.Q<Button>("reload-btn").clicked += Reload;

        _aspectReadout = rootVisualElement.Q<Label>("node-aspect-adjustment-readout");
        _aspectReadout.text = _nodeAspectAdjustment.ToString("F2");

        var aspectField = rootVisualElement.Q<Slider>("node-aspect-adjustment");
        aspectField.value = _nodeAspectAdjustment;
        aspectField.RegisterValueChangedCallback(e =>
        {
            _nodeAspectAdjustment = e.newValue;
            _aspectReadout.text = _nodeAspectAdjustment.ToString("F2");
            _canvas.MarkDirtyRepaint();
        });

        LoadTheme();

        // Restore last-used path across domain reloads.
        string saved = EditorPrefs.GetString("EditorFixtureMap.lastPath", "");
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
            LoadFrom(saved);
    }

    // --- Loading -----------------------------------------------------

    private void PromptLoad()
    {
        string dir  = string.IsNullOrEmpty(_mapPath) ? "Assets" : Path.GetDirectoryName(_mapPath);
        string path = EditorUtility.OpenFilePanel("Open Fixture Map", dir, "json");
        if (!string.IsNullOrEmpty(path))
            LoadFrom(path);
    }

    private void Reload()
    {
        LoadTheme();
        if (!string.IsNullOrEmpty(_mapPath))
            LoadFrom(_mapPath);
    }

    private void LoadTheme()
    {
        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../", ThemePath));
        if (!File.Exists(fullPath)) return;
        try
        {
            _theme = JsonUtility.FromJson<Theme>(File.ReadAllText(fullPath));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EditorFixtureMap] Failed to load theme {ThemePath}: {ex.Message}");
        }
    }

    private void LoadFrom(string absolutePath)
    {
        try
        {
            string json = File.ReadAllText(absolutePath);
            _fixtures = ParseFixtureArray(json);
            _mapPath  = absolutePath;

            EditorPrefs.SetString("EditorFixtureMap.lastPath", absolutePath);

            string projectRelative = ToProjectRelative(absolutePath);
            _pathLabel.text = projectRelative ?? absolutePath;

            ResolveSceneObjects();
            RecalculateLayout();
            _canvas.MarkDirtyRepaint();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[EditorFixtureMap] Failed to load {absolutePath}: {ex.Message}");
        }
    }

    private void ResolveSceneObjects()
    {
        _fixtureObjects = new List<UnityEngine.Object>(_fixtures.Count);
        foreach (var f in _fixtures)
        {
            UnityEngine.Object obj = null;
            if (!string.IsNullOrEmpty(f.sceneObject) &&
                GlobalObjectId.TryParse(f.sceneObject, out GlobalObjectId gid))
                obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            _fixtureObjects.Add(obj);
        }
    }

    // --- Layout ------------------------------------------------------

    private void RecalculateLayout()
    {
        if (_fixtures == null || _fixtures.Count == 0) return;

        Rect rect = _canvas.contentRect;
        if (rect.width <= 0 || rect.height <= 0) return;

        _canvasSize = new Vector2(rect.width, rect.height);

        // Compute fixture bounding box in fixture space, including half-extents of each node
        // so nodes at the edges aren't clipped by the padding margin.
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var f in _fixtures)
        {
            float hw = f.size.x * 0.5f;
            float hd = f.size.y * 0.5f;
            if (f.position.x - hw < minX) minX = f.position.x - hw;
            if (f.position.x + hw > maxX) maxX = f.position.x + hw;
            if (f.position.y - hd < minY) minY = f.position.y - hd;
            if (f.position.y + hd > maxY) maxY = f.position.y + hd;
        }

        float extentX = maxX - minX;
        float extentY = maxY - minY;

        float usableW = _canvasSize.x - NodePadding * 2f;
        float usableH = _canvasSize.y - NodePadding * 2f;

        // Protect against degenerate (all fixtures at same position) or zero extents.
        _scale = (extentX > 0.001f && extentY > 0.001f)
            ? Mathf.Min(usableW / extentX, usableH / extentY)
            : 50f;

        // Centre of the fixture bounding box maps to centre of canvas.
        float fixtCentreX = (minX + maxX) * 0.5f;
        float fixtCentreY = (minY + maxY) * 0.5f;
        _offset = new Vector2(
            _canvasSize.x * 0.5f - fixtCentreX * _scale,
            _canvasSize.y * 0.5f - fixtCentreY * _scale
        );
    }

    // --- Drawing -----------------------------------------------------

    private void DrawCanvas()
    {
        Rect rect = _canvas.contentRect;
        if (rect.width <= 0 || rect.height <= 0) return;

        if (!Mathf.Approximately(rect.width, _canvasSize.x) ||
            !Mathf.Approximately(rect.height, _canvasSize.y))
            RecalculateLayout();

        if (_fixtures == null || _fixtures.Count == 0)
        {
            DrawEmptyState(rect);
            return;
        }

        HandleMouseEvents(rect);
        DrawFixtures();
    }

    private void HandleMouseEvents(Rect rect)
    {
        Event e = Event.current;
        if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition)) return;
        if (e.button != 0) return;

        // Hit-test in reverse draw order so topmost node wins.
        int hitIndex = -1;
        for (int i = _fixtures.Count - 1; i >= 0; i--)
        {
            var f  = _fixtures[i];
            Vector2 p = FixtureToCanvas(f.position.x, f.position.y);

            float pw  = Mathf.Max(f.size.x * _scale, NodeMinSize) * 0.5f;
            float pd  = Mathf.Max(f.size.y * _scale, NodeMinSize) * 0.5f;
            float dpw = pw + (pd - pw) * _nodeAspectAdjustment;
            float dpd = pd + (pw - pd) * _nodeAspectAdjustment;

            var nodeRect = new Rect(p.x - dpw, p.y - dpd, dpw * 2f, dpd * 2f);
            if (nodeRect.Contains(e.mousePosition)) { hitIndex = i; break; }
        }

        if (hitIndex >= 0)
        {
            var obj = _fixtureObjects[hitIndex];
            if (obj != null)
            {
                if (e.control || e.command || e.shift)
                {
                    // Ctrl/Cmd+click: toggle in selection.
                    // Shift does the same since proper shift-to-select-range doesn't make a lot of sense here.
                    var current = new List<UnityEngine.Object>(Selection.objects);
                    if (current.Contains(obj))
                        current.Remove(obj);
                    else
                        current.Add(obj);
                    Selection.objects = current.ToArray();
                }
                else
                {
                    Selection.activeObject = obj;
                }
            }
        }
        else
        {
            // Click on blank canvas: deselect all.
            Selection.objects = Array.Empty<UnityEngine.Object>();
        }

        e.Use();
    }

    private void DrawEmptyState(Rect rect)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment  = TextAnchor.MiddleCenter,
            normal     = { textColor = new Color(1f, 1f, 1f, 0.25f) },
            fontSize   = 12,
        };
        GUI.Label(rect, "No fixture map loaded.\nUse Load map… to open a FixtureMap.json.", style);
    }

    private void DrawFixtures()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);

        var labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize  = 10,
            normal    = { textColor = _theme.label.ToColor() },
        };

        for (int i = 0; i < _fixtures.Count; i++)
        {
            var f        = _fixtures[i];
            var obj      = _fixtureObjects[i];
            bool selected = obj != null && selectionSet.Contains(obj);

            Vector2 p = FixtureToCanvas(f.position.x, f.position.y);

            // Scale physical dimensions to canvas pixels.
            float pw = Mathf.Max(f.size.x * _scale, NodeMinSize) * 0.5f;
            float pd = Mathf.Max(f.size.y * _scale, NodeMinSize) * 0.5f;

            // Handle extremely tall or wide fixtures by fudging the scale closer to a square.
            float dpw = pw + (pd - pw) * _nodeAspectAdjustment;
            float dpd = pd + (pw - pd) * _nodeAspectAdjustment;

            // Corners: top-left, top-right, bottom-right, bottom-left (clockwise).
            // Canvas Y is flipped relative to fixture space, so depth runs top-to-bottom.
            var corners = new Vector3[]
            {
                new Vector3(p.x - dpw, p.y - dpd),
                new Vector3(p.x + dpw, p.y - dpd),
                new Vector3(p.x + dpw, p.y + dpd),
                new Vector3(p.x - dpw, p.y + dpd),
            };

            Color fill    = selected ? _theme.nodeSelected.ToColor()   : _theme.node.ToColor();
            Color outline = selected ? _theme.nodeOutlineSel.ToColor() : _theme.nodeOutline.ToColor();
            Handles.DrawSolidRectangleWithOutline(corners, fill, outline);

            // Label below node

            if (selected)
            {
                float labelW = Mathf.Max(pw, 120f);
                var labelRect = new Rect(p.x - labelW * 0.5f, p.y + dpd + 2f, labelW, 32f);
                GUI.Label(labelRect, f.name, labelStyle);
            }
        }
    }

    // Converts fixture-space XY to canvas pixel coordinates.
    // Fixture +Y maps to canvas -Y so the stage reads naturally top-to-back.
    private Vector2 FixtureToCanvas(float fx, float fy) => new Vector2(_offset.x + fx * _scale, _offset.y - fy * _scale);

    // --- JSON parsing ------------------------------------------------
        //
        // Unity's JsonUtility can't deserialise a root array, so we wrap it.

        [Serializable]
    private struct FixtureArrayWrapper { public List<FixtureEntry> items; }

    private static List<FixtureEntry> ParseFixtureArray(string json)
    {
        string wrapped = "{\"items\":" + json + "}";
        var wrapper = JsonUtility.FromJson<FixtureArrayWrapper>(wrapped);
        return wrapper.items ?? new List<FixtureEntry>();
    }

    // --- Helpers -----------------------------------------------------

    private static string ToProjectRelative(string absolutePath)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        string absNorm  = absolutePath.Replace('\\', '/');
        if (absNorm.StartsWith(dataPath))
            return "Assets" + absNorm.Substring(dataPath.Length);
        return null;
    }
}
