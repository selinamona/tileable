using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

/// <summary>
/// Vertex Painter (Editor Tool)
/// ----------------------------------
/// Lightweight vertex color painter for MeshFilter meshes (URP/ShaderGraph friendly).
///
/// Features:
/// - Non-destructive workflow: paints on a temporary mesh instance ("_PaintedInstance").
/// - Stroke-based undo (1 undo step per brush stroke).
/// - Instant ShaderGraph refresh after Undo (forces vertex color rebind).
/// - Auto-add MeshCollider while painting (removed when exiting paint mode if it was auto-added).
/// - Tracks unsaved painted objects and allows selecting them in one click.
/// - Save Changes: saves all unsaved painted meshes to Output folder, overwriting existing Output meshes when possible.
/// - Save Preset Mesh: saves current painted mesh to Output/Presets with user-defined name + overwrite confirmation.
/// - Keyboard shortcut to toggle paint mode.
///
/// Notes:
/// - Requires a MeshFilter and (during painting) a MeshCollider.
/// - Painting logic: base is 1 (white). Normal paint writes inverted value (1 - targetValue).
///   Holding CTRL/CMD paints 1 (erase).
/// - While painting, normal selection is blocked. Use Shift+Click to select a different object.
/// - If an object was "prepared" (instanced) but never painted, it will revert to the original mesh
///   when you switch selection or exit paint mode.
/// </summary>

namespace HeroAssets
{
    public class VertexPainter : EditorWindow
    {
        #region Types

        private enum PaintChannel { R, G, B, A }

        #endregion

        #region Constants

        private const string WindowTitle = "Vertex Painter";
        private const string MenuPath = "Hero Assets/Vertex Painter";

        private const string TempSuffix = "_PaintedInstance";

        // Naming
        private const string PresetsFolderName = "Presets";
        private const string VpSuffix = "_VP";
        private const string PaintedInstanceSuffix = "_PaintedInstance";

        // Shortcut: Ctrl+Alt+V (Win/Linux) / Cmd+Alt+V (Mac)
        private const string ShortcutId = "Vertex Painter/Toggle Painting";

        // UI layout
        private const float ButtonHeight = 28f;
        private const float BlockSpacing = 10f;

        private const string LogPrefix = "[VertexPainter]";

        private const string BrandTitle = "Hero Assets - Vertex Painter";
        private const string BrandSubtitle = "Lightweight vertex color painting for meshes";

        #endregion

        #region UI State

        private bool paintingEnabled;
        private PaintChannel channel = PaintChannel.R;

        private float brushRadius = 1.0f;
        private float falloff = 2.0f;
        private float brushStrength = 1.0f;
        private float targetValue = 1.0f; // 0..1 (inverted when painting)

        [SerializeField] private DefaultAsset outputFolder;

        #endregion

        #region Workflow State

        private static readonly Color InitialColor = new Color(1, 1, 1, 1);

        // Current target
        private MeshFilter currentMF;
        private Mesh workingMesh;   // temporary editable mesh instance
        private Mesh originalMesh;  // source mesh (for naming)
        private bool meshInstanced;
        private bool isWorkingMeshTemporary;

        // Stroke / Undo
        private bool strokeActive;
        private int strokeUndoGroup = -1;
        private bool undoRefreshPending;

        // Hit cache for smooth preview
        private bool hasCachedHit;
        private Vector3 cachedHitPoint;
        private Vector3 cachedHitNormal;
        private Vector2 cachedMousePos;
        private GameObject cachedHitGO;

        // Auto MeshCollider (only if we added it)
        private MeshCollider autoAddedMeshCollider;

        // Objects painted but not saved
        private readonly HashSet<GameObject> unsavedPaintedObjects = new HashSet<GameObject>();

        // Original mesh per object (session only)
        private readonly Dictionary<int, Mesh> originalMeshByGO = new Dictionary<int, Mesh>();

        // Objects that were "prepared" (_PaintedInstance) but NOT painted yet
        private readonly HashSet<int> preparedButNotPainted = new HashSet<int>();

        #endregion

        #region Unity Hooks

        [MenuItem(MenuPath)]
        public static void ShowWindow() => GetWindow<VertexPainter>(false, WindowTitle, true);

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += OnEditorUpdate;

            paintingEnabled = false;

            TryBindSelectionIfAlreadyPrepared();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= OnEditorUpdate;

            CleanupMeshCollider();
        }

        private void OnSelectionChanged()
        {
            bool wasPainting = paintingEnabled;

            var prevMF = currentMF;

            CleanupMeshCollider();

            // If previous target was only prepared (not painted), revert it.
            RevertIfPreparedButNotPainted(prevMF);

            ResetTarget();

            if (Selection.activeGameObject == null)
            {
                Repaint();
                SceneView.RepaintAll();
                return;
            }

            if (wasPainting)
            {
                TryBindSelectionIfAlreadyPrepared();

                if (currentMF == null || currentMF.sharedMesh == null)
                    TryPrepareSelectedMesh(out _);

                if (currentMF != null)
                    EnsureMeshCollider();

                paintingEnabled = true;
            }
            else
            {
                TryBindSelectionIfAlreadyPrepared();
                paintingEnabled = false;
            }

            Repaint();
            SceneView.RepaintAll();
        }

