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

    // --- Viewmodel ---------------------------------------------------

    // Precomputed canvas-space layout for a single fixture node.
    private struct FixtureLayout
    {
        public Vector2 centre;   // canvas-space centre pixel
        public Vector2 halfExt;  // half-width and half-height in pixels
    }

    // Precomputed canvas-space layout for a group bounding box.
    private struct GroupLayout
    {
        public Rect rect;        // canvas-space bounding rect including padding
        public bool valid;       // false if the group has no valid member fixtures
    }

    // --- State -------------------------------------------------------

    private List<FixtureEntry>         _fixtures            = new();
    private List<UnityEngine.Object>   _fixtureObjects      = new();  // resolved scene objects, parallel to _fixtures (null if unresolved)
    private List<UnityEngine.Object>   _fixtureDefinitions  = new();  // resolved FixtureDefinition components
    private List<UnityEngine.Object>   _fixtureDrivers      = new();  // resolved FixtureDriver components
    private List<GroupEntry>           _groups              = new();
    private string                     _mapPath             = "";
    private Theme                      _theme               = Theme.Default();

    // Live update debouncing (30fps = ~33ms per frame)
    private double                     _lastRepaintTime      = 0;
    private const double               RepaintIntervalMs     = 30;

    // Canvas layout — recomputed when fixtures load or the canvas resizes.
    private Vector2               _canvasSize;
    private List<FixtureLayout>   _fixtureLayouts = new();
    private List<GroupLayout>     _groupLayouts   = new();

    // Visual element refs
    private IMGUIContainer _canvas;
    private Label          _pathLabel;

    // Cached IMGUI styles. Initialised once on first draw.
    // GUI.skin not available at CreateGUI time.
    private GUIStyle _nodeLabelStyle;
    private GUIStyle _groupLabelStyle;
    private GUIStyle _emptyStateStyle;

    private void EnsureStyles()
    {
        if (_nodeLabelStyle != null) return;
        _nodeLabelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize  = 10,
            normal    = { textColor = _theme.nodeLabel.ToColor() },
        };
        _groupLabelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize  = 10,
            normal    = { textColor = _theme.groupLabel.ToColor() },
        };
        _emptyStateStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = 12,
            normal    = { textColor = new Color(1f, 1f, 1f, 0.25f) },
        };
    }

    // Node appearance and layout
    // Minimum margin from canvas edge to outermost node centre.
    private const float CanvasPadding = 20f;

    // Pixels of margin around outermost node edges when drawing a group box.
    private const float GroupMargin = 12f;

    // Tunable layout parameters — bound to footer fields.
    private float _minGap                   = 0.1f;
    private float _gapCompressionK          = 2f;
    private float _nodeCompressionK         = 5f;
    private float _nodeCompressionThreshold = 4f;
    private bool  _flipY                    = true;


    // --- Lifecycle ---------------------------------------------------
    [MenuItem("Tools/Fixture Map")]
    private static void Open() => GetWindow<EditorFixtureMap>("Fixture Map");

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now - _lastRepaintTime >= RepaintIntervalMs / 1000.0)
        {
            _lastRepaintTime = now;
            _canvas?.MarkDirtyRepaint();
        }
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

        BindFloatField("min-gap-field",                    _minGap,                   v => _minGap                   = v);
        BindFloatField("gap-compression-k-field",          _gapCompressionK,          v => _gapCompressionK          = v);
        BindFloatField("node-compression-k-field",         _nodeCompressionK,         v => _nodeCompressionK         = v);
        BindFloatField("node-compression-threshold-field", _nodeCompressionThreshold, v => _nodeCompressionThreshold = v);

        var flipYToggle = rootVisualElement.Q<Toggle>("flip-y-toggle");
        flipYToggle.value = _flipY;
        flipYToggle.RegisterValueChangedCallback(e =>
        {
            _flipY = e.newValue;
            RecalculateLayout();
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
        _fixtureDefinitions = new List<UnityEngine.Object>(_fixtures.Count);
        _fixtureDrivers = new List<UnityEngine.Object>(_fixtures.Count);

        // Fast path: find all FixtureDefinition and FixtureDriver components in the scene.
        var allDefinitions = FindObjectsByType<FixtureDefinition>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var allDrivers = FindObjectsByType<FixtureDriver>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // Build lookup by GameObject for fast matching.
        var definitionsByGameObject = new Dictionary<GameObject, FixtureDefinition>(allDefinitions.Length);
        var driversByGameObject = new Dictionary<GameObject, FixtureDriver>(allDrivers.Length);

        foreach (var def in allDefinitions)
            definitionsByGameObject[def.gameObject] = def;
        foreach (var drv in allDrivers)
            driversByGameObject[drv.gameObject] = drv;

        // Match fixtures by resolving their scene object reference.
        for (int i = 0; i < _fixtures.Count; i++)
        {
            var f = _fixtures[i];
            UnityEngine.Object sceneObj = null;

            if (!string.IsNullOrEmpty(f.sceneObject) &&
                GlobalObjectId.TryParse(f.sceneObject, out GlobalObjectId gid))
                sceneObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);

            _fixtureObjects.Add(sceneObj);

            // Match components by the scene object's GameObject.
            FixtureDefinition def = null;
            FixtureDriver drv = null;

            if (sceneObj is GameObject go)
            {
                definitionsByGameObject.TryGetValue(go, out def);
                driversByGameObject.TryGetValue(go, out drv);
            }

            _fixtureDefinitions.Add(def);
            _fixtureDrivers.Add(drv);
        }
    }

    // --- Layout ------------------------------------------------------

    private void RecalculateLayout()
    {
        if (_fixtures == null || _fixtures.Count == 0) return;

        Rect rect = _canvas.contentRect;
        if (rect.width <= 0 || rect.height <= 0) return;

        _canvasSize = new Vector2(rect.width, rect.height);

        // Build sorted lists of unique X and Y world positions.
        var uniqueX = new List<float>();
        var uniqueY = new List<float>();
        foreach (var f in _fixtures)
        {
            if (!uniqueX.Contains(f.position.x)) uniqueX.Add(f.position.x);
            if (!uniqueY.Contains(f.position.y)) uniqueY.Add(f.position.y);
        }
        uniqueX.Sort();
        uniqueY.Sort();

        int cols = uniqueX.Count;
        int rows = uniqueY.Count;

        // Generate node sizes from fixture data, keeping raw world sizes for gap calculation.
        var worldSizesX = new float[cols];
        var worldSizesY = new float[rows];
        var nodeSizesX  = new float[cols];
        var nodeSizesY  = new float[rows];
        foreach (var f in _fixtures)
        {
            int xi = uniqueX.IndexOf(f.position.x);
            int yi = uniqueY.IndexOf(f.position.y);
            worldSizesX[xi] = f.size.x;
            worldSizesY[yi] = f.size.y;
            var nodeSize = CompressNodeSize(f.size.x, f.size.y, _nodeCompressionThreshold, _nodeCompressionK);
            nodeSizesX[xi] = nodeSize.x;
            nodeSizesY[yi] = nodeSize.y;
        }

        // Sweep each axis independently using real world positions and fixture dimensions.
        var centresX = SweepAxis(uniqueX.ToArray(), worldSizesX, nodeSizesX, _minGap, _gapCompressionK);
        var centresY = SweepAxis(uniqueY.ToArray(), worldSizesY, nodeSizesY, _minGap, _gapCompressionK);

        // Bounding box derived from sweep results.
        float layoutMinX = centresX[0]        - nodeSizesX[0]        * 0.5f;
        float layoutMaxX = centresX[cols - 1] + nodeSizesX[cols - 1] * 0.5f;
        float layoutMinY = centresY[0]        - nodeSizesY[0]        * 0.5f;
        float layoutMaxY = centresY[rows - 1] + nodeSizesY[rows - 1] * 0.5f;
        float spanX = layoutMaxX - layoutMinX;
        float spanY = layoutMaxY - layoutMinY;

        // Fit the layout uniformly into the canvas, respecting CanvasPadding.
        float usableW = _canvasSize.x - CanvasPadding * 2f;
        float usableH = _canvasSize.y - CanvasPadding * 2f;
        float scale   = Mathf.Min(usableW / spanX, usableH / spanY);

        // Centre the layout on the canvas.
        float offsetX = _canvasSize.x * 0.5f - (layoutMinX + spanX * 0.5f) * scale;
        float offsetY = _canvasSize.y * 0.5f - (layoutMinY + spanY * 0.5f) * scale;

        _fixtureLayouts = new List<FixtureLayout>(_fixtures.Count);
        foreach (var f in _fixtures)
        {
            int xi = uniqueX.IndexOf(f.position.x);
            int yi = uniqueY.IndexOf(f.position.y);

            int yiFlipped = _flipY ? rows - 1 - yi : yi;

            float px = offsetX + centresX[xi] * scale;
            float py = offsetY + centresY[yiFlipped] * scale;

            _fixtureLayouts.Add(new FixtureLayout
            {
                centre  = new Vector2(px, py),
                halfExt = new Vector2(
                    nodeSizesX[xi] * 0.5f * scale,
                    nodeSizesY[yi] * 0.5f * scale
                ),
            });
        }

        // --- Group viewmodel ---
        _groupLayouts = new List<GroupLayout>(_groups.Count);
        foreach (var g in _groups)
        {
            if (g.fixtures == null || g.fixtures.Count == 0)
            {
                _groupLayouts.Add(new GroupLayout { valid = false });
                continue;
            }

            float gMinX = float.MaxValue, gMaxX = float.MinValue;
            float gMinY = float.MaxValue, gMaxY = float.MinValue;
            bool any = false;

            foreach (int fi in g.fixtures)
            {
                if (fi < 0 || fi >= _fixtureLayouts.Count) continue;
                var fl = _fixtureLayouts[fi];
                if (fl.centre.x - fl.halfExt.x < gMinX) gMinX = fl.centre.x - fl.halfExt.x;
                if (fl.centre.x + fl.halfExt.x > gMaxX) gMaxX = fl.centre.x + fl.halfExt.x;
                if (fl.centre.y - fl.halfExt.y < gMinY) gMinY = fl.centre.y - fl.halfExt.y;
                if (fl.centre.y + fl.halfExt.y > gMaxY) gMaxY = fl.centre.y + fl.halfExt.y;
                any = true;
            }

            if (!any)
            {
                _groupLayouts.Add(new GroupLayout { valid = false });
                continue;
            }

            _groupLayouts.Add(new GroupLayout
            {
                valid = true,
                rect  = new Rect(
                    gMinX - GroupMargin,
                    gMinY - GroupMargin,
                    (gMaxX - gMinX) + GroupMargin * 2f,
                    (gMaxY - gMinY) + GroupMargin * 2f
                ),
            });
        }
    }

    // --- Drawing -----------------------------------------------------

    private void DrawCanvas()
    {
        Rect rect = _canvas.contentRect;
        if (rect.width <= 0 || rect.height <= 0) return;

        EnsureStyles();

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
        for (int i = _fixtureLayouts.Count - 1; i >= 0; i--)
        {
            var fl = _fixtureLayouts[i];
            if (new Rect(fl.centre.x - fl.halfExt.x, fl.centre.y - fl.halfExt.y,
                         fl.halfExt.x * 2f, fl.halfExt.y * 2f).Contains(e.mousePosition))
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
            // No fixture hit: check group bounding boxes.
            int groupHit = -1;
            for (int gi = _groupLayouts.Count - 1; gi >= 0; gi--)
            {
                var gl = _groupLayouts[gi];
                if (gl.valid && gl.rect.Contains(e.mousePosition))
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

    private void DrawEmptyState(Rect rect)
    {
        GUI.Label(rect, "No fixture map loaded.\nUse Load map… to open a FixtureMap.json.", _emptyStateStyle);
    }

    private void DrawGroups()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);

        for (int gi = 0; gi < _groups.Count; gi++)
        {
            var gl = _groupLayouts[gi];
            if (!gl.valid) continue;
            Rect r = gl.rect;

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
                GUI.Label(labelRect, g.name, _groupLabelStyle);
            }
        }
    }

    private void DrawFixtures()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);

        for (int i = 0; i < _fixtures.Count; i++)
        {
            var f = _fixtures[i];
            var fl = _fixtureLayouts[i];
            var obj = _fixtureObjects[i];
            var definition = _fixtureDefinitions[i];
            var driver = _fixtureDrivers[i];
            bool selected = obj != null && selectionSet.Contains(obj);

            DrawFixtureNode(f, fl, definition, driver, selected);
        }
    }

    private void DrawFixtureNode(FixtureEntry fixture, FixtureLayout layout, UnityEngine.Object definition, UnityEngine.Object driver, bool selected)
    {
        Vector2 p = layout.centre;
        float dpw = layout.halfExt.x;
        float dpd = layout.halfExt.y;

        // Corners: top-left, top-right, bottom-right, bottom-left (clockwise).
        var corners = new Vector3[]
        {
            new(p.x - dpw, p.y - dpd),
            new(p.x + dpw, p.y - dpd),
            new(p.x + dpw, p.y + dpd),
            new(p.x - dpw, p.y + dpd),
        };

        Color fill = selected ? _theme.nodeFill_Active.ToColor() : _theme.nodeFill.ToColor();
        Color outline = selected ? _theme.nodeOutline_Active.ToColor() : _theme.nodeOutline.ToColor();
        Handles.DrawSolidRectangleWithOutline(corners, fill, outline);

        if (selected)
        {
            float labelW = Mathf.Max(dpw, 120f);
            var labelRect = new Rect(p.x - labelW * 0.5f, p.y + dpd + 2f, labelW, 32f);
            GUI.Label(labelRect, fixture.name, _nodeLabelStyle);
        }

        if (driver == null || definition == null) return;

        var driverTyped = (FixtureDriver)driver;
        var definitionTyped = (FixtureDefinition)definition;

        // Inner rectangle visualising luminaire state
        float rotationX = 0.5f;
        if (definitionTyped.Profile != null && definitionTyped.Profile.AxisX.Enabled && driverTyped.Head != null)
            rotationX = GetNormalizedAxisRotation(driverTyped.Head, definitionTyped.Profile.AxisX, 0);

        float rotationZ = 0.5f;
        if (definitionTyped.Profile != null && definitionTyped.Profile.AxisZ.Enabled && driverTyped.Head != null)
            rotationZ = GetNormalizedAxisRotation(driverTyped.Head, definitionTyped.Profile.AxisZ, 2);

        // Resolve emission color, handling blackbody mode.
        Color emissionColor = definitionTyped.EmissionColor;
        if (definitionTyped.Colour == FixtureDefinition.ColourMode.Blackbody)
            emissionColor = FixtureDefinition.BlackbodyToRGB(definitionTyped.ColourTemperature);

        // Get brightness normalized to max brightness (PropsTransform.localScale.x in linear space).
        float brightness = 0f;
        if (driverTyped.PropsTransform != null)
            brightness = Mathf.InverseLerp(0f, definitionTyped.Profile.BrightnessMax, driverTyped.PropsTransform.localScale.x);

        // Fill colour has alpha modulated by brightness; outline is always opaque.
        Color fillColor = emissionColor;
        fillColor.a = brightness;
        Color outlineColor = emissionColor;
        outlineColor.a = 1f;

        float padding = 12f;
        var innerCorners = new Vector3[]
        {
            new(corners[0].x + padding * rotationZ, corners[0].y + padding * rotationX),
            new(corners[1].x - padding * rotationZ, corners[1].y + padding * rotationX),
            new(corners[2].x - padding * (1 - rotationZ), corners[2].y - padding * (1 - rotationX)),
            new(corners[3].x + padding * (1 - rotationZ), corners[3].y - padding * (1 - rotationX))
        };

        Handles.DrawSolidRectangleWithOutline(innerCorners, fillColor, outlineColor);
    }

    private float GetNormalizedAxisRotation(Transform head, FixtureProfile.RotationAxis axis, int axisComponent)
    {
        float currentAngle = head.localEulerAngles[axisComponent];
        currentAngle = NormalizeAngle(currentAngle);
        return Mathf.InverseLerp(axis.Min, axis.Max, currentAngle);
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }

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

    // --- Mapping functions -----------------------------------------------


    // Compresses a distance, preserving values up to minDistance and soft-capping beyond it.
    // k controls compression strength: 0 = passthrough, higher = more aggressive.
    // The compressed value asymptotically approaches minDistance + 1/k for large distances.
    private static float CompressDistance(float distance, float minDistance, float k)
    {
        if (distance <= minDistance) return minDistance;
        float excess = distance - minDistance;
        return minDistance + excess / (1f + excess * k);
    }

    // Maps a fixture's raw world-space dimensions to layout-space node dimensions.
    // When a fixture is extremely narrow it can become difficult to click in the diagram.
    // This processor handles particularly long and narrow fixtures by crushing their long
    // axis past a certain point.
    private static Vector2 CompressNodeSize(float sizeX, float sizeY, float minAspect, float k)
    {
        float longAxis  = Mathf.Max(sizeX, sizeY);
        float shortAxis = Mathf.Min(sizeX, sizeY);

        // Don't handle cases where the ratio isn't too extreme.
        if (longAxis / shortAxis <= minAspect) return new(sizeX, sizeY);

        // For extreme aspect ratios, compress the long axis past the threshold.
        float compressed = CompressDistance(longAxis, shortAxis * minAspect, k);

        // Reconstruct with the same orientation as the input.
        return sizeX >= sizeY ? new(compressed, shortAxis) : new(shortAxis, compressed);
    }

    // Returns layout-space centre positions for n nodes swept along one axis.
    // worldPositions[i] is the world coordinate of node i (sorted ascending).
    // worldSizes[i] is the raw world-space size, used to compute edge-to-edge gaps.
    // nodeSizes[i] is the layout-space size (may differ from worldSizes after compression).
    // The gap between adjacent nodes is derived from world geometry, then compressed.
    private static float[] SweepAxis(float[] worldPositions, float[] worldSizes, float[] nodeSizes, float minGap, float k)
    {
        int n = nodeSizes.Length;
        var centres = new float[n];
        float cursor = 0f;
        for (int i = 0; i < n; i++)
        {
            centres[i] = cursor + nodeSizes[i] * 0.5f;
            cursor += nodeSizes[i];
            if (i < n - 1)
            {
                // Edge-to-edge gap in world space, using raw sizes to avoid compression artifacts.
                float worldGap = worldPositions[i + 1] - worldPositions[i]
                                 - worldSizes[i] * 0.5f - worldSizes[i + 1] * 0.5f;
                cursor += CompressDistance(worldGap, minGap, k);
            }
        }
        return centres;
    }

    private void BindFloatField(string name, float initialValue, Action<float> setter)
    {
        var field = rootVisualElement.Q<FloatField>(name);
        if (field == null) return;
        field.value = initialValue;
        field.RegisterValueChangedCallback(e =>
        {
            setter(e.newValue);
            RecalculateLayout();
            _canvas.MarkDirtyRepaint();
        });
    }

    private static string ToProjectRelative(string absolutePath)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        string absNorm  = absolutePath.Replace('\\', '/');
        if (absNorm.StartsWith(dataPath))
            return "Assets" + absNorm.Substring(dataPath.Length);
        return null;
    }
}
