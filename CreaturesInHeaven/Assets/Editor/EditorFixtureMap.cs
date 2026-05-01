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
    private struct GroupEntry
    {
        public string     name;
        public string     sceneObject;
        public List<int>  fixtures;  // indices into the fixtures array
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
        public ThemeColour nodeFill;
        public ThemeColour nodeOutline;
        public ThemeColour nodeFill_Active;
        public ThemeColour nodeOutline_Active;
        public ThemeColour nodeLabel;
        public ThemeColour groupFill;
        public ThemeColour groupOutline;
        public ThemeColour groupFill_Active;
        public ThemeColour groupOutline_Active;
        public ThemeColour groupLabel;

        public static Theme Default() => new Theme
        {
            nodeFill = new ThemeColour { r = 0.90f, g = 0.85f, b = 1.00f, a = 0.50f },
            nodeOutline = new ThemeColour { r = 1.00f, g = 1.00f, b = 1.00f, a = 1.00f },
            nodeFill_Active = new ThemeColour { r = 0.90f, g = 0.75f, b = 1.00f, a = 0.85f },
            nodeOutline_Active = new ThemeColour { r = 0.80f, g = 0.60f, b = 1.00f, a = 1.00f },
            nodeLabel = new ThemeColour { r = 1.00f, g = 1.00f, b = 1.00f, a = 0.75f },
            groupFill = new ThemeColour { r = 0.90f, g = 0.85f, b = 1.00f, a = 0.50f },
            groupOutline = new ThemeColour { r = 1.00f, g = 1.00f, b = 1.00f, a = 1.00f },
            groupFill_Active = new ThemeColour { r = 0.90f, g = 0.75f, b = 1.00f, a = 0.85f },
            groupOutline_Active = new ThemeColour { r = 0.80f, g = 0.60f, b = 1.00f, a = 1.00f },
            groupLabel = new ThemeColour { r = 1.00f, g = 1.00f, b = 1.00f, a = 0.75f },
        };
    }

    private const string ThemePath = "Assets/Editor/EditorFixtureMapTheme.json";

    // --- State -------------------------------------------------------

    private List<FixtureEntry>         _fixtures       = new();
    private List<UnityEngine.Object>   _fixtureObjects = new();  // resolved scene objects, parallel to _fixtures (null if unresolved)
    private List<GroupEntry>           _groups         = new();
    private List<UnityEngine.Object>   _groupObjects   = new();  // resolved scene objects, parallel to _groups (null if unresolved)
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
            ParseMap(json, out _fixtures, out _groups);
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

        _groupObjects = new List<UnityEngine.Object>(_groups.Count);
        foreach (var g in _groups)
        {
            UnityEngine.Object obj = null;
            if (!string.IsNullOrEmpty(g.sceneObject) &&
                GlobalObjectId.TryParse(g.sceneObject, out GlobalObjectId gid))
                obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            _groupObjects.Add(obj);
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
        DrawGroups();
        DrawFixtures();
    }

    private void HandleMouseEvents(Rect rect)
    {
        Event e = Event.current;
        if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition)) return;
        if (e.button != 0) return;

        bool additive = e.control || e.command || e.shift;

        // Fixtures have priority: hit-test in reverse draw order so topmost wins.
        int fixtureHit = -1;
        for (int i = _fixtures.Count - 1; i >= 0; i--)
        {
            var f = _fixtures[i];
            Vector2 p = FixtureToCanvas(f.position.x, f.position.y);
            float pw  = Mathf.Max(f.size.x * _scale, NodeMinSize) * 0.5f;
            float pd  = Mathf.Max(f.size.y * _scale, NodeMinSize) * 0.5f;
            float dpw = pw + (pd - pw) * _nodeAspectAdjustment;
            float dpd = pd + (pw - pd) * _nodeAspectAdjustment;
            if (new Rect(p.x - dpw, p.y - dpd, dpw * 2f, dpd * 2f).Contains(e.mousePosition))
                { fixtureHit = i; break; }
        }

        if (fixtureHit >= 0)
        {
            var obj = _fixtureObjects[fixtureHit];
            if (obj != null)
                ToggleOrSet(obj, additive);
        }
        else
        {
            // No fixture hit — check group bounding boxes.
            int groupHit = -1;
            for (int gi = _groups.Count - 1; gi >= 0; gi--)
            {
                if (GroupCanvasRect(gi, out Rect groupRect) && groupRect.Contains(e.mousePosition))
                    { groupHit = gi; break; }
            }

            if (groupHit >= 0)
            {
                // Collect the resolved scene objects for all member fixtures.
                var members = new List<UnityEngine.Object>();
                foreach (int fi in _groups[groupHit].fixtures)
                    if (fi >= 0 && fi < _fixtureObjects.Count && _fixtureObjects[fi] != null)
                        members.Add(_fixtureObjects[fi]);

                if (members.Count > 0)
                {
                    if (additive)
                    {
                        // Determine whether the group is currently fully selected; if so, remove all members.
                        var current = new HashSet<UnityEngine.Object>(Selection.objects);
                        bool allSelected = members.TrueForAll(m => current.Contains(m));
                        if (allSelected)
                            foreach (var m in members) current.Remove(m);
                        else
                            foreach (var m in members) current.Add(m);
                        Selection.objects = new List<UnityEngine.Object>(current).ToArray();
                    }
                    else
                    {
                        Selection.objects = members.ToArray();
                    }
                }
            }
            else
            {
                // Click on blank canvas: deselect all.
                Selection.objects = Array.Empty<UnityEngine.Object>();
            }
        }

        e.Use();
    }

    private void ToggleOrSet(UnityEngine.Object obj, bool additive)
    {
        if (additive)
        {
            var current = new List<UnityEngine.Object>(Selection.objects);
            if (current.Contains(obj)) current.Remove(obj);
            else current.Add(obj);
            Selection.objects = current.ToArray();
        }
        else
        {
            Selection.activeObject = obj;
        }
    }

    // Computes the canvas-space bounding rect for a group from its member fixture positions.
    // Returns false if the group has no valid members.
    private bool GroupCanvasRect(int groupIndex, out Rect canvasRect)
    {
        const float GroupPadding = 12f;  // pixels of padding around the outermost node edges
        canvasRect = default;
        var g = _groups[groupIndex];
        if (g.fixtures == null || g.fixtures.Count == 0) return false;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        bool any = false;

        foreach (int fi in g.fixtures)
        {
            if (fi < 0 || fi >= _fixtures.Count) continue;
            var f = _fixtures[fi];
            Vector2 p = FixtureToCanvas(f.position.x, f.position.y);
            float pw  = Mathf.Max(f.size.x * _scale, NodeMinSize) * 0.5f;
            float pd  = Mathf.Max(f.size.y * _scale, NodeMinSize) * 0.5f;
            float dpw = pw + (pd - pw) * _nodeAspectAdjustment;
            float dpd = pd + (pw - pd) * _nodeAspectAdjustment;
            if (p.x - dpw < minX) minX = p.x - dpw;
            if (p.x + dpw > maxX) maxX = p.x + dpw;
            if (p.y - dpd < minY) minY = p.y - dpd;
            if (p.y + dpd > maxY) maxY = p.y + dpd;
            any = true;
        }

        if (!any) return false;
        canvasRect = new Rect(minX - GroupPadding, minY - GroupPadding,
                              (maxX - minX) + GroupPadding * 2f, (maxY - minY) + GroupPadding * 2f);
        return true;
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

    private void DrawGroups()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);

        var labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize  = 10,
            normal    = { textColor = _theme.groupLabel.ToColor() },
        };

        for (int gi = 0; gi < _groups.Count; gi++)
        {
            if (!GroupCanvasRect(gi, out Rect r)) continue;

            // A group is "selected" when all its member fixtures are in the selection.
            // In reality the actual FixtureGroup object in the scene isn't ever selected.
            var g = _groups[gi];
            bool selected = g.fixtures != null && g.fixtures.Count > 0 &&
                            g.fixtures.TrueForAll(fi =>
                                fi >= 0 && fi < _fixtureObjects.Count &&
                                _fixtureObjects[fi] != null &&
                                selectionSet.Contains(_fixtureObjects[fi]));

            Color fill    = selected ? _theme.groupFill_Active.ToColor()   : _theme.groupFill.ToColor();
            Color outline = selected ? _theme.groupOutline_Active.ToColor() : _theme.groupOutline.ToColor();

            var corners = new Vector3[]
            {
                new Vector3(r.xMin, r.yMin),
                new Vector3(r.xMax, r.yMin),
                new Vector3(r.xMax, r.yMax),
                new Vector3(r.xMin, r.yMax),
            };
            Handles.DrawSolidRectangleWithOutline(corners, fill, outline);

            if (selected)
            {
                float labelW = Mathf.Max(r.width, 120f);
                var labelRect = new Rect(r.center.x - labelW * 0.5f, r.yMax + 2f, labelW, 32f);
                GUI.Label(labelRect, g.name, labelStyle);
            }
        }
    }

    private void DrawFixtures()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);

        var labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize  = 10,
            normal    = { textColor = _theme.nodeLabel.ToColor() },
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

            Color fill    = selected ? _theme.nodeFill_Active.ToColor()   : _theme.nodeFill.ToColor();
            Color outline = selected ? _theme.nodeOutline_Active.ToColor() : _theme.nodeOutline.ToColor();
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

    [Serializable]
    private struct MapWrapper
    {
        public List<FixtureEntry> items;
        public List<GroupEntry>   groups;
    }

    private static void ParseMap(string json, out List<FixtureEntry> fixtures, out List<GroupEntry> groups)
    {
        var wrapper = JsonUtility.FromJson<MapWrapper>(json);
        fixtures = wrapper.items  ?? new List<FixtureEntry>();
        groups   = wrapper.groups ?? new List<GroupEntry>();
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