        #endregion

        #region Undo Refresh

        private void OnUndoRedo()
        {
            undoRefreshPending = true;
            Repaint();
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate()
        {
            if (!undoRefreshPending) return;
            undoRefreshPending = false;

            ForceMeshGpuRefreshAfterUndo();

            Repaint();
            SceneView.RepaintAll();
        }

        private void ForceMeshGpuRefreshAfterUndo()
        {
            if (currentMF == null || currentMF.sharedMesh == null) return;

            var m = workingMesh != null ? workingMesh : currentMF.sharedMesh;

            var cols = m.colors;
            if (cols != null && cols.Length == m.vertexCount)
                m.colors = cols;

            m.RecalculateBounds();
            currentMF.sharedMesh = m;

            EditorUtility.SetDirty(m);
            EditorUtility.SetDirty(currentMF);
        }

        #endregion

        #region GUI

        private void OnGUI()
        {
            var buttonStyle = new GUIStyle(GUI.skin.button) { fixedHeight = ButtonHeight };

            DrawHeader();

            DrawPaintToggle();

            EditorGUILayout.Space();

            DrawBrushSettings();

            EditorGUILayout.Space(12);

            DrawActionBlocks(buttonStyle);

            EditorGUILayout.Space(12);

            DrawOutputFolderSlot();

            EditorGUILayout.Space(12);

            DrawLegend();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6);

            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            var subtitleStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

            EditorGUILayout.LabelField(BrandTitle, titleStyle);
            EditorGUILayout.LabelField(BrandSubtitle, subtitleStyle);

            EditorGUILayout.Space(6);

            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.25f));

            EditorGUILayout.Space(10);
        }

        private void DrawPaintToggle()
        {
            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                bool newEnabled = EditorGUILayout.ToggleLeft("Enable Painting", paintingEnabled);

                if (newEnabled && !paintingEnabled)
                {
                    if (CanReuseCurrentTemporaryInstance())
                    {
                        EnsureMeshCollider();
                        paintingEnabled = true;
                    }
                    else if (TryPrepareSelectedMesh(out _))
                    {
                        EnsureMeshCollider();
                        paintingEnabled = true;
                    }
                }
                else if (!newEnabled && paintingEnabled)
                {
                    // If the current object was only prepared but not painted, revert it.
                    RevertIfPreparedButNotPainted(currentMF);

                    paintingEnabled = false;
                    CleanupMeshCollider();
                }
            }
        }

        private void DrawBrushSettings()
        {
            channel = (PaintChannel)EditorGUILayout.EnumPopup("Channel", channel);
            brushRadius = EditorGUILayout.Slider("Brush Radius", brushRadius, 0.01f, 5f);
            falloff = EditorGUILayout.Slider("Falloff", falloff, 0.5f, 8f);
            brushStrength = EditorGUILayout.Slider("Brush Strength", brushStrength, 0.01f, 1f);
            targetValue = EditorGUILayout.Slider("Paint Value (0..1)", targetValue, 0f, 1f);
        }

        private void DrawActionBlocks(GUIStyle buttonStyle)
        {
            PruneUnsavedSet();

            string outFolder = GetOutputFolderPathOrNull();
            bool hasOutputFolder = !string.IsNullOrEmpty(outFolder);

            bool hasSelection = Selection.activeGameObject != null;

            bool hasValidTarget =
                meshInstanced &&
                currentMF != null &&
                hasSelection &&
                currentMF.gameObject == Selection.activeGameObject &&
                workingMesh != null &&
                currentMF.sharedMesh == workingMesh;

            bool canEditChannels = paintingEnabled && hasValidTarget;
            bool canRevert = hasValidTarget && isWorkingMeshTemporary;

            bool canSelectUnsaved = unsavedPaintedObjects.Count > 0;

            bool canSavePreset = hasValidTarget && hasOutputFolder;
            bool canSaveChanges = unsavedPaintedObjects.Count > 0 && hasOutputFolder;

            // -------------------------
            // Block 1: Paint operations + Revert
            // -------------------------
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(!canEditChannels))
                {
                    if (GUILayout.Button("Fill Channel With Paint Value", buttonStyle))
                        FillChannelOnMesh(InvertPaintValue(targetValue));

                    if (GUILayout.Button("Clear Channel", buttonStyle))
                        FillChannelOnMesh(1f);

                    if (GUILayout.Button("Clear All Channels", buttonStyle))
                        ClearAllChannelsOnMesh();
                }

                using (new EditorGUI.DisabledScope(!canRevert))
                {
                    if (GUILayout.Button("Revert (Restore Original Mesh)", buttonStyle))
                        RevertCurrentObjectToOriginal();
                }
            }

            EditorGUILayout.Space(BlockSpacing);

            // -------------------------
            // Block 2: Utilities
            // -------------------------
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(!canSelectUnsaved))
                {
                    if (GUILayout.Button($"Select Unsaved Painted Objects ({unsavedPaintedObjects.Count})", buttonStyle))
                    {
                        Selection.objects = GetUnsavedSelectionObjects();
                        SceneView.RepaintAll();
                    }
                }
            }

            EditorGUILayout.Space(BlockSpacing);

            // -------------------------
            // Block 3: Save (last button = Save Changes)
            // -------------------------
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(!canSavePreset))
                {
                    if (GUILayout.Button("Save Preset Mesh", buttonStyle))
                        SavePresetMesh();
                }

                using (new EditorGUI.DisabledScope(!canSaveChanges))
                {
                    if (GUILayout.Button($"Save Changes ({unsavedPaintedObjects.Count})", buttonStyle))
                        SaveChangesAllUnsaved();
                }

                if (!hasOutputFolder)
                    EditorGUILayout.HelpBox("Set an Output Folder to enable saving.", MessageType.Warning);
            }
        }

        private void DrawOutputFolderSlot()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Output Folder", EditorStyles.boldLabel);

                outputFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    outputFolder,
                    typeof(DefaultAsset),
                    false
                );

                string folderPath = GetOutputFolderPathOrNull();
                if (string.IsNullOrEmpty(folderPath))
                {
                    EditorGUILayout.HelpBox("Drag & drop a folder from the Project window.\nMeshes will be saved in Output, and presets in Output/Presets.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField(folderPath, EditorStyles.miniLabel);
                }
            }
        }

        private static void DrawLegend()
        {
            EditorGUILayout.HelpBox(
                "Enable Painting to start painting.\n" +
                "Click to paint.\n" +
                "Ctrl/Cmd + Click to erase.\n" +
                "Shift + Click to select another object.",
                MessageType.Info);
        }

        #endregion

        #region Preparation / Binding

        private void ResetTarget()
        {
            meshInstanced = false;
            isWorkingMeshTemporary = false;

            workingMesh = null;
            originalMesh = null;
            currentMF = null;

            strokeActive = false;
            strokeUndoGroup = -1;
            undoRefreshPending = false;

            hasCachedHit = false;
            cachedHitGO = null;
        }

        private bool CanReuseCurrentTemporaryInstance()
        {
            if (!meshInstanced) return false;
            if (!isWorkingMeshTemporary) return false;
            if (currentMF == null) return false;
            if (workingMesh == null) return false;

            if (Selection.activeGameObject != currentMF.gameObject) return false;
            if (currentMF.sharedMesh != workingMesh) return false;

            return true;
        }

        private bool TryPrepareSelectedMesh(out string error)
        {
            error = null;

            var go = Selection.activeGameObject;
            if (!go)
            {
                error = "No selection.";
                ResetTarget();
                return false;
            }

            currentMF = go.GetComponent<MeshFilter>();
            if (!currentMF || !currentMF.sharedMesh)
            {
                error = "Selected object must have a MeshFilter with a mesh.";
                ResetTarget();
                return false;
            }

            originalMesh = currentMF.sharedMesh;

            int id = go.GetInstanceID();

            // Store original (always update)
            originalMeshByGO[id] = originalMesh;

            // Mark as prepared (until first paint stroke)
            preparedButNotPainted.Add(id);

            // Create editable temp instance
            workingMesh = Instantiate(originalMesh);

            string baseName = StripPaintedInstanceSuffix(originalMesh.name);
            workingMesh.name = $"{baseName}{TempSuffix}";

            currentMF.sharedMesh = workingMesh;

            meshInstanced = true;
            isWorkingMeshTemporary = true;

            EnsureColorsInitialized(workingMesh);
            return true;
        }

        private void TryBindSelectionIfAlreadyPrepared()
        {
            var go = Selection.activeGameObject;
            if (!go) return;

            var mf = go.GetComponent<MeshFilter>();
            if (!mf || !mf.sharedMesh) return;

            bool looksTemporary = mf.sharedMesh.name.EndsWith(TempSuffix);
            bool isUnsaved = unsavedPaintedObjects.Contains(go);

            if (!looksTemporary && !isUnsaved) return;

            currentMF = mf;
            workingMesh = mf.sharedMesh;
            originalMesh = null;

            meshInstanced = true;
            isWorkingMeshTemporary = looksTemporary;

            paintingEnabled = false;
        }

        #endregion

        #region Scene GUI Painting

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!paintingEnabled) return;
            if (!meshInstanced || currentMF == null || workingMesh == null) return;

            var go = currentMF.gameObject;
            if (!go) return;

            var e = Event.current;

            // Block normal selection while painting (allow Shift+Click to pick)
            if (!e.shift && e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            // Shift+Click: select another object intentionally
            if (e.shift && e.type == EventType.MouseDown && e.button == 0)
            {
                var picked = HandleUtility.PickGameObject(e.mousePosition, false);
                if (picked != null)
                    Selection.activeGameObject = picked;

                e.Use();
                sceneView.Repaint();
                return;
            }

            // Don't interfere with SceneView navigation
            if (e.alt || e.button == 1 || e.button == 2)
                return;

            // Needs MeshCollider (existing or auto-added)
            var mc = go.GetComponent<MeshCollider>();
            if (mc == null) return;

            // Update cache only on relevant mouse events
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                UpdateHitCache(go, e);
                sceneView.Repaint();
            }

            // Preview disc
            if (hasCachedHit)
            {
                Handles.color = new Color(1, 1, 1, 0.5f);
                Handles.DrawWireDisc(cachedHitPoint, cachedHitNormal, brushRadius);
            }

            bool isLMBDown = (e.type == EventType.MouseDown && e.button == 0 && !e.alt);
            bool isLMBDrag = (e.type == EventType.MouseDrag && e.button == 0 && !e.alt);
            bool isLMBUp = (e.type == EventType.MouseUp && e.button == 0);

            if (!hasCachedHit)
            {
                if (strokeActive && isLMBUp)
                {
                    strokeActive = false;
                    if (strokeUndoGroup >= 0)
                        Undo.CollapseUndoOperations(strokeUndoGroup);
                    strokeUndoGroup = -1;
                }
                return;
            }

            // START stroke
            if (isLMBDown)
            {
                strokeActive = true;

                Undo.IncrementCurrentGroup();
                strokeUndoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(workingMesh, "Vertex Paint Stroke");

                bool erase = e.control || e.command;
                float v = erase ? 1f : InvertPaintValue(targetValue);

                PaintAtPoint(cachedHitPoint, brushRadius, v, brushStrength, falloff);
                e.Use();
            }

            // CONTINUE stroke
            if (strokeActive && isLMBDrag)
            {
                bool erase = e.control || e.command;
                float v = erase ? 1f : InvertPaintValue(targetValue);

                PaintAtPoint(cachedHitPoint, brushRadius, v, brushStrength, falloff);
                e.Use();
            }

            // END stroke
            if (strokeActive && isLMBUp)
            {
                strokeActive = false;

                if (strokeUndoGroup >= 0)
                    Undo.CollapseUndoOperations(strokeUndoGroup);

                strokeUndoGroup = -1;

                MarkCurrentObjectUnsaved();
                Repaint();
            }
        }

        private void UpdateHitCache(GameObject targetGO, Event e)
        {
            if (hasCachedHit && cachedHitGO == targetGO && cachedMousePos == e.mousePosition)
                return;

            cachedHitGO = targetGO;
            cachedMousePos = e.mousePosition;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit, 10000f) &&
                hit.collider != null &&
                hit.collider.gameObject == targetGO)
            {
                hasCachedHit = true;
                cachedHitPoint = hit.point;
                cachedHitNormal = hit.normal;
            }
            else
            {
                hasCachedHit = false;
            }
        }

        #endregion

        #region Painting Operations

        private static float InvertPaintValue(float slider01) => 1f - slider01;

        private void PaintAtPoint(Vector3 worldPoint, float radius, float value, float strength, float falloffPower)
        {
            Vector3[] verts = workingMesh.vertices;
            Color[] cols = workingMesh.colors;

            if (cols == null || cols.Length != workingMesh.vertexCount)
            {
                cols = new Color[workingMesh.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = InitialColor;
            }

            Transform t = currentMF.transform;
            float r2 = radius * radius;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 wp = t.TransformPoint(verts[i]);
                float d2 = (wp - worldPoint).sqrMagnitude;
                if (d2 > r2) continue;

                float d = Mathf.Sqrt(d2);
                float nd = Mathf.Clamp01(1f - (d / radius));
                float w = Mathf.Pow(nd, falloffPower) * strength;

                Color c = cols[i];
                ApplyToChannel(ref c, value, w);
                cols[i] = c;
            }

            workingMesh.colors = cols;
            EditorUtility.SetDirty(workingMesh);
        }

        private void FillChannelOnMesh(float value)
        {
            if (workingMesh == null) return;

            Undo.RegisterCompleteObjectUndo(workingMesh, "Fill Vertex Channel");

            Color[] cols = workingMesh.colors;
            if (cols == null || cols.Length != workingMesh.vertexCount)
            {
                cols = new Color[workingMesh.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = InitialColor;
            }

            for (int i = 0; i < cols.Length; i++)
            {
                Color c = cols[i];
                ApplyToChannel(ref c, value, 1f);
                cols[i] = c;
            }

            workingMesh.colors = cols;
            EditorUtility.SetDirty(workingMesh);

            SceneView.RepaintAll();
            MarkCurrentObjectUnsaved();
        }

        private void ClearAllChannelsOnMesh()
        {
            if (workingMesh == null) return;

            Undo.RegisterCompleteObjectUndo(workingMesh, "Clear All Vertex Channels");

            Color[] cols = workingMesh.colors;
            if (cols == null || cols.Length != workingMesh.vertexCount)
            {
                cols = new Color[workingMesh.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = InitialColor;
            }

            for (int i = 0; i < cols.Length; i++)
                cols[i] = InitialColor;

            workingMesh.colors = cols;
            EditorUtility.SetDirty(workingMesh);

            SceneView.RepaintAll();
            MarkCurrentObjectUnsaved();
        }

        private void EnsureColorsInitialized(Mesh mesh)
        {
            var cols = mesh.colors;
            if (cols == null || cols.Length != mesh.vertexCount)
                SetAllVertexColors(mesh, InitialColor);
        }

        private static void SetAllVertexColors(Mesh mesh, Color c)
        {
            var cols = new Color[mesh.vertexCount];
            for (int i = 0; i < cols.Length; i++)
                cols[i] = c;

            mesh.colors = cols;
            EditorUtility.SetDirty(mesh);
        }

        private void ApplyToChannel(ref Color c, float target, float t)
        {
            switch (channel)
            {
                case PaintChannel.R: c.r = Mathf.Lerp(c.r, target, t); break;
                case PaintChannel.G: c.g = Mathf.Lerp(c.g, target, t); break;
                case PaintChannel.B: c.b = Mathf.Lerp(c.b, target, t); break;
                case PaintChannel.A: c.a = Mathf.Lerp(c.a, target, t); break;
            }
        }

        #endregion

        #region Saving (Preset + Save Changes)

        private void SavePresetMesh()
        {
            if (workingMesh == null || currentMF == null) return;

            string outRoot = GetOutputFolderPathOrNull();
            if (string.IsNullOrEmpty(outRoot))
            {
                Debug.LogWarning($"{LogPrefix} Output Folder is not set.");
                return;
            }

            string presetsFolder = EnsurePresetsFolder(outRoot);

            // NOTE: Unity's SaveFilePanelInProject requires a default name.
            // Using an empty string will typically show "New Asset" as a starting point.
            string path = EditorUtility.SaveFilePanelInProject(
                "Save Preset Mesh",
                "",
                "asset",
                "Choose a name for the preset mesh (it will be saved in Output/Presets).",
                presetsFolder
            );

            if (string.IsNullOrEmpty(path)) return;

            // Force to Presets folder (keep it simple & consistent)
            if (!IsPathUnderFolder(path, presetsFolder))
            {
                EditorUtility.DisplayDialog(
                    "Invalid Location",
                    "Please save presets inside the Presets folder.\n\n" + presetsFolder,
                    "OK"
                );
                return;
            }

            // Enforce _VP suffix (your branding)
            path = EnsureFileNameHasSuffix(path, VpSuffix);

            // Overwrite confirmation
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Overwrite Preset?",
                    $"A preset with this name already exists:\n\n{path}\n\nDo you want to overwrite it?",
                    "Overwrite",
                    "Cancel"
                );

                if (!overwrite) return;

                Undo.RecordObject(existing, "Overwrite Preset Mesh");
                EditorUtility.CopySerialized(workingMesh, existing);
                existing.name = Path.GetFileNameWithoutExtension(path);

                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"{LogPrefix} Overwrote preset mesh: {path}");
                ApplySavedPresetToCurrentObject(existing);
                return;
            }

            // Create new preset asset
            Mesh saved = Instantiate(workingMesh);
            saved.name = Path.GetFileNameWithoutExtension(path);

            AssetDatabase.CreateAsset(saved, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"{LogPrefix} Saved preset mesh: {path}");
            ApplySavedPresetToCurrentObject(saved);
        }


        private void SaveChangesAllUnsaved()
        {
            PruneUnsavedSet();
            if (unsavedPaintedObjects.Count == 0) return;

            string outRoot = GetOutputFolderPathOrNull();
            if (string.IsNullOrEmpty(outRoot))
            {
                Debug.LogWarning($"{LogPrefix} Output Folder is not set.");
                return;
            }

            EnsurePresetsFolder(outRoot);

            AssetDatabase.StartAssetEditing();
            try
            {
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Save Vertex Paint Changes");

                var toProcess = new List<GameObject>(unsavedPaintedObjects);
                int savedCount = 0;

                foreach (var go in toProcess)
                {
                    if (go == null)
                    {
                        unsavedPaintedObjects.Remove(go);
                        continue;
                    }

                    var mf = go.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        unsavedPaintedObjects.Remove(go);
                        continue;
                    }

                    int id = go.GetInstanceID();

                    // This is the TEMP mesh we just painted on (_PaintedInstance)
                    Mesh srcMesh = mf.sharedMesh;

                    // -----------------------------
                    // 1) Decide overwrite target:
                    //    Use the "original mesh at the moment painting started" (originalMeshByGO),
                    //    because mf.sharedMesh is currently the temp PaintedInstance.
                    // -----------------------------
                    Mesh overwriteTarget = null;

                    // Preferred: overwrite the original mesh we started from (if it is a VP mesh in Output root)
                    if (originalMeshByGO.TryGetValue(id, out var originalAtStart) && originalAtStart != null)
                    {
                        if (AssetDatabase.Contains(originalAtStart))
                        {
                            string originalPath = AssetDatabase.GetAssetPath(originalAtStart);
                            if (IsPathUnderFolder(originalPath, outRoot) &&
                                !IsInPresetsFolder(originalPath, outRoot) &&
                                IsVpMeshName(originalAtStart.name))
                            {
                                overwriteTarget = originalAtStart;
                            }
                        }
                    }

                    // Fallback (rare): if we couldn't resolve from originalMeshByGO, try currentAssigned if it's an asset
                    if (overwriteTarget == null)
                    {
                        var currentAssigned = mf.sharedMesh;
                        if (currentAssigned != null && AssetDatabase.Contains(currentAssigned))
                        {
                            string currentPath = AssetDatabase.GetAssetPath(currentAssigned);
                            if (IsPathUnderFolder(currentPath, outRoot) &&
                                !IsInPresetsFolder(currentPath, outRoot) &&
                                IsVpMeshName(currentAssigned.name))
                            {
                                overwriteTarget = currentAssigned;
                            }
                        }
                    }

                    // Keep ref so we can destroy temp instance after assigning the saved asset
                    var tempBeforeAssign = mf.sharedMesh;

                    if (overwriteTarget != null)
                    {
                        // -----------------------------
                        // 2A) OVERWRITE existing Output mesh (no new files)
                        Undo.RecordObject(overwriteTarget, "Overwrite Painted Mesh");

                        // Copia REAL del mesh (vértices, colors, submeshes, etc.)
                        CopyMeshData(srcMesh, overwriteTarget);

                        // Mantén el nombre del asset estable (evita warnings y cosas raras)
                        string assetPath = AssetDatabase.GetAssetPath(overwriteTarget);
                        overwriteTarget.name = Path.GetFileNameWithoutExtension(assetPath);

                        // Asegura bounds coherentes
                        overwriteTarget.RecalculateBounds();

                        EditorUtility.SetDirty(overwriteTarget);

                        Undo.RecordObject(mf, "Assign Saved Mesh");
                        mf.sharedMesh = overwriteTarget;
                        // Esta es la “base” desde la que pintarás la próxima vez.
                        // Si no lo actualizas, puedes volver a engancharte a referencias viejas.
                        originalMeshByGO[id] = mf.sharedMesh;

                        EditorUtility.SetDirty(mf);

                        // Optional but useful: keep originalMeshByGO pointing to the overwritten asset
                        originalMeshByGO[id] = overwriteTarget;
                    }
                    else
                    {
                        // -----------------------------
                        // 2B) CREATE a new Output mesh for this GO
                        // base name: OriginalMeshName_VP
                        // -----------------------------
                        string baseOriginalName = GetBestOriginalMeshNameForObject(id, srcMesh); // base without _VP and without _PaintedInstance
                        string baseName = $"{baseOriginalName}{VpSuffix}";
                        string relPath = GenerateUniqueMeshAssetPath(outRoot, baseName);

                        Mesh saved = Instantiate(srcMesh);
                        saved.name = Path.GetFileNameWithoutExtension(relPath);

                        AssetDatabase.CreateAsset(saved, relPath);

                        Undo.RecordObject(mf, "Assign Saved Mesh");
                        mf.sharedMesh = saved;
                        EditorUtility.SetDirty(mf);

                        // IMPORTANT: next time we paint this GO, this becomes its "original at start"
                        originalMeshByGO[id] = saved;
                    }

                    // -----------------------------
                    // 3) Destroy temp mesh instance if needed
                    // -----------------------------
                    if (tempBeforeAssign != null && !AssetDatabase.Contains(tempBeforeAssign))
                    {
                        DestroyImmediate(tempBeforeAssign);
                    }

                    // -----------------------------
                    // 4) Clear tracking for this object
                    // -----------------------------
                    unsavedPaintedObjects.Remove(go);
                    preparedButNotPainted.Remove(id);

                    savedCount++;
                }

                Undo.CollapseUndoOperations(undoGroup);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"{LogPrefix} Saved changes for {savedCount} object(s) to: {outRoot}");
            }
            finally
            {
                paintingEnabled = false;
                CleanupMeshCollider();
                ResetTarget();

                AssetDatabase.StopAssetEditing();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        private static string GetStableBaseNameForSave(Mesh srcMesh)
        {
            if (srcMesh == null) return "Mesh";

            string n = srcMesh.name;

            // 1) quitar _PaintedInstance si está
            if (n.EndsWith(PaintedInstanceSuffix))
                n = n.Substring(0, n.Length - PaintedInstanceSuffix.Length);

            // 2) si ya termina en _VP, lo dejamos tal cual (porque Save Changes quiere exactamente Mesh_VP)
            if (n.EndsWith(VpSuffix))
                return n;

            // 3) si no lo tiene, se lo añadimos
            return n + VpSuffix;
        }
        private static string BuildPaintedBaseName(string originalMeshName)
        {
            originalMeshName = SanitizeName(originalMeshName);

            // Evita nombres como Mesh_VP_VP
            if (originalMeshName.EndsWith(VpSuffix))
                originalMeshName = originalMeshName.Substring(0, originalMeshName.Length - VpSuffix.Length);

            return $"{originalMeshName}{VpSuffix}";
        }

        private string GetBestOriginalMeshNameForObject(int id, Mesh fallback)
        {
            if (originalMeshByGO.TryGetValue(id, out var original) && original != null)
                return StripToolSuffixes(original.name);

            return StripToolSuffixes(fallback != null ? fallback.name : "Mesh");
        }

        private string GetOutputFolderPathOrNull()
        {
            if (outputFolder == null) return null;

            string path = AssetDatabase.GetAssetPath(outputFolder);
            if (string.IsNullOrEmpty(path)) return null;

            if (!AssetDatabase.IsValidFolder(path)) return null;

            return path;
        }

        private static string EnsurePresetsFolder(string outputRoot)
        {
            string presetsPath = $"{outputRoot}/{PresetsFolderName}";
            if (AssetDatabase.IsValidFolder(presetsPath))
                return presetsPath;

            // Create folder
            string guid = AssetDatabase.CreateFolder(outputRoot, PresetsFolderName);
            string createdPath = AssetDatabase.GUIDToAssetPath(guid);

            AssetDatabase.Refresh();
            return createdPath;
        }

        private static string StripPaintedInstanceSuffix(string name)
        {
            while (name.EndsWith(TempSuffix))
                name = name.Substring(0, name.Length - TempSuffix.Length);
            return name;
        }

        private static string StripToolSuffixes(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Mesh";

            // Remove temporary suffix
            name = StripPaintedInstanceSuffix(name);

            // Remove our painted suffix if present (so we can re-derive base consistently)
            if (name.EndsWith(VpSuffix))
                name = name.Substring(0, name.Length - VpSuffix.Length);

            // Also remove trailing _1, _2 etc if they exist and were produced by GenerateUniqueAssetPath
            // (optional; keeps base stable when repainting already saved meshes)
            name = StripTrailingUnityNumericSuffix(name);

            return name;
        }

        private static string StripTrailingUnityNumericSuffix(string name)
        {
            // Unity style: Foo_1, Foo_2, Foo_10...
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore >= name.Length - 1)
                return name;

            string tail = name.Substring(lastUnderscore + 1);
            for (int i = 0; i < tail.Length; i++)
            {
                if (!char.IsDigit(tail[i]))
                    return name;
            }

            return name.Substring(0, lastUnderscore);
        }

        private static string GenerateUniqueMeshAssetPath(string outputFolder, string baseName)
        {
            string candidate = $"{outputFolder}/{baseName}.asset";
            return AssetDatabase.GenerateUniqueAssetPath(candidate);
        }

        private static bool IsPathUnderFolder(string assetPath, string folderPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folderPath))
                return false;

            assetPath = assetPath.Replace("\\", "/");
            folderPath = folderPath.Replace("\\", "/").TrimEnd('/');

            return assetPath.StartsWith(folderPath + "/");
        }

        private static string EnsureFileNameHasSuffix(string assetPath, string suffix)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;

            string dir = Path.GetDirectoryName(assetPath)?.Replace("\\", "/") ?? "Assets";
            string file = Path.GetFileNameWithoutExtension(assetPath);
            string ext = Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(ext)) ext = ".asset";

            if (!file.EndsWith(suffix))
                file += suffix;

            return $"{dir}/{file}{ext}";
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Mesh";

            // Minimal sanitize for file names
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            return s.Trim();
        }

        #endregion

        #region MeshCollider Management

        private void EnsureMeshCollider()
        {
            if (currentMF == null) return;

            var go = currentMF.gameObject;
            var existing = go.GetComponent<MeshCollider>();

            if (existing != null)
            {
                autoAddedMeshCollider = null;
                return;
            }

            autoAddedMeshCollider = Undo.AddComponent<MeshCollider>(go);
            autoAddedMeshCollider.sharedMesh = currentMF.sharedMesh;
        }

        private void CleanupMeshCollider()
        {
            if (autoAddedMeshCollider != null)
            {
                Undo.DestroyObjectImmediate(autoAddedMeshCollider);
                autoAddedMeshCollider = null;
            }
        }

        #endregion

        #region Unsaved Tracking

        private void MarkCurrentObjectUnsaved()
        {
            if (currentMF == null) return;

            int id = currentMF.gameObject.GetInstanceID();

            preparedButNotPainted.Remove(id);

            unsavedPaintedObjects.Add(currentMF.gameObject);
            Repaint();
        }

        private void PruneUnsavedSet()
        {
            unsavedPaintedObjects.RemoveWhere(go => go == null);
        }

        private GameObject[] GetUnsavedGameObjects()
        {
            PruneUnsavedSet();

            var arr = new GameObject[unsavedPaintedObjects.Count];
            int i = 0;
            foreach (var go in unsavedPaintedObjects)
                arr[i++] = go;

            return arr;
        }

        private Object[] GetUnsavedSelectionObjects()
        {
            var gos = GetUnsavedGameObjects();
            var arr = new Object[gos.Length];
            for (int i = 0; i < gos.Length; i++) arr[i] = gos[i];
            return arr;
        }

        #endregion

        #region Shortcut

        [Shortcut(ShortcutId, KeyCode.V, ShortcutModifiers.Control | ShortcutModifiers.Alt)]
        private static void TogglePaintingShortcut()
        {
            var win = GetWindow<VertexPainter>();
            win.TogglePaintingFromShortcut();
            win.Repaint();
            SceneView.RepaintAll();
        }

        private void TogglePaintingFromShortcut()
        {
            if (paintingEnabled)
            {
                RevertIfPreparedButNotPainted(currentMF);

                paintingEnabled = false;
                CleanupMeshCollider();
                return;
            }

            if (Selection.activeGameObject == null) return;

            if (CanReuseCurrentTemporaryInstance())
            {
                EnsureMeshCollider();
                paintingEnabled = true;
                return;
            }

            if (TryPrepareSelectedMesh(out _))
            {
                EnsureMeshCollider();
                paintingEnabled = true;
            }
        }

        #endregion

        #region Revert

        private void RevertCurrentObjectToOriginal()
        {
            if (currentMF == null) return;

            var go = currentMF.gameObject;
            if (!go) return;

            int id = go.GetInstanceID();
            if (!originalMeshByGO.TryGetValue(id, out var original) || original == null)
            {
                Debug.LogWarning($"{LogPrefix} No original mesh stored for this object (cannot revert).");
                return;
            }

            Undo.RecordObject(currentMF, "Revert Vertex Paint");

            var temp = currentMF.sharedMesh;
            bool isTempInstance =
                temp != null &&
                (temp.name.EndsWith(TempSuffix) || isWorkingMeshTemporary) &&
                !AssetDatabase.Contains(temp);

            currentMF.sharedMesh = original;
            EditorUtility.SetDirty(currentMF);

            if (isTempInstance)
                DestroyImmediate(temp);

            unsavedPaintedObjects.Remove(go);
            preparedButNotPainted.Remove(id);
            originalMeshByGO.Remove(id);

            paintingEnabled = false;
            CleanupMeshCollider();
            ResetTarget();

            Repaint();
            SceneView.RepaintAll();
        }

        private void RevertIfPreparedButNotPainted(MeshFilter mf)
        {
            if (mf == null) return;

            var go = mf.gameObject;
            if (!go) return;

            int id = go.GetInstanceID();

            if (!preparedButNotPainted.Contains(id))
                return;

            if (!originalMeshByGO.TryGetValue(id, out var original) || original == null)
                return;

            var temp = mf.sharedMesh;
            bool isTempInstance = temp != null && !AssetDatabase.Contains(temp);

            Undo.RecordObject(mf, "Revert Prepared Mesh");
            mf.sharedMesh = original;
            EditorUtility.SetDirty(mf);

            if (isTempInstance)
                DestroyImmediate(temp);

            preparedButNotPainted.Remove(id);
            originalMeshByGO.Remove(id);
        }

        #endregion
        private static bool IsVpMeshName(string meshName)
        {
            return !string.IsNullOrEmpty(meshName) && meshName.Contains(VpSuffix);
        }

        private static bool IsInPresetsFolder(string assetPath, string outRoot)
        {
            string presetsPath = $"{outRoot}/{PresetsFolderName}";
            return IsPathUnderFolder(assetPath, presetsPath);
        }

        private static void CopyMeshData(Mesh src, Mesh dst)
        {
            if (src == null || dst == null) return;

            // Importante: limpia completamente el destino
            dst.Clear();

            // Geometría base
            dst.vertices = src.vertices;
            dst.normals = src.normals;
            dst.tangents = src.tangents;
            dst.colors = src.colors;

            // UVs (copiamos las 8 por si acaso)
            dst.uv = src.uv;
            dst.uv2 = src.uv2;
            dst.uv3 = src.uv3;
            dst.uv4 = src.uv4;
            dst.uv5 = src.uv5;
            dst.uv6 = src.uv6;
            dst.uv7 = src.uv7;
            dst.uv8 = src.uv8;

            // Submeshes + tris
            dst.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
                dst.SetTriangles(src.GetTriangles(s), s, true);

            // Bounds
            dst.bounds = src.bounds;
        }

        private void ApplySavedPresetToCurrentObject(Mesh presetAsset)
        {
            if (currentMF == null || presetAsset == null) return;

            // Guardamos referencia a la mesh actual (normalmente el _PaintedInstance)
            var tempBeforeAssign = currentMF.sharedMesh;

            // Asignar preset al objeto
            Undo.RecordObject(currentMF, "Assign Preset Mesh");
            currentMF.sharedMesh = presetAsset;
            EditorUtility.SetDirty(currentMF);

            // Si lo anterior era una instancia temporal (no asset), la destruimos
            if (tempBeforeAssign != null && !AssetDatabase.Contains(tempBeforeAssign) && tempBeforeAssign != presetAsset)
                DestroyImmediate(tempBeforeAssign);

            // Estado interno de la tool
            workingMesh = presetAsset;
            meshInstanced = true;
            isWorkingMeshTemporary = false;

            // ✅ Salir de pintura (seguridad)
            paintingEnabled = false;
            CleanupMeshCollider();

            // Tracking: ya está guardado (como preset), así que no debe quedar como "unsaved"
            int id = currentMF.gameObject.GetInstanceID();
            preparedButNotPainted.Remove(id);
            unsavedPaintedObjects.Remove(currentMF.gameObject);

            // Muy importante: a partir de ahora, esta es la "base" para futuras sesiones de pintado/revert
            originalMeshByGO[id] = presetAsset;

            Repaint();
            SceneView.RepaintAll();
        }
    }
}