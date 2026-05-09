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
    private struct SelectionGroup
    {
        public string    name;
        public List<int> fixtures;
    }

    [Serializable]
    private struct SelectionGroupFile
    {
        public List<SelectionGroup> groups;
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
            nodeFill = CreateThemeColour(0.90f, 0.85f, 1.00f, 0.50f),
            nodeOutline = CreateThemeColour(1.00f, 1.00f, 1.00f, 1.00f),
            nodeFill_Active = CreateThemeColour(0.90f, 0.75f, 1.00f, 0.85f),
            nodeOutline_Active = CreateThemeColour(0.80f, 0.60f, 1.00f, 1.00f),
            nodeLabel = CreateThemeColour(1.00f, 1.00f, 1.00f, 0.75f),
            groupFill = CreateThemeColour(0.90f, 0.85f, 1.00f, 0.50f),
            groupOutline = CreateThemeColour(1.00f, 1.00f, 1.00f, 1.00f),
            groupFill_Active = CreateThemeColour(0.90f, 0.75f, 1.00f, 0.85f),
            groupOutline_Active = CreateThemeColour(0.80f, 0.60f, 1.00f, 1.00f),
            groupLabel = CreateThemeColour(1.00f, 1.00f, 1.00f, 0.75f),
        };

        private static ThemeColour CreateThemeColour(float r, float g, float b, float a)
            => new ThemeColour { r = r, g = g, b = b, a = a };
    }

    private const string ThemePath = "Assets/Editor/EditorFixtureMapTheme.json";

    // --- Viewmodel ---------------------------------------------------

    // Precomputed viewmodel layout for a single fixture node.
    private struct FixtureLayout
    {
        public Vector2 centre;   // viewmodel-space centre
        public Vector2 halfExt;  // half-width and half-height in viewmodel space
    }

    // Precomputed viewmodel layout for a group bounding box.
    private struct GroupLayout
    {
        public Rect rect;        // viewmodel-space bounding rect including padding
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

    // Logical layout — recomputed when fixtures load or layout parameters change.
    private List<FixtureLayout>   _fixtureLayouts = new();
    private List<GroupLayout>     _groupLayouts   = new();
    private Rect                  _logicalBounds  = new();  // bounding rect in logical space

    // Viewport — recomputed when canvas resizes. Transforms logical space to screen space.
    private Vector2               _canvasSize;
    private float                 _logicalScale   = 1f;  // scale factor from logical to screen space
    private Vector2               _logicalOffset  = Vector2.zero;  // screen offset of logical origin

    // Pan and zoom
    private float                 _zoom           = 1f;
    private Vector2               _panOffset      = Vector2.zero;
    private bool                  _isPanning      = false;

    // Visual element refs
    private IMGUIContainer _canvas;
    private Label          _pathLabel;

    // Selection options state
    private bool _includeMainFixture = true;
    private bool _includeFixtureHead = true;
    private bool _includePropsTransform = true;

    // Selection groups state
    private List<SelectionGroup> _selectionGroups    = new();
    private int                  _selectedGroupIndex = -1;
    private bool                 _sgRenaming         = false;

    // Selection groups UI refs
    private ScrollView _sgList;
    private TextField  _sgSearchField;
    private string     _sgSearchText = "";
    private Button     _sgCreateBtn, _sgRenameBtn, _sgDeleteBtn;
    private Button     _sgAddBtn, _sgRemoveBtn;

    // Cached IMGUI styles. Initialised once on first draw.
    // GUI.skin not available at CreateGUI time.
    private GUIStyle _nodeLabelStyle;
    private GUIStyle _groupLabelStyle;
    private GUIStyle _emptyStateStyle;

    private void EnsureStyles()
    {
        if (_nodeLabelStyle != null) return;
        _nodeLabelStyle = CreateLabelStyle(TextAnchor.UpperCenter, 10, _theme.nodeLabel.ToColor());
        _groupLabelStyle = CreateLabelStyle(TextAnchor.UpperCenter, 10, _theme.groupLabel.ToColor());
        _emptyStateStyle = CreateLabelStyle(TextAnchor.MiddleCenter, 12, new Color(1f, 1f, 1f, 0.25f));
    }

    private static GUIStyle CreateLabelStyle(TextAnchor alignment, int fontSize, Color textColor)
    {
        return new GUIStyle(GUI.skin.label)
        {
            alignment = alignment,
            fontSize = fontSize,
            normal = { textColor = textColor },
        };
    }

    // Node appearance and layout
    // Minimum margin from canvas edge to outermost node centre.
    private const float CanvasPadding = 20f;

    // Margin around outermost node edges when drawing a group box.
    private const float GroupMargin = 0.1f;

    // Selection groups file extension
    private string SelectionsPath => string.IsNullOrEmpty(_mapPath) ? null : _mapPath + ".selections.json";

    // Tunable layout parameters — bound to footer fields.
    private float _minGap                   = 0.1f;
    private float _gapCompressionK          = 4f;
    private float _nodeCompressionK         = 5f;
    private float _nodeCompressionThreshold = 4f;
    private bool  _flipY                    = false;


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
        UpdateSgButtons();
        RefreshSgListSelectionStates();
    }

    public void CreateGUI()
    {
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/EditorFixtureMap.uxml");
        uxml.CloneTree(rootVisualElement);

        _pathLabel = rootVisualElement.Q<Label>("map-path-label");

        _canvas = rootVisualElement.Q<IMGUIContainer>("canvas");
        _canvas.onGUIHandler = DrawCanvas;
        _canvas.RegisterCallback<GeometryChangedEvent>(_ => UpdateViewport());

        rootVisualElement.Q<Button>("load-btn").clicked   += PromptLoad;
        rootVisualElement.Q<Button>("reload-btn").clicked += Reload;

        var viewSettingsPanel = rootVisualElement.Q<VisualElement>("view-settings-panel");
        rootVisualElement.Q<Button>("view-settings-btn").clicked += () =>
            viewSettingsPanel.EnableInClassList("visible", !viewSettingsPanel.ClassListContains("visible"));
        rootVisualElement.Q<Button>("view-settings-close-btn").clicked += () =>
            viewSettingsPanel.RemoveFromClassList("visible");

        BindFloatField("min-gap-field",                    _minGap,                   v => _minGap                   = v);
        BindFloatField("gap-compression-k-field",          _gapCompressionK,          v => _gapCompressionK          = v);
        BindFloatField("node-compression-k-field",         _nodeCompressionK,         v => _nodeCompressionK         = v);
        BindFloatField("node-compression-threshold-field", _nodeCompressionThreshold, v => _nodeCompressionThreshold = v);

        var flipYToggle = rootVisualElement.Q<Toggle>("flip-y-toggle");
        flipYToggle.value = _flipY;
        flipYToggle.RegisterValueChangedCallback(e =>
        {
            _flipY = e.newValue;
            ComputeLogicalLayout();
            UpdateViewport();
            _canvas.MarkDirtyRepaint();
        });

        // Wire selection options toggles — all default to true
        var mainFixtureToggle = rootVisualElement.Q<Toggle>("include-main-fixture-toggle");
        mainFixtureToggle.value = _includeMainFixture;
        mainFixtureToggle.RegisterValueChangedCallback(e => _includeMainFixture = e.newValue);

        var fixtureHeadToggle = rootVisualElement.Q<Toggle>("include-fixture-head-toggle");
        fixtureHeadToggle.value = _includeFixtureHead;
        fixtureHeadToggle.RegisterValueChangedCallback(e => _includeFixtureHead = e.newValue);

        var propsTransformToggle = rootVisualElement.Q<Toggle>("include-props-transform-toggle");
        propsTransformToggle.value = _includePropsTransform;
        propsTransformToggle.RegisterValueChangedCallback(e => _includePropsTransform = e.newValue);

        // Wire selection groups panel
        _sgList        = rootVisualElement.Q<ScrollView>("sg-list");
        _sgSearchField = rootVisualElement.Q<TextField>("sg-search");
        _sgCreateBtn   = rootVisualElement.Q<Button>("sg-create-btn");
        _sgRenameBtn   = rootVisualElement.Q<Button>("sg-rename-btn");
        _sgDeleteBtn   = rootVisualElement.Q<Button>("sg-delete-btn");
        _sgAddBtn      = rootVisualElement.Q<Button>("sg-add-btn");
        _sgRemoveBtn   = rootVisualElement.Q<Button>("sg-remove-btn");

        _sgSearchField.RegisterValueChangedCallback(e =>
        {
            _sgSearchText = e.newValue;
            RefreshSgList();
        });

        _sgCreateBtn.clicked   += OnSgCreate;
        _sgRenameBtn.clicked   += OnSgRename;
        _sgDeleteBtn.clicked   += OnSgDelete;
        _sgAddBtn.clicked      += OnSgAddFixtures;
        _sgRemoveBtn.clicked   += OnSgRemoveFixtures;

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

            _zoom = 1f;
            _panOffset = Vector2.zero;
            ComputeLogicalLayout();
            UpdateViewport();

            LoadSelectionGroups();
            RefreshSgList();

            _canvas.MarkDirtyRepaint();
        }
        catch (Exception ex)
        {
            Debug.LogError($"  [EditorFixtureMap] Failed to load {absolutePath}: {ex.Message}");
        }
    }

    private void SaveSelectionGroups()
    {
        string path = SelectionsPath;
        if (path == null) return;
        try { File.WriteAllText(path, JsonUtility.ToJson(new SelectionGroupFile { groups = _selectionGroups }, prettyPrint: true)); }
        catch (Exception ex) { Debug.LogWarning($"[EditorFixtureMap] Could not save selections: {ex.Message}"); }
    }

    private void LoadSelectionGroups()
    {
        _selectionGroups    = new();
        _selectedGroupIndex = -1;
        string path = SelectionsPath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            var file = JsonUtility.FromJson<SelectionGroupFile>(File.ReadAllText(path));
            _selectionGroups = file.groups ?? new();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EditorFixtureMap] Could not load selections: {ex.Message}");
        }
    }

    private void ResolveSceneObjects()
    {
        _fixtureObjects = new List<UnityEngine.Object>(_fixtures.Count);
        _fixtureDefinitions = new List<UnityEngine.Object>(_fixtures.Count);
        _fixtureDrivers = new List<UnityEngine.Object>(_fixtures.Count);

        // Find all FixtureDefinition and FixtureDriver components in the scene.
        var allDefinitions = FindObjectsByType<FixtureDefinition>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var allDrivers = FindObjectsByType<FixtureDriver>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // Build lookup by GameObject.
        var definitionsByGameObject = new Dictionary<GameObject, FixtureDefinition>(allDefinitions.Length);
        var driversByGameObject = new Dictionary<GameObject, FixtureDriver>(allDrivers.Length);

        foreach (var def in allDefinitions)
            definitionsByGameObject[def.gameObject] = def;
        foreach (var drv in allDrivers)
            driversByGameObject[drv.gameObject] = drv;

        // Build a map of GlobalObjectId to sceneObject via reverse lookup from known objects.
        // This avoids slow GlobalObjectIdentifierToObjectSlow calls entirely.
        var globalIdCache = new Dictionary<string, UnityEngine.Object>();

        // Cache all FixtureDefinition scene objects by their GlobalObjectId string.
        foreach (var def in allDefinitions)
        {
            string gidStr = GlobalObjectId.GetGlobalObjectIdSlow(def.gameObject).ToString();
            if (!globalIdCache.ContainsKey(gidStr))
                globalIdCache[gidStr] = def.gameObject;
        }

        // Also cache FixtureDriver scene objects.
        foreach (var drv in allDrivers)
        {
            string gidStr = GlobalObjectId.GetGlobalObjectIdSlow(drv.gameObject).ToString();
            if (!globalIdCache.ContainsKey(gidStr))
                globalIdCache[gidStr] = drv.gameObject;
        }

        // Match fixtures by looking up their GlobalObjectId string in the cache.
        for (int i = 0; i < _fixtures.Count; i++)
        {
            var f = _fixtures[i];
            UnityEngine.Object sceneObj = null;

            if (!string.IsNullOrEmpty(f.sceneObject) && globalIdCache.TryGetValue(f.sceneObject, out var cached))
                sceneObj = cached;

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

    // Recompute logical layout from fixture data. Called when fixtures or parameters change.
    private void ComputeLogicalLayout()
    {
        if (_fixtures == null || _fixtures.Count == 0) return;

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

        // Compute logical-space bounding box.
        float layoutMinX = centresX[0]        - nodeSizesX[0]        * 0.5f;
        float layoutMaxX = centresX[cols - 1] + nodeSizesX[cols - 1] * 0.5f;
        float layoutMinY = centresY[0]        - nodeSizesY[0]        * 0.5f;
        float layoutMaxY = centresY[rows - 1] + nodeSizesY[rows - 1] * 0.5f;
        _logicalBounds = new Rect(layoutMinX, layoutMinY, layoutMaxX - layoutMinX, layoutMaxY - layoutMinY);

        // Create fixture layouts in logical space (no viewport scaling).
        _fixtureLayouts = new List<FixtureLayout>(_fixtures.Count);
        foreach (var f in _fixtures)
        {
            int xi = uniqueX.IndexOf(f.position.x);
            int yi = uniqueY.IndexOf(f.position.y);
            int yiFlipped = _flipY ? rows - 1 - yi : yi;

            _fixtureLayouts.Add(new FixtureLayout
            {
                centre  = new Vector2(centresX[xi], centresY[yiFlipped]),
                halfExt = new Vector2(nodeSizesX[xi] * 0.5f, nodeSizesY[yi] * 0.5f),
            });
        }

        // Create group layouts in logical space.
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

            // Compute bounding box in logical space from fixture layouts
            foreach (int fi in g.fixtures)
            {
                if (fi < 0 || fi >= _fixtureLayouts.Count) continue;
                var fl = _fixtureLayouts[fi];
                float flMinX = fl.centre.x - fl.halfExt.x;
                float flMaxX = fl.centre.x + fl.halfExt.x;
                float flMinY = fl.centre.y - fl.halfExt.y;
                float flMaxY = fl.centre.y + fl.halfExt.y;

                if (flMinX < gMinX) gMinX = flMinX;
                if (flMaxX > gMaxX) gMaxX = flMaxX;
                if (flMinY < gMinY) gMinY = flMinY;
                if (flMaxY > gMaxY) gMaxY = flMaxY;
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
                    gMaxX - gMinX + GroupMargin * 2f,
                    gMaxY - gMinY + GroupMargin * 2f
                ),
            });
        }
    }

    // Update viewport transformation to fit logical layout into current canvas. Called on canvas resize.
    private void UpdateViewport()
    {
        Rect rect = _canvas.contentRect;
        if (rect.width <= 0 || rect.height <= 0) return;
        if (_logicalBounds.width <= 0 || _logicalBounds.height <= 0) return;

        _canvasSize = new Vector2(rect.width, rect.height);

        // Fit the layout uniformly into the canvas, respecting CanvasPadding.
        float usableW = _canvasSize.x - CanvasPadding * 2f;
        float usableH = _canvasSize.y - CanvasPadding * 2f;
        _logicalScale = Mathf.Min(usableW / _logicalBounds.width, usableH / _logicalBounds.height);

        // Centre the layout on the canvas.
        _logicalOffset = new Vector2(
            _canvasSize.x * 0.5f - (_logicalBounds.xMin + _logicalBounds.width * 0.5f) * _logicalScale,
            _canvasSize.y * 0.5f - (_logicalBounds.yMin + _logicalBounds.height * 0.5f) * _logicalScale
        );
    }

    // --- Drawing -----------------------------------------------------

    private void DrawCanvas()
    {
        Rect rect = _canvas.contentRect;
        if (rect.width <= 0 || rect.height <= 0) return;

        EnsureStyles();

        if (!Mathf.Approximately(rect.width, _canvasSize.x) ||
            !Mathf.Approximately(rect.height, _canvasSize.y))
            UpdateViewport();

        if (_fixtures == null || _fixtures.Count == 0)
        {
            DrawEmptyState(rect);
            return;
        }

        HandleMouseEvents(rect);

        // Clip drawing to canvas bounds to prevent zoomed content from overflowing.
        GUI.BeginClip(rect);
        DrawGroups();
        DrawFixtures();
        GUI.EndClip();
    }

    private void HandleMouseEvents(Rect rect)
    {
        Event e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;

        // Zoom via scroll wheel
        if (e.type == EventType.ScrollWheel)
        {
            float delta = e.delta.y * -0.05f;
            float newZoom = Mathf.Clamp(_zoom + delta * _zoom, 0.1f, 10f);
            // Zoom toward mouse: keep the layout point under the cursor fixed.
            _panOffset = e.mousePosition - (e.mousePosition - _panOffset) * (newZoom / _zoom);
            _zoom = newZoom;
            _canvas.MarkDirtyRepaint();
            e.Use();
            return;
        }

        // Pan via middle-mouse drag
        if (e.type == EventType.MouseDown && e.button == 2)
        {
            _isPanning = true;
            e.Use();
            return;
        }

        if (_isPanning)
        {
            if (e.type == EventType.MouseDrag)
            {
                _panOffset += e.delta;
                _canvas.MarkDirtyRepaint();
                e.Use();
                return;
            }
            else if (e.type == EventType.MouseUp && e.button == 2)
            {
                _isPanning = false;
                e.Use();
                return;
            }
        }

        // Left-click interactions
        if (e.type != EventType.MouseDown || e.button != 0) return;

        bool additive = e.control || e.command || e.shift;
        bool doubleClick = e.clickCount == 2;

        // Convert screen position to layout space for hit-testing
        Vector2 layoutPos = ToLayout(e.mousePosition);

        // Fixtures have priority: hit-test in reverse draw order so topmost wins.
        int fixtureHit = -1;
        for (int i = _fixtureLayouts.Count - 1; i >= 0; i--)
        {
            var fl = _fixtureLayouts[i];
            if (new Rect(fl.centre.x - fl.halfExt.x, fl.centre.y - fl.halfExt.y,
                         fl.halfExt.x * 2f, fl.halfExt.y * 2f).Contains(layoutPos))
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
                if (gl.valid && gl.rect.Contains(layoutPos))
                    { groupHit = gi; break; }
            }

            if (groupHit >= 0)
            {
                var toSelect = new List<UnityEngine.Object>();
                foreach (int fi in _groups[groupHit].fixtures)
                    CollectFixtureObjects(fi, toSelect);

                SetFixtureSelection(toSelect, additive ? SelectionMode.SmartToggle : SelectionMode.Replace);
            }
            else
            {
                // Single-click deselects all
                Selection.objects = Array.Empty<UnityEngine.Object>();
            }
        }

        e.Use();
    }

    private void ToggleOrSet(UnityEngine.Object obj, bool additive)
    {
        var toSelect = new List<UnityEngine.Object>();

        if (obj is GameObject go)
        {
            int fixtureIndex = _fixtureObjects.IndexOf(go);
            CollectFixtureObjects(fixtureIndex, toSelect);
        }
        else
        {
            toSelect.Add(obj);
        }

        SetFixtureSelection(toSelect, additive ? SelectionMode.Toggle : SelectionMode.Replace);
    }

    private enum SelectionMode
    {
        Replace,      // Clear selection and select only these objects
        Toggle,       // For each object: if selected, deselect; otherwise select (per-item toggle)
        Add,          // Add these objects to current selection
        Remove,       // Remove these objects from current selection
        SmartToggle,  // If all objects already selected, deselect all; otherwise select all (group toggle)
    }

    // Unified selection handler for all selection operations (diagram clicks, UI buttons, etc).
    // Collects the desired objects and delegates to this method with the appropriate mode.
    private void SetFixtureSelection(List<UnityEngine.Object> fixtures, SelectionMode mode)
    {
        if (fixtures.Count == 0) return;

        var current = new HashSet<UnityEngine.Object>(Selection.objects);

        switch (mode)
        {
            case SelectionMode.Replace:
                // Non-additive fixture/group clicks, "Select group" button.
                Selection.objects = fixtures.ToArray();
                break;

            case SelectionMode.Toggle:
                // Toggle or invert each individual fixture's selection state.
                foreach (var fixture in fixtures)
                    if (current.Contains(fixture)) current.Remove(fixture);
                    else current.Add(fixture);
                Selection.objects = new List<UnityEngine.Object>(current).ToArray();
                break;

            case SelectionMode.Add:
                // Adds the fixtures to the current pool of selected fixtures.
                foreach (var fixture in fixtures) current.Add(fixture);
                Selection.objects = new List<UnityEngine.Object>(current).ToArray();
                break;

            case SelectionMode.Remove:
                // Removes the specified fixtures from the current pool.
                foreach (var fixture in fixtures) current.Remove(fixture);
                Selection.objects = new List<UnityEngine.Object>(current).ToArray();
                break;

            case SelectionMode.SmartToggle:
                // Ddeselect all if already all selected, else select all.
                bool allSelected = fixtures.TrueForAll(obj => current.Contains(obj));
                if (allSelected)
                    foreach (var fixture in fixtures) current.Remove(fixture);
                else
                    foreach (var fixture in fixtures) current.Add(fixture);
                Selection.objects = new List<UnityEngine.Object>(current).ToArray();
                break;
        }
    }


    private void DrawEmptyState(Rect rect)
    {
        GUI.Label(rect, "No fixture map loaded.\nUse Load map… to open a FixtureMap.json.", _emptyStateStyle);
    }

    private bool IsGroupSelected(GroupEntry group, HashSet<UnityEngine.Object> selectionSet)
    {
        return group.fixtures != null && group.fixtures.Count > 0 &&
               group.fixtures.TrueForAll(fi =>
                   fi >= 0 && fi < _fixtureObjects.Count &&
                   _fixtureObjects[fi] != null &&
                   selectionSet.Contains(_fixtureObjects[fi]));
    }

    private void DrawGroups()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);

        for (int gi = 0; gi < _groups.Count; gi++)
        {
            var gl = _groupLayouts[gi];
            if (!gl.valid) continue;
            Rect r = gl.rect;

            var g = _groups[gi];
            bool selected = IsGroupSelected(g, selectionSet);

            Color fill    = selected ? _theme.groupFill_Active.ToColor()   : _theme.groupFill.ToColor();
            Color outline = selected ? _theme.groupOutline_Active.ToColor() : _theme.groupOutline.ToColor();

            // Transform from logical space to screen space (applying viewport fit, zoom, and pan)
            Vector2 minScreen = ToScreen(new Vector2(r.xMin, r.yMin));
            Vector2 maxScreen = ToScreen(new Vector2(r.xMax, r.yMax));
            var corners = new Vector3[]
            {
                new Vector3(minScreen.x, minScreen.y),
                new Vector3(maxScreen.x, minScreen.y),
                new Vector3(maxScreen.x, maxScreen.y),
                new Vector3(minScreen.x, maxScreen.y),
            };
            Handles.DrawSolidRectangleWithOutline(corners, fill, outline);

            if (selected)
            {
                float labelW = Mathf.Max(maxScreen.x - minScreen.x, 120f);
                var labelRect = new Rect((minScreen.x + maxScreen.x) * 0.5f - labelW * 0.5f, maxScreen.y + 2f, labelW, 32f);
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

        // Transform centre from logical to screen space
        Vector2 ps = ToScreen(p);
        // Half-extents scale with viewport fit and zoom
        float dpws = dpw * _logicalScale * _zoom;
        float dpds = dpd * _logicalScale * _zoom;

        // Corners: top-left, top-right, bottom-right, bottom-left (clockwise).
        var corners = new Vector3[]
        {
            new(ps.x - dpws, ps.y - dpds),
            new(ps.x + dpws, ps.y - dpds),
            new(ps.x + dpws, ps.y + dpds),
            new(ps.x - dpws, ps.y + dpds),
        };

        Color fill = selected ? _theme.nodeFill_Active.ToColor() : _theme.nodeFill.ToColor();
        Color outline = selected ? _theme.nodeOutline_Active.ToColor() : _theme.nodeOutline.ToColor();
        Handles.DrawSolidRectangleWithOutline(corners, fill, outline);

        if (selected)
        {
            float labelW = Mathf.Max(dpws, 120f);
            var labelRect = new Rect(ps.x - labelW * 0.5f, ps.y + dpds + 2f, labelW, 32f);
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

        // Get brightness normalised to max brightness.
        float brightness = 0f;
        if (driverTyped.PropsTransform != null)
        {
            float scale = driverTyped.PropsTransform.localScale.x;
            brightness = Mathf.InverseLerp(0f, definitionTyped.Profile.BrightnessMax, scale);
        }

        // Fill and outline colours have alpha modulated by brightness.
        Color fillColor = emissionColor;
        fillColor.a = brightness;
        Color outlineColor = emissionColor;
        outlineColor.a = brightness + 0.5f;

        float padding = .06f * _logicalScale * _zoom;
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

    // --- Selection Groups UI ----------------------------------------

    private void RefreshSgList()
    {
        _sgList.Clear();

        // Build filtered list with original indices, excluding items being renamed
        var filtered = new List<(int index, SelectionGroup group)>();
        for (int i = 0; i < _selectionGroups.Count; i++)
        {
            // Skip items being renamed unless the filter is empty
            if (i == _selectedGroupIndex && _sgRenaming && !string.IsNullOrEmpty(_sgSearchText))
                continue;

            if (_selectionGroups[i].name.Contains(_sgSearchText, System.StringComparison.OrdinalIgnoreCase))
                filtered.Add((i, _selectionGroups[i]));
        }

        // Sort alphabetically by name
        filtered.Sort((a, b) => a.group.name.CompareTo(b.group.name));

        foreach (var (i, group) in filtered)
        {
            int idx = i;
            var item = new SelectionGroupItem();
            item.GroupIndex = idx;
            item.SetSelected(i == _selectedGroupIndex);

            if (i == _selectedGroupIndex && _sgRenaming)
            {
                item.SetEditMode(
                    group.name,
                    newName => CommitRename(idx, newName),
                    () => CancelRename()
                );
            }
            else
            {
                item.SetViewMode(
                    group.name,
                    group.fixtures?.Count ?? 0,
                    () => OnSgListItemClicked(idx),
                    () => OnSgReplaceSelection(idx),
                    () => OnSgAddSelection(idx),
                    () => OnSgRemoveSelection(idx)
                );
                int selectedCount = GetGroupSelectionCount(group);
                int totalCount = group.fixtures?.Count ?? 0;
                item.SetSelectionState(selectedCount, totalCount);
            }

            _sgList.Add(item);
        }
        UpdateSgButtons();
    }

    // Update selection state icons for all visible items in the selection group list.
    private void RefreshSgListSelectionStates()
    {
        if (_sgList == null) return;

        for (int i = 0; i < _sgList.childCount; i++)
        {
            if (_sgList[i] is SelectionGroupItem item && item.GroupIndex >= 0 && item.GroupIndex < _selectionGroups.Count)
            {
                var group = _selectionGroups[item.GroupIndex];
                int selectedCount = GetGroupSelectionCount(group);
                int totalCount = group.fixtures?.Count ?? 0;
                item.SetSelectionState(selectedCount, totalCount);
            }
        }
    }

    private void OnSgListItemClicked(int index)
    {
        _selectedGroupIndex = (_selectedGroupIndex == index) ? -1 : index;
        RefreshSgList();
    }

    private void CommitRename(int index, string newName)
    {
        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            var g = _selectionGroups[index];
            g.name = newName;
            _selectionGroups[index] = g;
            SaveSelectionGroups();
        }
        _sgRenaming = false;
        RefreshSgList();
    }

    private void CancelRename()
    {
        _sgRenaming = false;
        RefreshSgList();
    }

    private void UpdateSgButtons()
    {
        // UI elements may not exist if the window hasn't been created yet or is not visible
        if (_sgCreateBtn == null) return;

        bool hasMap     = _fixtures.Count > 0;
        bool hasGroup   = _selectedGroupIndex >= 0 && _selectedGroupIndex < _selectionGroups.Count;

        _sgCreateBtn.SetEnabled(hasMap);
        _sgRenameBtn.SetEnabled(hasGroup);
        _sgDeleteBtn.SetEnabled(hasGroup);
        _sgAddBtn.SetEnabled(hasGroup);
        _sgRemoveBtn.SetEnabled(hasGroup);

        if (!hasGroup)
        {
            _sgAddBtn.text    = "Add 0 to group";
            _sgRemoveBtn.text = "Remove 0 from group";
            return;
        }

        var group = _selectionGroups[_selectedGroupIndex];
        var groupSet    = new HashSet<int>(group.fixtures ?? new List<int>());
        var selectedIdx = GetSelectedFixtureIndices();

        int addCount    = 0;
        int removeCount = 0;
        foreach (int idx in selectedIdx)
        {
            if (!groupSet.Contains(idx)) addCount++;
            else removeCount++;
        }

        _sgAddBtn.text    = $"Add {addCount} to group";
        _sgRemoveBtn.text = $"Remove {removeCount} from group";
    }

    private List<int> GetSelectedFixtureIndices()
    {
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);
        var result = new List<int>();
        for (int i = 0; i < _fixtureObjects.Count; i++)
            if (_fixtureObjects[i] != null && selectionSet.Contains(_fixtureObjects[i]))
                result.Add(i);
        return result;
    }

    // Count how many fixtures in a group are currently selected.
    private int GetGroupSelectionCount(SelectionGroup group)
    {
        if (group.fixtures == null || group.fixtures.Count == 0) return 0;
        var selectionSet = new HashSet<UnityEngine.Object>(Selection.objects);
        int count = 0;
        foreach (int idx in group.fixtures)
            if (idx >= 0 && idx < _fixtureObjects.Count && _fixtureObjects[idx] != null && selectionSet.Contains(_fixtureObjects[idx]))
                count++;
        return count;
    }

    // Collects root + head + props objects for a single fixture index, respecting selection toggle settings.
    private void CollectFixtureObjects(int fixtureIndex, List<UnityEngine.Object> outList)
    {
        if (fixtureIndex < 0 || fixtureIndex >= _fixtureObjects.Count) return;
        var fixtureRoot = _fixtureObjects[fixtureIndex];
        if (fixtureRoot == null) return;

        if (_includeMainFixture)
            outList.Add(fixtureRoot);

        if (_includeFixtureHead || _includePropsTransform)
        {
            if (fixtureIndex < _fixtureDrivers.Count)
            {
                var driver = _fixtureDrivers[fixtureIndex] as FixtureDriver;
                if (driver != null)
                {
                    if (_includeFixtureHead && driver.Head != null)
                        outList.Add(driver.Head.gameObject);
                    if (_includePropsTransform && driver.PropsTransform != null)
                        outList.Add(driver.PropsTransform.gameObject);
                }
            }
        }
    }

    private List<UnityEngine.Object> GroupFixtureObjects(SelectionGroup group)
    {
        var result = new List<UnityEngine.Object>();
        if (group.fixtures == null) return result;
        foreach (int fi in group.fixtures)
            CollectFixtureObjects(fi, result);
        return result;
    }

    private void OnSgCreate()
    {
        var group = new SelectionGroup
        {
            name     = "Group " + (_selectionGroups.Count + 1),
            fixtures = GetSelectedFixtureIndices()
        };
        _selectionGroups.Add(group);
        _selectedGroupIndex = _selectionGroups.Count - 1;
        _sgRenaming = true;
        SaveSelectionGroups();
        RefreshSgList();
    }

    private void OnSgRename()
    {
        if (_selectedGroupIndex < 0) return;
        _sgRenaming = true;
        RefreshSgList();
    }

    private void OnSgDelete()
    {
        if (_selectedGroupIndex < 0) return;
        string name = _selectionGroups[_selectedGroupIndex].name;
        if (!EditorUtility.DisplayDialog("Delete Group", $"Delete \"{name}\"?", "Delete", "Cancel")) return;

        _selectionGroups.RemoveAt(_selectedGroupIndex);
        _selectedGroupIndex = Mathf.Clamp(_selectedGroupIndex, -1, _selectionGroups.Count - 1);
        if (_selectionGroups.Count == 0) _selectedGroupIndex = -1;

        SaveSelectionGroups();
        RefreshSgList();
    }

    private void SetSgSelection(int groupIndex, SelectionMode mode)
    {
        if (groupIndex < 0 || groupIndex >= _selectionGroups.Count) return;
        var group = _selectionGroups[groupIndex];
        var objects = GroupFixtureObjects(group);
        SetFixtureSelection(objects, mode);
    }

    private void OnSgReplaceSelection(int groupIndex) => SetSgSelection(groupIndex, SelectionMode.Replace);
    private void OnSgAddSelection(int groupIndex) => SetSgSelection(groupIndex, SelectionMode.Add);
    private void OnSgRemoveSelection(int groupIndex) => SetSgSelection(groupIndex, SelectionMode.Remove);

    private void OnSgAddFixtures()
    {
        if (_selectedGroupIndex < 0) return;
        var selectedIdx = GetSelectedFixtureIndices();
        var group = _selectionGroups[_selectedGroupIndex];
        if (group.fixtures == null) group.fixtures = new List<int>();
        var groupSet = new HashSet<int>(group.fixtures);
        foreach (int idx in selectedIdx)
            groupSet.Add(idx);
        group.fixtures = new List<int>(groupSet);
        group.fixtures.Sort();
        _selectionGroups[_selectedGroupIndex] = group;
        SaveSelectionGroups();
        UpdateSgButtons();
    }

    private void OnSgRemoveFixtures()
    {
        if (_selectedGroupIndex < 0) return;
        var selectedIdx = new HashSet<int>(GetSelectedFixtureIndices());
        var group = _selectionGroups[_selectedGroupIndex];
        group.fixtures?.RemoveAll(i => selectedIdx.Contains(i));
        _selectionGroups[_selectedGroupIndex] = group;
        SaveSelectionGroups();
        UpdateSgButtons();
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

    // Transform from logical space to screen space (applying viewport fit, then zoom and pan)
    private Vector2 ToScreen(Vector2 logicalPt)
        => (logicalPt * _logicalScale + _logicalOffset) * _zoom + _panOffset;

    // Transform from screen space to logical space (inverse of ToScreen)
    private Vector2 ToLayout(Vector2 screenPt)
        => ((screenPt - _panOffset) / _zoom - _logicalOffset) / _logicalScale;

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
            ComputeLogicalLayout();
            UpdateViewport();
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
