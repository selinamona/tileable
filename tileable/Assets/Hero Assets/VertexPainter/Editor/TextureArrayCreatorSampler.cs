#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace HeroAssets
{
    /// <summary>
    /// Hero Assets - Texture Manager
    /// ----------------------------------
    /// Build Texture2DArray + masks from texture sets (Sampler variation folders).
    ///
    /// Key behaviors:
    /// - Setup tab owns Variation Folders + suffixes + output setup.
    /// - Auto-fills arrays + height slots whenever folders/suffixes change.
    /// - Auto-applies import settings before generating outputs (no user toggle).
    /// - Does NOT overwrite existing assets without asking.
    ///
    /// Arrays naming (Output Folder):
    /// - TA_<Name>_BaseColor.asset
    /// - TA_<Name>_Normal.asset
    /// - TA_<Name>_MaskMap.asset
    ///
    /// Height mask naming (Output Folder):
    /// - T_<Name>_HeightMask.asset
    ///
    /// MaskMap behavior (IMPORTANT):
    /// - Building arrays NEVER saves generated per-folder MaskMaps (Mode A).
    /// - If a folder lacks a Unity MaskMap, the tool generates a temporary mask in memory and builds the array.
    /// - Only the Masks tab button "Generate & Save Masks" can save per-folder MaskMaps.
    /// </summary>
    public class TextureManager : EditorWindow
    {
        #region Constants

        private const string WindowTitle = "Texture Manager";
        private const string MenuPath = "Hero Assets/Texture Manager";

        private const string BrandTitle = "Hero Assets - Texture Manager";
        private const string BrandSubtitle = "Build Texture2D Array + masks from texture sets";

        private const float ButtonHeight = 30f;
        private const float BlockSpacing = 10f;

        private const string LogPrefix = "[TextureManager]";

        // Prefs
        private const string PrefKeyOutputFolder = "HeroAssets.TextureManager.OutputFolder";
        private const string PrefKeyDescriptiveName = "HeroAssets.TextureManager.DescriptiveName";
        private const string PrefKeyMaxTextureSize = "HeroAssets.TextureManager.MaxTextureSize";

        private const string PrefKeySuffix_BaseColor = "HeroAssets.TextureManager.Suffix.BaseColor";
        private const string PrefKeySuffix_Normal = "HeroAssets.TextureManager.Suffix.Normal";
        private const string PrefKeySuffix_UnityMaskMap = "HeroAssets.TextureManager.Suffix.UnityMaskMap";
        private const string PrefKeySuffix_Height = "HeroAssets.TextureManager.Suffix.Height";

        private const string PrefKeySuffix_ARM = "HeroAssets.TextureManager.Suffix.ARM";
        private const string PrefKeySuffix_ORM = "HeroAssets.TextureManager.Suffix.ORM";
        private const string PrefKeySuffix_Metallic = "HeroAssets.TextureManager.Suffix.Metallic";
        private const string PrefKeySuffix_AO = "HeroAssets.TextureManager.Suffix.AO";
        private const string PrefKeySuffix_Smoothness = "HeroAssets.TextureManager.Suffix.Smoothness";
        private const string PrefKeySuffix_Roughness = "HeroAssets.TextureManager.Suffix.Roughness";

        private const string PrefKeyFlipGreen = "HeroAssets.TextureManager.Normals.FlipGreen";

        private const string PrefKeySelectedSuffixPresetGuid = "HeroAssets.TextureManager.SuffixPreset.SelectedGuid";
        private const string PrefKeyPresetSaveFolder = "HeroAssets.TextureManager.SuffixPreset.SaveFolder";

        private const string GeneratedUnityMaskSuffix = "_MaskMap";



        #endregion

        #region Types / State
        private enum TextureSemantic
        {
            BaseColor,
            Normal,
            UnityMaskMap,
            Height,
            ARM,
            ORM,
            Metallic,
            AO,
            Smoothness,
            Roughness
        }

        private enum Tab
        {
            Setup = 0,
            TextureArrays = 1,
            Masks = 2
        }

        [Serializable]
        private class MaskSources
        {
            public DefaultAsset folder;
            public Texture2D unityMaskMap;

            public Texture2D arm;
            public Texture2D orm;

            public Texture2D metallic;
            public Texture2D ao;
            public Texture2D smoothness;
            public Texture2D roughness;

            public string suggestedBaseName = "";
        }

        [Serializable]
        private class State
        {
            public List<DefaultAsset> variationFolders = new List<DefaultAsset>();
            public bool includeSubfolders = true;

            public string baseColorSuffix = "_BaseColor";
            public string normalSuffix = "_Normal";
            public string unityMaskMapSuffix = "_MaskMap";
            public string heightSuffix = "_Height";

            public bool otherFormatsFoldout = false;
            public string armSuffix = "_ARM";
            public string ormSuffix = "_ORM";
            public string metallicSuffix = "_Metallic";
            public string aoSuffix = "_AO";
            public string smoothnessSuffix = "_Smoothness";
            public string roughnessSuffix = "_Roughness";

            public DefaultAsset outputFolder;
            public string descriptiveName = "";
            public int maxTextureSize = 2048;

            public bool genAlbedo = true;
            public bool genNormal = true;
            public bool genMaskArray = true;
            public bool genHeightMask = true;

            // Only affects Masks tab saving behavior (Mode A: arrays never save)
            public bool genPerFolderMaskMaps = true;

            public bool flipGreenChannel = false;

            public bool generateMipmaps = true;
            public FilterMode filterMode = FilterMode.Trilinear;
            public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
            public int anisoLevel = 8;

            public List<Texture2D> albedo = new List<Texture2D>();
            public List<Texture2D> normal = new List<Texture2D>();
            public List<Texture2D> mask = new List<Texture2D>();

            public Texture2D heightR;
            public Texture2D heightG;
            public Texture2D heightB;
            public Texture2D heightA;

            public List<MaskSources> maskSources = new List<MaskSources>();
        }

        private State state;

        private Tab currentTab = Tab.Setup;
        private Vector2 scroll;

        private ReorderableList variationFoldersList;
        private ReorderableList albedoList;
        private ReorderableList normalList;
        private ReorderableList maskArrayList;
        private ReorderableList perFolderMasksList;

        // -------------------------
        // Suffix Presets (NEW)
        // -------------------------

        // Presets runtime cache
        private List<HeroAssetsSuffixPreset> cachedSuffixPresets = new List<HeroAssetsSuffixPreset>();
        private List<string> cachedSuffixPresetLabels = new List<string>();
        private List<string> cachedSuffixPresetGuids = new List<string>(); // parallel to presets list (only for preset entries)

        private int suffixPresetPopupIndex = 0;

        // Special entries

        private const string PresetLabelCustomModified = "Custom (modified)";

        private bool suffixesDirtyFromPreset = false;
        private SuffixesData lastAppliedPresetSuffixes;
        private string lastSelectedPresetGuid = ""; // stored selection when preset selected

        #endregion

        #region Unity Hooks

        [MenuItem(MenuPath)]
        public static void ShowWindow() => GetWindow<TextureManager>(false, WindowTitle, true);

        private void OnEnable()
        {
            state ??= new State();

            if (state.variationFolders.Count == 0)
                for (int i = 0; i < 4; i++) state.variationFolders.Add(null);

            RestorePrefs();

            RefreshSuffixPresets(includeModifiedEntry: suffixesDirtyFromPreset);
            RestoreSelectedSuffixPreset();

            SetupLists();
            AutoFillFromFolders();
        }

        private void OnDisable()
        {
            SavePrefs();
        }

        #endregion

        #region GUI

        private void OnGUI()
        {
            DrawHeader();
            DrawTabs();

            scroll = EditorGUILayout.BeginScrollView(scroll);

            switch (currentTab)
            {
                case Tab.Setup:
                    DrawSetupTab();
                    break;
                case Tab.TextureArrays:
                    DrawTextureArraysTab();
                    break;
                case Tab.Masks:
                    DrawMasksTab();
                    break;
            }

            EditorGUILayout.EndScrollView();
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

        private void DrawTabs()
        {
            currentTab = (Tab)GUILayout.Toolbar((int)currentTab, new[]
            {
                "Setup",
                "Texture Arrays",
                "Masks"
            });

            EditorGUILayout.Space(8);
        }

        private void DrawSetupTab()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Variation Folders", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Add variation folders in order (each folder = one layer). Changes auto-fill arrays and mask sources.",
                    MessageType.Info);

                state.includeSubfolders = EditorGUILayout.Toggle("Include Subfolders", state.includeSubfolders);

                EditorGUILayout.Space(6);


                variationFoldersList.DoLayoutList();

               

                EditorGUILayout.Space(6);
                if (GUILayout.Button("Clear Variation Folders", GUILayout.Height(ButtonHeight)))
                {
                    for (int i = 0; i < state.variationFolders.Count; i++)
                        state.variationFolders[i] = null;

                    EnsureLayerListsSize(state.variationFolders.Count);

                    for (int i = 0; i < state.albedo.Count; i++)
                        state.albedo[i] = null;

                    for (int i = 0; i < state.normal.Count; i++)
                        state.normal[i] = null;

                    for (int i = 0; i < state.mask.Count; i++)
                        state.mask[i] = null;

                    for (int i = 0; i < state.maskSources.Count; i++)
                        state.maskSources[i] = new MaskSources();

                    ClearHeightSlots();

                    Repaint();
                }

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Suffixes (file name ends with):", EditorStyles.miniBoldLabel);

                DrawSuffixPresetDropdownUI();
                EditorGUILayout.Space(6);

                EditorGUI.BeginChangeCheck();

                state.baseColorSuffix = EditorGUILayout.TextField("BaseColor", state.baseColorSuffix);
                state.normalSuffix = EditorGUILayout.TextField("Normal", state.normalSuffix);
                state.unityMaskMapSuffix = EditorGUILayout.TextField("Unity MaskMap (M/AO/S)", state.unityMaskMapSuffix);
                state.heightSuffix = EditorGUILayout.TextField("Height", state.heightSuffix);

                state.otherFormatsFoldout = EditorGUILayout.Foldout(state.otherFormatsFoldout, "Other supported formats", true);
                if (state.otherFormatsFoldout)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        state.armSuffix = EditorGUILayout.TextField("ARM / R=AO, G=Rough, B=Metal", state.armSuffix);
                        state.ormSuffix = EditorGUILayout.TextField("ORM / R=AO, G=Rough, B=Metal", state.ormSuffix);
                        state.metallicSuffix = EditorGUILayout.TextField("Metallic", state.metallicSuffix);
                        state.aoSuffix = EditorGUILayout.TextField("AO", state.aoSuffix);
                        state.smoothnessSuffix = EditorGUILayout.TextField("Smoothness", state.smoothnessSuffix);
                        state.roughnessSuffix = EditorGUILayout.TextField("Roughness", state.roughnessSuffix);
                    }
                }

                bool suffixesChangedByUser = EditorGUI.EndChangeCheck();
                if (suffixesChangedByUser)
                {
                    // Mark as Custom (modified) if we were coming from a preset and user edited anything
                    if (!string.IsNullOrEmpty(lastSelectedPresetGuid))
                        suffixesDirtyFromPreset = true;

                    SaveSuffixPrefs();
                    RefreshAllFolderLayersFromNaming();
                    RefreshHeightSlots_Mixed();
                    Repaint();
                }

                EditorGUILayout.Space(6);
                if (GUILayout.Button("Save Current As Preset...", GUILayout.Height(ButtonHeight)))
                {
                    SaveCurrentSuffixesAsPreset();
                }
            }

            EditorGUILayout.Space(BlockSpacing);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Output setup", EditorStyles.boldLabel);

                state.outputFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent("Output Folder (Assets/...)"),
                    state.outputFolder,
                    typeof(DefaultAsset),
                    false
                );
                state.descriptiveName = EditorGUILayout.TextField(
                    new GUIContent("Descriptive Name (required)", "Example: RockyGround, Mud_Wet_01"),
                    state.descriptiveName
                );

                state.maxTextureSize = EditorGUILayout.IntPopup(
                    "Max Texture Size",
                    state.maxTextureSize,
                    new string[] { "32", "64", "128", "256", "512", "1024", "2048", "4096", "8192" },
                    new int[] { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 }
                 );

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Generate outputs", EditorStyles.boldLabel);


                state.genAlbedo = EditorGUILayout.ToggleLeft("Texture Array: BaseColor", state.genAlbedo);

                using (new EditorGUILayout.HorizontalScope())
                {
                    state.genNormal = EditorGUILayout.ToggleLeft("Texture Array: Normal", state.genNormal, GUILayout.MinWidth(180));
                    state.flipGreenChannel = EditorGUILayout.ToggleLeft("Flip Green Channel", state.flipGreenChannel, GUILayout.MinWidth(160));
                }

                state.genMaskArray = EditorGUILayout.ToggleLeft("Texture Array: MaskMap", state.genMaskArray);
                state.genHeightMask = EditorGUILayout.ToggleLeft("Texture: HeightMask (RGBA)", state.genHeightMask);

                // Important: this now ONLY affects the Masks tab saving, not arrays build
                state.genPerFolderMaskMaps = EditorGUILayout.ToggleLeft("Masks tab: Save missing Unity MaskMaps in folders", state.genPerFolderMaskMaps);

                EditorGUILayout.Space(8);

                bool outOk = ValidateOutputSetup(out string outMsg, out MessageType outType);
                EditorGUILayout.HelpBox(outMsg, outType);

                using (new EditorGUI.DisabledScope(!outOk))
                {
                    if (GUILayout.Button("Build All Outputs", GUILayout.Height(ButtonHeight + 6)))
                    {
                        bool anyFolder = state.variationFolders.Any(f => f != null);
                        if (anyFolder)
                        {
                            if (HasAnyValidVariationFolder())
                                AutoFillFromFolders();
                        }

                        

                        if (AnyArrayEnabled())
                        {
                            if (!ValidateArrays(out var arraysMsg, out _))
                            {
                                Debug.LogError($"{LogPrefix} Build All blocked (arrays): {arraysMsg}");
                                return;
                            }

                            if (!GenerateSelectedArrays_NoDialogs_WithOverwritePrompt())
                                return;
                        }

                        if (state.genHeightMask)
                        {
                            if (!ValidateHeightMaskPermissive(out var heightMsg, out var heightType))
                            {
                                Debug.LogError($"{LogPrefix} Build All blocked (height): {heightMsg}");
                                return;
                            }

                            if (heightType == MessageType.Warning)
                                Debug.LogWarning($"{LogPrefix} {heightMsg}");

                            if (!GenerateHeightMask_NoDialogs_Permissive_WithOverwritePrompt())
                                return;
                        }

                        SavePrefs();
                    }
                }
            }
        }

        private void DrawTextureArraysTab()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Texture Arrays", EditorStyles.boldLabel);

                if (!AnyArrayEnabled())
                {
                    EditorGUILayout.HelpBox("No Texture Arrays enabled. Enable them in Setup → Output setup.", MessageType.Info);
                    return;
                }

                EditorGUILayout.LabelField("Review & Manual Override", EditorStyles.boldLabel);

                if (state.genAlbedo) albedoList.DoLayoutList();
                if (state.genNormal) normalList.DoLayoutList();
                if (state.genMaskArray) maskArrayList.DoLayoutList();

                EditorGUILayout.Space(8);

                EditorGUILayout.LabelField("Array Settings", EditorStyles.boldLabel);
                state.generateMipmaps = EditorGUILayout.Toggle("Generate Mipmaps", state.generateMipmaps);
                state.filterMode = (FilterMode)EditorGUILayout.EnumPopup("Filter Mode", state.filterMode);
                state.wrapMode = (TextureWrapMode)EditorGUILayout.EnumPopup("Wrap Mode", state.wrapMode);
                state.anisoLevel = EditorGUILayout.IntSlider("Aniso Level", state.anisoLevel, 0, 16);

                EditorGUILayout.Space(10);

                bool setupOk = ValidateOutputSetup(out _, out _);
                bool arraysOk = ValidateArrays(out string msg, out MessageType msgType);

                if (!setupOk)
                    EditorGUILayout.HelpBox("Go to Setup tab and configure Output Folder + Descriptive Name.", MessageType.Warning);

                EditorGUILayout.HelpBox(msg, msgType);

                using (new EditorGUI.DisabledScope(!(setupOk && arraysOk)))
                {
                    if (GUILayout.Button("Generate & Save Selected Arrays", GUILayout.Height(ButtonHeight + 6)))
                    {
                        bool anyFolder = state.variationFolders.Any(f => f != null);
                        if (anyFolder) AutoFillFromFolders();

                        if (!ValidateArrays(out var vMsg, out _))
                        {
                            Debug.LogError($"{LogPrefix} Arrays build blocked: {vMsg}");
                            return;
                        }

                        if (!GenerateSelectedArrays_NoDialogs_WithOverwritePrompt())
                            return;

                        SavePrefs();
                    }
                }
            }
        }

        private void DrawMasksTab()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Masks", EditorStyles.boldLabel);

                EditorGUILayout.HelpBox(
                    "This tab manages mask outputs:\n" +
                    "- HeightMask (RGBA packed from up to 4 Height maps; missing channels = mid-gray)\n" +
                    "- Optional: Save per-folder Unity MaskMaps (_MaskMap) if missing.\n\n" +
                    "Note: Building arrays never saves MaskMaps (Mode A).",
                    MessageType.Info
                );

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("HeightMask Sources (manual override)", EditorStyles.boldLabel);

                state.heightR = (Texture2D)EditorGUILayout.ObjectField("R (Variation 0)", state.heightR, typeof(Texture2D), false);
                state.heightG = (Texture2D)EditorGUILayout.ObjectField("G (Variation 1)", state.heightG, typeof(Texture2D), false);
                state.heightB = (Texture2D)EditorGUILayout.ObjectField("B (Variation 2)", state.heightB, typeof(Texture2D), false);
                state.heightA = (Texture2D)EditorGUILayout.ObjectField("A (Variation 3)", state.heightA, typeof(Texture2D), false);

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Per-Variation Mask Sources (editable)", EditorStyles.boldLabel);
                perFolderMasksList.DoLayoutList();

                EditorGUILayout.Space(10);

                bool setupOk = ValidateOutputSetup(out _, out _);

                string hMsg = "";
                MessageType hType = MessageType.Info;
                bool heightOk = !state.genHeightMask || ValidateHeightMaskPermissive(out hMsg, out hType);

                if (state.genHeightMask)
                    EditorGUILayout.HelpBox(hMsg, hType);

                if (!setupOk)
                    EditorGUILayout.HelpBox("If Variation Folders are empty, saved MaskMaps will go to Output Folder.", MessageType.Info);

                using (new EditorGUI.DisabledScope(!setupOk && (state.variationFolders.Count > 0)))
                {
                    if (GUILayout.Button("Generate & Save Masks", GUILayout.Height(ButtonHeight + 6)))
                    {
                        bool anyFolder = state.variationFolders.Any(f => f != null);
                        if (anyFolder) AutoFillFromFolders();

                        // Only here we save missing per-folder MaskMaps
                        if (state.genPerFolderMaskMaps)
                        {
                            if (!GenerateAndSavePerFolderMaskMaps())
                                return;

                            AutoFillFromFolders();
                        }

                        if (state.genHeightMask)
                        {
                            if (!heightOk)
                            {
                                Debug.LogError($"{LogPrefix} Height build blocked: {hMsg}");
                                return;
                            }

                            if (hType == MessageType.Warning)
                                Debug.LogWarning($"{LogPrefix} {hMsg}");

                            if (!GenerateHeightMask_NoDialogs_Permissive_WithOverwritePrompt())
                                return;
                        }

                        SavePrefs();
                    }
                }
            }
        }

        #endregion

        #region UI Lists Setup

        private void SetupLists()
        {
            variationFoldersList = new ReorderableList(state.variationFolders, typeof(DefaultAsset), true, true, true, true);
            variationFoldersList.drawHeaderCallback = r => EditorGUI.LabelField(r, "Variation Folders (drag to reorder)");
            variationFoldersList.elementHeight = EditorGUIUtility.singleLineHeight + 6;

            variationFoldersList.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                rect.y += 3;
                rect.height = EditorGUIUtility.singleLineHeight;

                EditorGUI.BeginChangeCheck();
                var newFolder = (DefaultAsset)EditorGUI.ObjectField(
                    rect,
                    state.variationFolders[index],
                    typeof(DefaultAsset),
                    false
                );

                if (EditorGUI.EndChangeCheck())
                {
                    state.variationFolders[index] = newFolder;

                    if (newFolder != null)
                    {
                        if (index == 0) state.heightR = null;
                        if (index == 1) state.heightG = null;
                        if (index == 2) state.heightB = null;
                        if (index == 3) state.heightA = null;
                        OverwriteLayerFromFolder(index);
                    }
                    else
                    {
                        // Limpieza si borran folder
                        EnsureLayerListsSize(state.variationFolders.Count);

                        if (state.genAlbedo && index < state.albedo.Count)
                            state.albedo[index] = null;

                        if (state.genNormal && index < state.normal.Count)
                            state.normal[index] = null;

                        if (state.genMaskArray && index < state.mask.Count)
                            state.mask[index] = null;

                        if (index < state.maskSources.Count)
                            state.maskSources[index] = new MaskSources();
                    }

                    RefreshHeightSlots_Mixed();   // 🔥 ALTAMENTE IMPORTANTE
                    Repaint();
                }
            };

            variationFoldersList.onAddCallback = _ =>
            {
                state.variationFolders.Add(null);
                EnsureLayerListsSize(state.variationFolders.Count);
                Repaint();
            };
            variationFoldersList.onRemoveCallback = l =>
            {
                int i = l.index;
                if (i < 0 || i >= state.variationFolders.Count) return;

                state.variationFolders.RemoveAt(i);

                if (i < state.maskSources.Count) state.maskSources.RemoveAt(i);
                if (state.genAlbedo && i < state.albedo.Count) state.albedo.RemoveAt(i);
                if (state.genNormal && i < state.normal.Count) state.normal.RemoveAt(i);
                if (state.genMaskArray && i < state.mask.Count) state.mask.RemoveAt(i);

                RefreshHeightSlots_Mixed();   // 🔥
                Repaint();
            };

            variationFoldersList.onReorderCallbackWithDetails = (_, __, ___) =>
            {
                ResetAllManualDataAndRebuildFromFolders();
            };

            


            albedoList = MakeTextureList(state.albedo, "BaseColor Slots");
            normalList = MakeTextureList(state.normal, "Normal Slots");
            maskArrayList = MakeTextureList(state.mask, "Unity MaskMap Slots (M/AO/S)");

            perFolderMasksList = new ReorderableList(state.maskSources, typeof(MaskSources), false, true, false, false);
            perFolderMasksList.drawHeaderCallback = r => EditorGUI.LabelField(r, "Mask sources per variation (not reorderable)");
            perFolderMasksList.elementHeight = EditorGUIUtility.singleLineHeight * 9 + 18;

            perFolderMasksList.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                if (index < 0 || index >= state.maskSources.Count) return;
                var entry = state.maskSources[index];

                bool hasFolder =
                    state.variationFolders != null &&
                    index < state.variationFolders.Count &&
                    state.variationFolders[index] != null;

                float y = rect.y + 2;
                float lh = EditorGUIUtility.singleLineHeight;
                float pad = 2;

                var line = new Rect(rect.x, y, rect.width, lh);
                string folderName = entry.folder != null ? AssetDatabase.GetAssetPath(entry.folder) + " (auto)" : "(manual)";
                EditorGUI.LabelField(line, $"#{index}  Folder: {folderName}");

                y += lh + pad;

                using (new EditorGUI.DisabledScope(hasFolder))
                {
                    entry.unityMaskMap = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "Unity MaskMap", entry.unityMaskMap, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.arm = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "ARM", entry.arm, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.orm = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "ORM", entry.orm, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.metallic = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "Metallic", entry.metallic, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.ao = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "AO", entry.ao, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.smoothness = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "Smoothness", entry.smoothness, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.roughness = (Texture2D)EditorGUI.ObjectField(new Rect(rect.x, y, rect.width, lh), "Roughness", entry.roughness, typeof(Texture2D), false);
                    y += lh + pad;

                    entry.suggestedBaseName = EditorGUI.TextField(new Rect(rect.x, y, rect.width, lh), "Suggested Base Name", entry.suggestedBaseName);
                }
            };
        }

        private ReorderableList MakeTextureList(List<Texture2D> list, string header)
        {
            // draggable = false  (NO reorder)
            var rl = new ReorderableList(list, typeof(Texture2D), draggable: false, displayHeader: true, displayAddButton: false, displayRemoveButton: false);

            rl.drawHeaderCallback = rect => EditorGUI.LabelField(rect, header);
            rl.elementHeight = EditorGUIUtility.singleLineHeight + 6;

            rl.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                rect.y += 3;
                rect.height = EditorGUIUtility.singleLineHeight;

                var left = new Rect(rect.x, rect.y, 70, rect.height);
                var field = new Rect(rect.x + 74, rect.y, rect.width - 74, rect.height);

                bool hasFolder =
                    state.variationFolders != null &&
                    index < state.variationFolders.Count &&
                    state.variationFolders[index] != null;

                string origin = hasFolder ? "folder" : "manual";
                EditorGUI.LabelField(left, $"#{index} ({origin})");

                using (new EditorGUI.DisabledScope(hasFolder))
                {
                    list[index] = (Texture2D)EditorGUI.ObjectField(field, list[index], typeof(Texture2D), false);
                }
            };

            rl.onAddCallback = _ => list.Add(null);
            rl.onRemoveCallback = l =>
            {
                if (l.index >= 0 && l.index < list.Count)
                    list.RemoveAt(l.index);
            };

            return rl;
        }

        #endregion

        #region Auto Fill

        private void AutoFillFromFolders()
        {
            FillArraysFromVariationFolders();
            FillHeightSlotsFromFirstFourFolders();
            FillPerFolderMaskSources();
        }

        private void FillArraysFromVariationFolders()
        {
            int layers = state.variationFolders.Count;
            if (layers <= 0) return;

            if (state.genAlbedo) ResizeList(state.albedo, layers);
            if (state.genNormal) ResizeList(state.normal, layers);
            if (state.genMaskArray) ResizeList(state.mask, layers);

            for (int layer = 0; layer < layers; layer++)
            {
                var folderAsset = state.variationFolders[layer];
                if (folderAsset == null)
                {
                    // Modo manual: no tocar los slots si no hay folder
                    continue;
                }

                string folderPath = AssetDatabase.GetAssetPath(folderAsset);
                if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                {
                    // No folder válido -> no pisar manual
                    continue;
                }

                FindMapsInFolder(folderPath,
                    out var baseColor,
                    out var normal,
                    out var unityMaskMap,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _
                );

                if (state.genAlbedo && layer < state.albedo.Count) state.albedo[layer] = baseColor;
                if (state.genNormal && layer < state.normal.Count) state.normal[layer] = normal;
                if (state.genMaskArray && layer < state.mask.Count) state.mask[layer] = unityMaskMap;
            }
        }

        private void FillHeightSlotsFromFirstFourFolders()
        {
            // Solo pisa el slot si hay folder válido en ese índice.
            // Si no hay folder -> modo manual -> NO tocar lo que haya.
            if (HasValidFolderAt(0)) state.heightR = FindHeightInFolderAtIndex(0);
            if (HasValidFolderAt(1)) state.heightG = FindHeightInFolderAtIndex(1);
            if (HasValidFolderAt(2)) state.heightB = FindHeightInFolderAtIndex(2);
            if (HasValidFolderAt(3)) state.heightA = FindHeightInFolderAtIndex(3);
        }

        private bool HasValidFolderAt(int idx)
        {
            if (state.variationFolders == null) return false;
            if (idx < 0 || idx >= state.variationFolders.Count) return false;
            var f = state.variationFolders[idx];
            if (f == null) return false;
            var p = AssetDatabase.GetAssetPath(f);
            return !string.IsNullOrEmpty(p) && AssetDatabase.IsValidFolder(p);
        }

        private Texture2D FindHeightInFolderAtIndex(int idx)
        {
            if (idx < 0 || idx >= state.variationFolders.Count) return null;
            var folderAsset = state.variationFolders[idx];
            if (folderAsset == null) return null;

            string folderPath = AssetDatabase.GetAssetPath(folderAsset);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath)) return null;

            FindMapsInFolder(folderPath,
                out _,
                out _,
                out _,
                out var height,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _
            );

            return height;
        }

        private void FillPerFolderMaskSources()
        {
            int layers = state.variationFolders.Count;
            if (layers < 0) layers = 0;

            while (state.maskSources.Count < layers) state.maskSources.Add(new MaskSources());
            while (state.maskSources.Count > layers) state.maskSources.RemoveAt(state.maskSources.Count - 1);

            for (int i = 0; i < layers; i++)
            {
                var entry = state.maskSources[i];
                entry.folder = state.variationFolders[i];

                if (entry.folder == null) continue;

                string folderPath = AssetDatabase.GetAssetPath(entry.folder);
                if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath)) continue;

                FindMapsInFolder(folderPath,
                    out _,
                    out _,
                    out var unityMaskMap,
                    out _,
                    out var arm,
                    out var orm,
                    out var metallic,
                    out var ao,
                    out var smooth,
                    out var rough
                );

                entry.unityMaskMap = unityMaskMap;
                entry.arm = arm;
                entry.orm = orm;
                entry.metallic = metallic;
                entry.ao = ao;
                entry.smoothness = smooth;
                entry.roughness = rough;
            }
        }

        private void ClearPerFolderMaskSources()
        {
            state.maskSources.Clear();
        }

        private void FindMapsInFolder(
            string folderPath,
            out Texture2D baseColor,
            out Texture2D normal,
            out Texture2D unityMaskMap,
            out Texture2D height,
            out Texture2D arm,
            out Texture2D orm,
            out Texture2D metallic,
            out Texture2D ao,
            out Texture2D smoothness,
            out Texture2D roughness)
        {
            baseColor = null;
            normal = null;
            unityMaskMap = null;
            height = null;

            arm = null;
            orm = null;
            metallic = null;
            ao = null;
            smoothness = null;
            roughness = null;

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
            if (guids == null || guids.Length == 0) return;

            Array.Sort(guids, StringComparer.OrdinalIgnoreCase);

            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(path)) continue;

                if (!state.includeSubfolders && !IsDirectChild(folderPath, path))
                    continue;

                string name = Path.GetFileNameWithoutExtension(path);

                if (baseColor == null && EndsWithIgnoreCase(name, state.baseColorSuffix))
                    baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (normal == null && EndsWithIgnoreCase(name, state.normalSuffix))
                    normal = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (unityMaskMap == null && EndsWithIgnoreCase(name, state.unityMaskMapSuffix))
                    unityMaskMap = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (height == null && EndsWithIgnoreCase(name, state.heightSuffix))
                    height = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (arm == null && EndsWithIgnoreCase(name, state.armSuffix))
                    arm = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (orm == null && EndsWithIgnoreCase(name, state.ormSuffix))
                    orm = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (metallic == null && EndsWithIgnoreCase(name, state.metallicSuffix))
                    metallic = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (ao == null && EndsWithIgnoreCase(name, state.aoSuffix))
                    ao = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (smoothness == null && EndsWithIgnoreCase(name, state.smoothnessSuffix))
                    smoothness = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (roughness == null && EndsWithIgnoreCase(name, state.roughnessSuffix))
                    roughness = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }

        private void AssignArrayLayer(int layer, Texture2D baseColor, Texture2D normal, Texture2D unityMaskMap)
        {
            if (state.genAlbedo && layer < state.albedo.Count) state.albedo[layer] = baseColor;
            if (state.genNormal && layer < state.normal.Count) state.normal[layer] = normal;
            if (state.genMaskArray && layer < state.mask.Count) state.mask[layer] = unityMaskMap;
        }

        private static void ResizeList<T>(List<T> list, int count) where T : class
        {
            while (list.Count < count) list.Add(null);
            while (list.Count > count) list.RemoveAt(list.Count - 1);
        }

        private static bool EndsWithIgnoreCase(string s, string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return false;
            return s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectChild(string folder, string assetPath)
        {
            if (!assetPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = assetPath.Substring(folder.Length);
            if (rest.StartsWith("/")) rest = rest.Substring(1);
            return !rest.Contains("/");
        }

        private void ClearArrayLists()
        {
            state.albedo.Clear();
            state.normal.Clear();
            state.mask.Clear();
        }

        private void ClearHeightSlots()
        {
            state.heightR = null;
            state.heightG = null;
            state.heightB = null;
            state.heightA = null;
        }

        private bool HasAnyValidVariationFolder()
        {
            if (state?.variationFolders == null) return false;

            foreach (var f in state.variationFolders)
            {
                if (f == null) continue;
                var p = AssetDatabase.GetAssetPath(f);
                if (!string.IsNullOrEmpty(p) && AssetDatabase.IsValidFolder(p))
                    return true;
            }
            return false;
        }

        private bool IsFolderMode()
        {
            if (state?.variationFolders == null) return false;

            foreach (var f in state.variationFolders)
            {
                if (f == null) continue;

                string p = AssetDatabase.GetAssetPath(f);
                if (!string.IsNullOrEmpty(p) && AssetDatabase.IsValidFolder(p))
                    return true;
            }

            return false;
        }
        private int GetLayerCount()
        {
            if (IsFolderMode())
                return state.variationFolders.Count;

            int count = 0;

            if (state.genAlbedo)
                count = Mathf.Max(count, state.albedo.Count);

            if (state.genNormal)
                count = Mathf.Max(count, state.normal.Count);

            if (state.genMaskArray)
                count = Mathf.Max(count, state.mask.Count);

            return count;
        }

        private bool AnyArrayEnabled() => state.genAlbedo || state.genNormal || state.genMaskArray;

        #endregion

        #region Output Setup Validation + Helpers

        private bool ValidateOutputSetup(out string message, out MessageType type)
        {
            if (state.outputFolder == null)
            {
                message = "Select an Output Folder (inside Assets/).";
                type = MessageType.Error;
                return false;
            }

            string folderPath = AssetDatabase.GetAssetPath(state.outputFolder);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath) || !folderPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                message = "Output Folder must be a valid folder inside Assets/.";
                type = MessageType.Error;
                return false;
            }

            string clean = SanitizeName(state.descriptiveName);
            if (string.IsNullOrWhiteSpace(clean))
            {
                message = "Descriptive Name is required (example: RockyGround).";
                type = MessageType.Error;
                return false;
            }

            message = $"Output OK.\nFolder: {folderPath}\nName: {clean}";
            type = MessageType.Info;
            return true;
        }

        private static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim().Replace(' ', '_');
            var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray();
            return new string(chars);
        }

        private static bool TryCreateOrReplaceAsset(UnityEngine.Object asset, string path, string friendlyName, out bool skipped)
        {
            skipped = false;

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (existing != null)
            {
                int r = EditorUtility.DisplayDialogComplex(
                    "Overwrite asset?",
                    $"An asset already exists:\n\n{path}\n\nDo you want to overwrite {friendlyName}?",
                    "Overwrite",
                    "Skip",
                    "Cancel"
                );

                if (r == 2) return false; // cancel
                if (r == 1)
                {
                    skipped = true;
                    UnityEngine.Object.DestroyImmediate(asset);
                    return true;
                }

                AssetDatabase.DeleteAsset(path);
            }

            AssetDatabase.CreateAsset(asset, path);
            return true;
        }

        private string GetOutputFolderPath() => AssetDatabase.GetAssetPath(state.outputFolder);

        #endregion

        #region Import Settings (Auto, always-on)

        private Dictionary<string, TextureSemantic> CollectTexturesForImportSettings()
        {
            var map = new Dictionary<string, TextureSemantic>(StringComparer.OrdinalIgnoreCase);

            void AddTex(Texture2D tex, TextureSemantic semantic)
            {
                if (tex == null) return;

                string path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path)) return;

                map[path] = semantic;
            }

            // -------------------------
            // Manual slots
            // -------------------------
            foreach (var t in state.albedo) AddTex(t, TextureSemantic.BaseColor);
            foreach (var t in state.normal) AddTex(t, TextureSemantic.Normal);
            foreach (var t in state.mask) AddTex(t, TextureSemantic.UnityMaskMap);

            AddTex(state.heightR, TextureSemantic.Height);
            AddTex(state.heightG, TextureSemantic.Height);
            AddTex(state.heightB, TextureSemantic.Height);
            AddTex(state.heightA, TextureSemantic.Height);

            if (state.maskSources != null)
            {
                foreach (var ms in state.maskSources)
                {
                    if (ms == null) continue;

                    AddTex(ms.unityMaskMap, TextureSemantic.UnityMaskMap);
                    AddTex(ms.arm, TextureSemantic.ARM);
                    AddTex(ms.orm, TextureSemantic.ORM);
                    AddTex(ms.metallic, TextureSemantic.Metallic);
                    AddTex(ms.ao, TextureSemantic.AO);
                    AddTex(ms.smoothness, TextureSemantic.Smoothness);
                    AddTex(ms.roughness, TextureSemantic.Roughness);
                }
            }

            // -------------------------
            // Folders (detección por naming)
            // -------------------------
            if (state.variationFolders != null)
            {
                foreach (var folderAsset in state.variationFolders)
                {
                    if (folderAsset == null) continue;

                    string folderPath = AssetDatabase.GetAssetPath(folderAsset);
                    if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                        continue;

                    string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
                    if (guids == null) continue;

                    foreach (var g in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(g);
                        if (string.IsNullOrEmpty(path)) continue;

                        if (!state.includeSubfolders && !IsDirectChild(folderPath, path))
                            continue;

                        string name = Path.GetFileNameWithoutExtension(path);

                        if (EndsWithIgnoreCase(name, state.baseColorSuffix)) map[path] = TextureSemantic.BaseColor;
                        else if (EndsWithIgnoreCase(name, state.normalSuffix)) map[path] = TextureSemantic.Normal;
                        else if (EndsWithIgnoreCase(name, state.unityMaskMapSuffix)) map[path] = TextureSemantic.UnityMaskMap;
                        else if (EndsWithIgnoreCase(name, state.heightSuffix)) map[path] = TextureSemantic.Height;
                        else if (EndsWithIgnoreCase(name, state.armSuffix)) map[path] = TextureSemantic.ARM;
                        else if (EndsWithIgnoreCase(name, state.ormSuffix)) map[path] = TextureSemantic.ORM;
                        else if (EndsWithIgnoreCase(name, state.metallicSuffix)) map[path] = TextureSemantic.Metallic;
                        else if (EndsWithIgnoreCase(name, state.aoSuffix)) map[path] = TextureSemantic.AO;
                        else if (EndsWithIgnoreCase(name, state.smoothnessSuffix)) map[path] = TextureSemantic.Smoothness;
                        else if (EndsWithIgnoreCase(name, state.roughnessSuffix)) map[path] = TextureSemantic.Roughness;
                    }
                }
            }

            return map;
        }

        private static bool IsLinearSemantic(TextureSemantic semantic)
        {
            switch (semantic)
            {
                case TextureSemantic.BaseColor:
                    return false;
                default:
                    return true;
            }
        }

        private void ApplyImportSettingsAuto()
        {
            var textures = CollectTexturesForImportSettings();
            if (textures.Count == 0) return;

            PropertyInfo flipProp = typeof(TextureImporter).GetProperty(
                "flipGreenChannel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kv in textures)
                {
                    string path = kv.Key;
                    TextureSemantic semantic = kv.Value;

                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null) continue;

                    bool dirty = false;

                    // Max Texture Size
                    if (importer.maxTextureSize != state.maxTextureSize)
                    {
                        importer.maxTextureSize = state.maxTextureSize;
                        dirty = true;
                    }

                    // Color space / texture type
                    if (semantic == TextureSemantic.Normal)
                    {
                        if (importer.textureType != TextureImporterType.NormalMap)
                        {
                            importer.textureType = TextureImporterType.NormalMap;
                            dirty = true;
                        }

                        if (importer.sRGBTexture)
                        {
                            importer.sRGBTexture = false;
                            dirty = true;
                        }

                        if (flipProp != null)
                        {
                            bool current = false;
                            try { current = (bool)flipProp.GetValue(importer); } catch { }

                            if (current != state.flipGreenChannel)
                            {
                                try
                                {
                                    flipProp.SetValue(importer, state.flipGreenChannel);
                                    dirty = true;
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        bool shouldBeLinear = IsLinearSemantic(semantic);

                        if (importer.sRGBTexture == shouldBeLinear)
                        {
                            importer.sRGBTexture = !shouldBeLinear;
                            dirty = true;
                        }
                    }

                    if (dirty)
                        importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }



        #endregion

        #region Per-Folder MaskMap Saving (Masks tab only)

        private bool GenerateAndSavePerFolderMaskMaps()
        {
            if (state.maskSources == null || state.maskSources.Count == 0)
            {
                Debug.LogWarning($"{LogPrefix} No variation mask sources available.");
                return true;
            }

            bool hasVariationFolders = state.variationFolders.Any(f => f != null);
            string fallbackFolder = ValidateOutputSetup(out _, out _) ? GetOutputFolderPath() : "Assets";

            var restoreReadable = new List<(string path, bool wasReadable)>();

            try
            {
                for (int i = 0; i < state.maskSources.Count; i++)
                {
                    var src = state.maskSources[i];
                    if (src == null) continue;

                    if (src.unityMaskMap != null) continue;

                    string targetFolder = fallbackFolder;
                    if (hasVariationFolders && src.folder != null)
                    {
                        string fp = AssetDatabase.GetAssetPath(src.folder);
                        if (!string.IsNullOrEmpty(fp) && AssetDatabase.IsValidFolder(fp))
                            targetFolder = fp;
                    }

                    string baseName = string.IsNullOrWhiteSpace(src.suggestedBaseName) ? SuggestBaseName(src) : src.suggestedBaseName;
                    if (string.IsNullOrWhiteSpace(baseName))
                        baseName = $"Variation_{i:00}";

                    string preferredPath = $"{targetFolder}/{baseName}{GeneratedUnityMaskSuffix}.asset";

                    var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(preferredPath);
                    if (existing != null)
                    {
                        int r = EditorUtility.DisplayDialogComplex(
                            "Overwrite MaskMap?",
                            $"A MaskMap already exists:\n\n{preferredPath}\n\nOverwrite it?",
                            "Overwrite",
                            "Skip",
                            "Cancel"
                        );

                        if (r == 2) return false;
                        if (r == 1) continue;

                        AssetDatabase.DeleteAsset(preferredPath);
                    }

                    if (!TryBuildUnityMaskTexture(src, out var maskTex, out var buildWarning, restoreReadable))
                    {
                        Debug.LogWarning($"{LogPrefix} Could not build MaskMap for variation #{i}.");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(buildWarning))
                        Debug.LogWarning($"{LogPrefix} MaskMap variation #{i}: {buildWarning}");

                    maskTex.name = Path.GetFileNameWithoutExtension(preferredPath);

                    AssetDatabase.CreateAsset(maskTex, preferredPath);
                    AssetDatabase.ImportAsset(preferredPath);
                    ApplyGeneratedMaskImportSettings(preferredPath);
                    AssetDatabase.SaveAssets();
                    var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(preferredPath);

                    src.unityMaskMap = saved;

                    if (state.genMaskArray && i < state.mask.Count)
                        state.mask[i] = saved;

                    Debug.Log($"{LogPrefix} Generated MaskMap: {preferredPath}");
                }

                AssetDatabase.Refresh();
                return true;
            }
            finally
            {
                RestoreReadableFlags(restoreReadable);
            }
        }

        private string SuggestBaseName(MaskSources src)
        {
            Texture2D pick =
                src.unityMaskMap ??
                src.arm ?? src.orm ??
                src.metallic ?? src.ao ?? src.smoothness ?? src.roughness;

            if (pick == null) return "";

            string path = AssetDatabase.GetAssetPath(pick);
            if (string.IsNullOrEmpty(path)) return pick.name;

            string name = Path.GetFileNameWithoutExtension(path);

            string[] suffixes =
            {
                state.unityMaskMapSuffix,
                state.armSuffix, state.ormSuffix,
                state.metallicSuffix, state.aoSuffix, state.smoothnessSuffix, state.roughnessSuffix,
                state.baseColorSuffix, state.normalSuffix, state.heightSuffix
            };

            foreach (var s in suffixes)
            {
                if (!string.IsNullOrEmpty(s) && EndsWithIgnoreCase(name, s))
                {
                    name = name.Substring(0, name.Length - s.Length);
                    name = name.TrimEnd('_', '-', ' ');
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(name))
                name = pick.name;

            return name;
        }

        #endregion

        #region Mask Building (shared)

        private bool TryBuildMaskMapRGBA32_ForArray(
     int layer,
     int w,
     int h,
     out Texture2D tempRGBA,
     out string reason,
     List<(string path, bool wasReadable)> restoreReadable)
        {
            tempRGBA = null;
            reason = "";

            // ¿Hay folder válido en este layer?
            bool hasFolder =
                state.variationFolders != null &&
                layer >= 0 &&
                layer < state.variationFolders.Count &&
                state.variationFolders[layer] != null &&
                AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(state.variationFolders[layer]));

            // Slot manual SOLO si NO hay folder
            Texture2D slot =
                (!hasFolder && state.mask != null && layer >= 0 && layer < state.mask.Count)
                    ? state.mask[layer]
                    : null;

            // Construimos ms:
            // - con folder: re-scan SIEMPRE
            // - sin folder: usar state.maskSources
            MaskSources ms;

            if (hasFolder)
            {
                ms = new MaskSources();
                ms.folder = state.variationFolders[layer];

                string folderPath = AssetDatabase.GetAssetPath(ms.folder);
                FindMapsInFolder(folderPath,
                    out _,
                    out _,
                    out var unityMaskMap,
                    out _,
                    out var arm,
                    out var orm,
                    out var metallic,
                    out var ao,
                    out var smooth,
                    out var rough
                );

                ms.unityMaskMap = unityMaskMap;
                ms.arm = arm;
                ms.orm = orm;
                ms.metallic = metallic;
                ms.ao = ao;
                ms.smoothness = smooth;
                ms.roughness = rough;
            }
            else
            {
                ms = (state.maskSources != null && layer >= 0 && layer < state.maskSources.Count)
                    ? state.maskSources[layer]
                    : null;
                ms ??= new MaskSources();
            }

            // Helper: validar resolución
            bool ResOk(Texture2D t) => t != null && t.width == w && t.height == h;

            // -------------------------
            // 0) Slot manual (solo sin folder)
            // -------------------------
            if (ResOk(slot))
            {
                EnsureReadableIfNeeded(slot, restoreReadable);
                var px = SafeGetPixels32(slot);

                tempRGBA = new Texture2D(w, h, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                if (px != null && px.Length == w * h) tempRGBA.SetPixels32(px);
                else FillDefaultMaskPixels(tempRGBA, w, h);

                tempRGBA.Apply(state.generateMipmaps, false);
                reason = "Slot manual usado (sin folder).";
                return true;
            }

            // -------------------------
            // 1) Unity MaskMap
            // -------------------------
            if (ResOk(ms.unityMaskMap))
            {
                EnsureReadableIfNeeded(ms.unityMaskMap, restoreReadable);
                var px = SafeGetPixels32(ms.unityMaskMap);

               

                tempRGBA = new Texture2D(w, h, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                if (px != null && px.Length == w * h) tempRGBA.SetPixels32(px);
                else FillDefaultMaskPixels(tempRGBA, w, h);

                tempRGBA.Apply(state.generateMipmaps, false);
                reason = hasFolder ? "Folder: Unity _MaskMap usado." : "Step 1: Unity _MaskMap usado.";
                return true;
            }

            // -------------------------
            // 2) ARM / ORM (AO=R, Rough=G, Metal=B)
            // -------------------------
            Texture2D packed = ResOk(ms.arm) ? ms.arm : (ResOk(ms.orm) ? ms.orm : null);
            if (packed != null)
            {
                EnsureReadableIfNeeded(packed, restoreReadable);
                var p = SafeGetPixels32(packed);
                if (p == null || p.Length != w * h)
                {
                    reason = "Step 2: ARM/ORM no readable -> default.";
                    tempRGBA = MakeDefaultMaskTex(w, h);
                    return true;
                }

                tempRGBA = new Texture2D(w, h, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                var outPx = new Color32[w * h];
                for (int i = 0; i < outPx.Length; i++)
                {
                    byte ao = p[i].r;
                    byte rough = p[i].g;
                    byte metal = p[i].b;
                    byte smooth = (byte)(255 - rough);
                    outPx[i] = new Color32(metal, ao, 0, smooth);
                }

                tempRGBA.SetPixels32(outPx);
                tempRGBA.Apply(state.generateMipmaps, false);
                reason = "Step 2: Built from ARM/ORM.";
                return true;
            }

            // Singles
            Texture2D tM = ResOk(ms.metallic) ? ms.metallic : null;
            Texture2D tAO = ResOk(ms.ao) ? ms.ao : null;
            Texture2D tS = ResOk(ms.smoothness) ? ms.smoothness : null;
            Texture2D tR = ResOk(ms.roughness) ? ms.roughness : null;

            if ((ms.metallic != null && tM == null) ||
                (ms.ao != null && tAO == null) ||
                (ms.smoothness != null && tS == null) ||
                (ms.roughness != null && tR == null))
            {
                reason = "Singles resolution mismatch -> default.";
                tempRGBA = MakeDefaultMaskTex(w, h);
                return true;
            }

            EnsureReadableIfNeeded(tM, restoreReadable);
            EnsureReadableIfNeeded(tAO, restoreReadable);
            EnsureReadableIfNeeded(tS, restoreReadable);
            EnsureReadableIfNeeded(tR, restoreReadable);

            var pM = (tM != null) ? SafeGetPixels32(tM) : null;
            var pAO = (tAO != null) ? SafeGetPixels32(tAO) : null;
            var pS = (tS != null) ? SafeGetPixels32(tS) : null;
            var pR = (tR != null) ? SafeGetPixels32(tR) : null;

            // -------------------------
            // 5) Metallic + AO + Metallic alpha como Smoothness
            // -------------------------
            if (tM != null && tAO != null && tS == null && tR == null && pM != null && pAO != null)
            {
                bool metallicAlphaUseful = false;
                int len = w * h;

                if (pM.Length == len)
                {
                    byte minA = 255;
                    byte maxA = 0;

                    for (int i = 0; i < len; i++)
                    {
                        byte a = pM[i].a;
                        if (a < minA) minA = a;
                        if (a > maxA) maxA = a;
                        if (minA != maxA)
                        {
                            metallicAlphaUseful = true;
                            break;
                        }
                    }
                }

                if (metallicAlphaUseful)
                {
                    tempRGBA = new Texture2D(w, h, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
                    {
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };

                    var outPx = new Color32[len];
                    for (int i = 0; i < len; i++)
                    {
                        byte metal = pM[i].r;
                        byte ao = pAO[i].r;
                        byte smooth = pM[i].a;

                        outPx[i] = new Color32(metal, ao, 0, smooth);
                    }

                    tempRGBA.SetPixels32(outPx);
                    tempRGBA.Apply(state.generateMipmaps, false);
                    reason = "Step 5: Built from Metallic + AO + Metallic alpha as Smoothness.";
                    return true;
                }
            }

            // Parcial + defaults
            tempRGBA = BuildFromSingles_ToTex(w, h, pM, pAO, pS, pR, invertRoughness: true);
            reason = "Step 6: Built from partial singles + defaults.";
            return true;
        }

        private Texture2D MakeDefaultMaskTex(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            FillDefaultMaskPixels(t, w, h);
            t.Apply(state.generateMipmaps, false);
            return t;
        }

        private void FillDefaultMaskPixels(Texture2D tex, int w, int h)
        {
            const byte MID = 128;
            var fill = new Color32[w * h];
            for (int i = 0; i < fill.Length; i++)
                fill[i] = new Color32(0, 255, 0, MID); // Metal=0, AO=1, Smooth=0.5
            tex.SetPixels32(fill);
        }

        private Texture2D BuildFromSingles_ToTex(
         int w, int h,
         Color32[] pM,
         Color32[] pAO,
         Color32[] pS,
         Color32[] pR,
         bool invertRoughness)
            {
                const byte MID = 128;
                int len = w * h;

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                var outPx = new Color32[len];

                for (int i = 0; i < len; i++)
                {
                    byte metal = (pM != null && pM.Length == len) ? pM[i].r : (byte)0;
                    byte ao = (pAO != null && pAO.Length == len) ? pAO[i].r : (byte)255;

                    byte smooth;
                    if (pS != null && pS.Length == len) smooth = pS[i].r;
                    else if (pR != null && pR.Length == len)
                    {
                        byte rough = pR[i].r;
                        smooth = invertRoughness ? (byte)(255 - rough) : rough;
                    }
                    else smooth = MID;

                    outPx[i] = new Color32(metal, ao, 0, smooth);
                }

                tex.SetPixels32(outPx);
                tex.Apply(state.generateMipmaps, false);
                return tex;
        }

        private bool HasAnyMaskSources(MaskSources src)
        {
            if (src == null) return false;
            return src.unityMaskMap != null ||
                   src.arm != null || src.orm != null ||
                   src.metallic != null || src.ao != null || src.smoothness != null || src.roughness != null;
        }

        private bool TryGetMaskResolution(MaskSources src, out int w, out int h)
        {
            w = 0; h = 0;
            if (src == null) return false;

            Texture2D pick =
                src.unityMaskMap ??
                src.arm ?? src.orm ??
                src.metallic ?? src.ao ?? src.smoothness ?? src.roughness;

            if (pick == null) return false;

            w = pick.width;
            h = pick.height;
            return w > 0 && h > 0;
        }

        /// <summary>
        /// Builds Unity MaskMap (M/AO/S):
        /// Case 1: ARM or ORM => AO=R, Rough=G, Metal=B; Smooth=1-Rough
        /// Case 2: Metallic + AO + Smoothness
        /// Case 3: missing channels with defaults + roughness inversion + metallic alpha fallback
        /// </summary>
        private bool TryBuildUnityMaskTexture(MaskSources src, out Texture2D outTex, out string warning, List<(string path, bool wasReadable)> restoreReadable)
        {
            outTex = null;
            warning = "";

            if (src.unityMaskMap != null)
            {
                warning = "Unity MaskMap already present; build skipped.";
                return false;
            }

            bool useARM = src.arm != null;
            bool useORM = !useARM && src.orm != null;

            var candidates = new List<Texture2D> { src.arm, src.orm, src.metallic, src.ao, src.smoothness, src.roughness }
                .Where(t => t != null).ToList();

            if (candidates.Count == 0)
            {
                warning = "No usable sources (ARM/ORM/Metallic/AO/Smoothness/Roughness).";
                return false;
            }

            int w = candidates[0].width;
            int h = candidates[0].height;

            foreach (var t in candidates)
            {
                if (t.width != w || t.height != h)
                {
                    warning = "Source textures have mismatched resolutions. MaskMap not generated.";
                    return false;
                }
            }

            EnsureReadableIfNeeded(useARM || useORM ? (useARM ? src.arm : src.orm) : null, restoreReadable);
            EnsureReadableIfNeeded(src.metallic, restoreReadable);
            EnsureReadableIfNeeded(src.ao, restoreReadable);
            EnsureReadableIfNeeded(src.smoothness, restoreReadable);
            EnsureReadableIfNeeded(src.roughness, restoreReadable);

            Color32[] pPacked = null;
            if (useARM && src.arm != null) pPacked = SafeGetPixels32(src.arm);
            if (useORM && src.orm != null) pPacked = SafeGetPixels32(src.orm);

            Color32[] pMetal = (!useARM && !useORM && src.metallic != null) ? SafeGetPixels32(src.metallic) : null;
            Color32[] pAO = (!useARM && !useORM && src.ao != null) ? SafeGetPixels32(src.ao) : null;
            Color32[] pSmooth = (!useARM && !useORM && src.smoothness != null) ? SafeGetPixels32(src.smoothness) : null;
            Color32[] pRough = (!useARM && !useORM && src.roughness != null) ? SafeGetPixels32(src.roughness) : null;

            int len = w * h;

            bool metallicAlphaUseful = false;

            // --- SAFETY GUARDS (prevent NullReference / Index issues) ---
            if ((useARM || useORM))
            {
                if (pPacked == null)
                {
                    warning = (useARM ? "ARM" : "ORM") + " source is not readable (GetPixels32 returned null).";
                    return false;
                }
                if (pPacked.Length != len)
                {
                    warning = (useARM ? "ARM" : "ORM") + $" pixels length mismatch. Expected {len}, got {pPacked.Length}.";
                    return false;
                }
            }
            else
            {
                if (pMetal != null && pMetal.Length != len) pMetal = null;
                if (pAO != null && pAO.Length != len) pAO = null;
                if (pSmooth != null && pSmooth.Length != len) pSmooth = null;
                if (pRough != null && pRough.Length != len) pRough = null;

                // Now safe to scan metallic alpha (if we still have pMetal)
                if (pMetal != null)
                {
                    byte minA = 255, maxA = 0;
                    for (int i = 0; i < len; i++)
                    {
                        byte a = pMetal[i].a;
                        if (a < minA) minA = a;
                        if (a > maxA) maxA = a;
                        if (maxA != minA) { metallicAlphaUseful = true; break; }
                    }
                }
            }


            const byte MID = 128;

            outTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var outPixels = new Color32[len];

            for (int i = 0; i < len; i++)
            {
                byte metallic;
                byte ao;
                byte smooth;

                if (useARM || useORM)
                {
                    // pPacked validated above
                    byte aoPacked = pPacked[i].r;
                    byte roughPacked = pPacked[i].g;
                    byte metalPacked = pPacked[i].b;

                    ao = aoPacked;
                    metallic = metalPacked;
                    smooth = (byte)(255 - roughPacked);
                }
                else
                {
                    metallic = (pMetal != null) ? pMetal[i].r : (byte)0;
                    ao = (pAO != null) ? pAO[i].r : (byte)255;

                    if (pSmooth != null) smooth = pSmooth[i].r;
                    else if (pRough != null) smooth = (byte)(255 - pRough[i].r);
                    else if (pMetal != null && metallicAlphaUseful) smooth = pMetal[i].a;
                    else smooth = MID;
                }

                // URP MaskMap: R=Metallic, G=AO, B unused, A=Smoothness
                outPixels[i] = new Color32(metallic, ao, 0, smooth);
            }

            outTex.SetPixels32(outPixels);
            outTex.Apply(false, false);

            var wlist = new List<string>();
            if (!useARM && !useORM)
            {
                if (src.metallic == null) wlist.Add("Metallic missing -> black");
                if (src.ao == null) wlist.Add("AO missing -> white");
                if (src.smoothness == null && src.roughness == null)
                {
                    if (src.metallic != null && metallicAlphaUseful) wlist.Add("Smoothness from Metallic alpha");
                    else wlist.Add("Smoothness missing -> mid gray");
                }
                else if (src.smoothness == null && src.roughness != null)
                {
                    wlist.Add("Smoothness from inverted Roughness");
                }
            }
            else
            {
                wlist.Add((useARM ? "ARM" : "ORM") + " used (AO=R, Rough=G, Metal=B; Smooth=1-Rough)");
            }

            warning = string.Join("; ", wlist);
            return true;
        }

        private static Color32[] SafeGetPixels32(Texture2D tex)
        {
            try { return tex.GetPixels32(); }
            catch { return null; }
        }

        private static void EnsureReadableIfNeeded(Texture2D tex, List<(string path, bool wasReadable)> restoreReadable)
        {
            if (tex == null) return;

            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;

            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;

            if (restoreReadable.Any(x => string.Equals(x.path, path, StringComparison.OrdinalIgnoreCase)))
                return;

            restoreReadable.Add((path, imp.isReadable));
            if (!imp.isReadable)
            {
                imp.isReadable = true;
                imp.SaveAndReimport();
            }
        }

        private static void RestoreReadableFlags(List<(string path, bool wasReadable)> restoreReadable)
        {
            foreach (var item in restoreReadable)
            {
                var imp = AssetImporter.GetAtPath(item.path) as TextureImporter;
                if (imp == null) continue;

                if (imp.isReadable != item.wasReadable)
                {
                    imp.isReadable = item.wasReadable;
                    imp.SaveAndReimport();
                }
            }
        }

        private void ApplyGeneratedMaskImportSettings(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            bool dirty = false;

            if (importer.sRGBTexture)
            {
                importer.sRGBTexture = false;
                dirty = true;
            }

            if (importer.mipmapEnabled != state.generateMipmaps)
            {
                importer.mipmapEnabled = state.generateMipmaps;
                dirty = true;
            }

            if (importer.wrapMode != state.wrapMode)
            {
                importer.wrapMode = state.wrapMode;
                dirty = true;
            }

            if (importer.filterMode != state.filterMode)
            {
                importer.filterMode = state.filterMode;
                dirty = true;
            }

            if (importer.anisoLevel != state.anisoLevel)
            {
                importer.anisoLevel = state.anisoLevel;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();
        }

        #endregion

        #region Arrays Validation + Build (Mode A for MaskMap)

        private bool ValidateArrays(out string message, out MessageType type)
        {
            if (!AnyArrayEnabled())
            {
                message = "No arrays enabled. Enable at least one Texture Array in Setup → Output setup.";
                type = MessageType.Error;
                return false;
            }

            int layers = GetLayerCount();

            if (layers <= 0)
            {
                message =
                    "No layers detected.\n\n" +
                    "Either:\n" +
                    "- Add Variation Folders in Setup tab\n" +
                    "or\n" +
                    "- Assign textures manually in Texture Arrays tab.";
                type = MessageType.Error;
                return false;
            }

            if (state.genAlbedo) ResizeList(state.albedo, layers);
            if (state.genNormal) ResizeList(state.normal, layers);
            if (state.genMaskArray) ResizeList(state.mask, layers);

            var missing = new List<string>();

            if (state.genAlbedo) missing.AddRange(FindMissingSlots(state.albedo, "BaseColor"));
            if (state.genNormal) missing.AddRange(FindMissingSlots(state.normal, "Normal"));

            // MaskMap array in Mode A: do NOT require _MaskMap, require "buildable" sources
            if (state.genMaskArray)
            {
                EnsureMaskSourcesSize(layers);

                for (int i = 0; i < layers; i++)
                {
                    var ms = state.maskSources[i];

                    // If slot exists, fine. If missing, must be buildable.
                    if (state.mask[i] != null) continue;

                    if (ms == null || !HasAnyMaskSources(ms))
                        missing.Add($"MaskMap: missing at layer #{i} (no _MaskMap and no sources to build one)");
                    else
                    {
                        // If no existing _MaskMap, require at least one of ARM/ORM or any of the singles
                        bool ok = (ms.arm != null || ms.orm != null || ms.metallic != null || ms.ao != null || ms.smoothness != null || ms.roughness != null);
                        if (!ok)
                            missing.Add($"MaskMap: missing at layer #{i} (no ARM/ORM/Metallic/AO/Smooth/Rough)");
                    }
                }
            }

            if (missing.Count > 0)
            {
                message = "Fix missing array slots:\n" + string.Join("\n", missing.Take(12)) + (missing.Count > 12 ? "\n... (more)" : "");
                type = MessageType.Error;
                return false;
            }

            var issues = new List<string>();
            if (state.genAlbedo && !ValidateUniformForCopy(state.albedo, "BaseColor", out var aIssue)) issues.Add(aIssue);
            if (state.genNormal && !ValidateUniformForCopy(state.normal, "Normal", out var nIssue)) issues.Add(nIssue);

            // MaskMap array uses RGBA32 fallback builder, so we validate consistent resolution only
            if (state.genMaskArray && !ValidateMaskArrayResolution(out var mmIssue))
                issues.Add(mmIssue);

            if (issues.Count > 0)
            {
                message = string.Join("\n\n", issues);
                type = MessageType.Error;
                return false;
            }

            message = "Arrays ready to build.";
            type = MessageType.Info;
            return true;
        }

        private void EnsureMaskSourcesSize(int layers)
        {
            while (state.maskSources.Count < layers) state.maskSources.Add(new MaskSources());
            while (state.maskSources.Count > layers) state.maskSources.RemoveAt(state.maskSources.Count - 1);
        }

        private bool ValidateMaskArrayResolution(out string issue)
        {
            issue = null;

            int layers = GetLayerCount();
            EnsureMaskSourcesSize(layers);

            // Find first available resolution
            int w = 0, h = 0;
            bool found = false;

            for (int i = 0; i < layers; i++)
            {
                if (state.mask[i] != null)
                {
                    w = state.mask[i].width;
                    h = state.mask[i].height;
                    found = true;
                    break;
                }

                if (TryGetMaskResolution(state.maskSources[i], out w, out h))
                {
                    found = true;
                    break;
                }
            }

            if (!found || w <= 0 || h <= 0)
            {
                issue = "MaskMap: could not determine resolution (no sources).";
                return false;
            }

            // Check all layers match
            for (int i = 0; i < layers; i++)
            {
                if (state.mask[i] != null)
                {
                    if (state.mask[i].width != w || state.mask[i].height != h)
                    {
                        issue =
                        $"MaskMap: resolution mismatch at layer #{i}.\n\n" +
                        $"Expected: {w}x{h}\n" +
                        $"Found: {state.mask[i].width}x{state.mask[i].height}\n\n" +
                        $"All MaskMap layers must have identical resolution.\n" +
                        $"Fix it by making all source textures match, or use 'Max Texture Size' to force a common resolution.";
                        return false;
                    }
                }
                else
                {
                    if (!TryGetMaskResolution(state.maskSources[i], out int lw, out int lh))
                    {
                        // already validated buildability earlier
                        continue;
                    }

                    if (lw != w || lh != h)
                    {
                        issue =
                        $"MaskMap: resolution mismatch at layer #{i}.\n\n" +
                        $"Expected: {w}x{h}\n" +
                        $"Found from sources: {lw}x{lh}\n\n" +
                        $"All MaskMap layers must have identical resolution.\n" +
                        $"Fix it by making all source textures match, or use 'Max Texture Size' to force a common resolution.";
                        return false;
                    }
                }
            }

            return true;
        }

        private static IEnumerable<string> FindMissingSlots(List<Texture2D> list, string label)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == null)
                    yield return $"{label}: missing at layer #{i}";
        }

        private static bool ValidateUniformForCopy(List<Texture2D> list, string label, out string issue)
        {
            issue = null;
            var src = list.Where(t => t != null).ToList();
            if (src.Count == 0)
            {
                issue = $"{label}: no textures.";
                return false;
            }

            int w = src[0].width;
            int h = src[0].height;
            var fmt = src[0].format;

            foreach (var t in src)
            {
                if (t.width != w || t.height != h)
                {
                    issue =
                        $"{label}: resolution mismatch.\n\n" +
                        $"Expected: {w}x{h}\n" +
                        $"Found: {t.name} -> {t.width}x{t.height}\n\n" +
                        $"All textures used in a Texture Array must have identical resolution.\n" +
                        $"Fix it by making all source textures match, or use 'Max Texture Size' to force a common resolution.";
                    return false;
                }

                if (t.format != fmt)
                {
                    issue = $"{label}: format mismatch (CopyTexture needs consistent formats).\n" +
                            $"First: {fmt}, found {t.name}: {t.format}.\n" +
                            "Fix: ensure same compression/format in import settings.";
                    return false;
                }
            }

            return true;
        }

        private bool GenerateSelectedArrays_NoDialogs_WithOverwritePrompt()
        {
            bool anyFolder = state.variationFolders != null && state.variationFolders.Any(f => f != null);

            if (anyFolder)
                AutoFillFromFolders();

            // En manual, esto no hace nada todavía (ver punto 3), pero no molesta:
            ApplyImportSettingsAuto();

            string cleanName = SanitizeName(state.descriptiveName);
            string folder = GetOutputFolderPath();

            // 1) Construir en memoria (sin StartAssetEditing)
            Texture2DArray arrAlbedo = null;
            Texture2DArray arrNormal = null;
            Texture2DArray arrMask = null;

            if (state.genAlbedo) arrAlbedo = BuildArray(state.albedo, linear: false);
            if (state.genNormal) arrNormal = BuildArray(state.normal, linear: true);
            if (state.genMaskArray) arrMask = BuildMaskMapArrayWithFallback();

            // 2) Guardar assets (aquí sí, si quieres)
            AssetDatabase.StartAssetEditing();
            try
            {
                if (state.genAlbedo)
                {
                    string path = $"{folder}/TA_{cleanName}_BaseColor.asset";
                    if (!TryCreateOrReplaceAsset(arrAlbedo, path, "TA BaseColor", out _)) return false;
                }

                if (state.genNormal)
                {
                    string path = $"{folder}/TA_{cleanName}_Normal.asset";
                    if (!TryCreateOrReplaceAsset(arrNormal, path, "TA Normal", out _)) return false;
                }

                if (state.genMaskArray)
                {
                    string path = $"{folder}/TA_{cleanName}_MaskMap.asset";
                    if (!TryCreateOrReplaceAsset(arrMask, path, "TA MaskMap", out _)) return false;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log($"{LogPrefix} Arrays saved to: {folder}");
            return true;
        }

        private Texture2DArray BuildArray(List<Texture2D> list, bool linear)
        {
            var sources = list.Where(t => t != null).ToList();

            int width = sources[0].width;
            int height = sources[0].height;
            int depth = sources.Count;
            var format = sources[0].format;

            var array = new Texture2DArray(width, height, depth, format, state.generateMipmaps, linear)
            {
                filterMode = state.filterMode,
                wrapMode = state.wrapMode,
                anisoLevel = state.anisoLevel
            };

            for (int layer = 0; layer < depth; layer++)
            {
                var src = sources[layer];
                int mipCount = state.generateMipmaps ? src.mipmapCount : 1;

                for (int mip = 0; mip < mipCount; mip++)
                    Graphics.CopyTexture(src, 0, mip, array, layer, mip);
            }

            array.Apply(false, false);
            return array;
        }

        /// <summary>
        /// Builds MaskMap array using RGBA32 to avoid CopyTexture format constraints.
        /// For each layer:
        /// - if a Unity _MaskMap exists -> converts to RGBA32 temp (readable) and copies
        /// - else -> builds temporary RGBA32 mask from sources and copies
        /// Nothing is saved to disk (Mode A).
        /// </summary>
        private Texture2DArray BuildMaskMapArrayWithFallback()
        {
            int layers = GetLayerCount();
            EnsureMaskSourcesSize(layers);
            ResizeList(state.mask, layers);

            // Determine reference resolution
            int w = 0, h = 0;
            bool found = false;

            for (int i = 0; i < layers; i++)
            {
                if (state.mask[i] != null)
                {
                    w = state.mask[i].width;
                    h = state.mask[i].height;
                    found = true;
                    break;
                }

                if (TryGetMaskResolution(state.maskSources[i], out w, out h))
                {
                    found = true;
                    break;
                }
            }

            if (!found) throw new Exception("MaskMapArray: no sources to determine resolution.");

            // Forzar también el tamaño objetivo del MaskMap array al Max Texture Size seleccionado
            w = Mathf.Min(w, state.maxTextureSize);
            h = Mathf.Min(h, state.maxTextureSize);

            var array = new Texture2DArray(w, h, layers, TextureFormat.RGBA32, state.generateMipmaps, linear: true)
            {
                filterMode = state.filterMode,
                wrapMode = state.wrapMode,
                anisoLevel = state.anisoLevel
            };

            var restoreReadable = new List<(string path, bool wasReadable)>();
            var tempsToDestroy = new List<Texture2D>();

            try
            {
                for (int layer = 0; layer < layers; layer++)
                {
                    if (!TryBuildMaskMapRGBA32_ForArray(layer, w, h, out var tempRGBA, out var why, restoreReadable))
                    {
                        tempRGBA = MakeDefaultMaskTex(w, h);
                        why = "Fallback defaults (unexpected false).";
                    }

                    tempsToDestroy.Add(tempRGBA);

                    int mipCount = state.generateMipmaps ? tempRGBA.mipmapCount : 1;
                    for (int mip = 0; mip < mipCount; mip++)
                        Graphics.CopyTexture(tempRGBA, 0, mip, array, layer, mip);

                    // opcional: log debug por layer
                    Debug.Log($"{LogPrefix} MaskMap layer {layer}: {why}");
                }

                array.Apply(false, false);
                return array;
            }
            finally
            {
                foreach (var t in tempsToDestroy)
                {
                    if (t != null) UnityEngine.Object.DestroyImmediate(t);
                }
                RestoreReadableFlags(restoreReadable);
            }
        }

        #endregion

        #region HeightMask Validation + Build (Permissive)

        private bool ValidateHeightMaskPermissive(out string message, out MessageType type)
        {
            var hs = new[] { state.heightR, state.heightG, state.heightB, state.heightA };
            var present = hs.Where(t => t != null).ToList();

            if (present.Count == 0)
            {
                message = "No Height textures found. A 1x1 mid-gray HeightMask will be generated.";
                type = MessageType.Warning;
                return true;
            }

            int w = present[0].width;
            int h = present[0].height;

            foreach (var t in present)
            {
                if (t.width != w || t.height != h)
                {
                    message = "Height textures must share the same resolution (for the ones you provided).";
                    type = MessageType.Error;
                    return false;
                }
            }

            if (present.Count < 4)
            {
                message =
                    $"HeightMask will be generated using {present.Count} height texture(s).\n" +
                    "Missing channels will be filled with mid-gray (0.5).";

                type = MessageType.Warning;
                return true;
            }

            message = $"Height mask ready ({w}x{h}).";
            type = MessageType.Info;
            return true;
        }

        private bool GenerateHeightMask_NoDialogs_Permissive_WithOverwritePrompt()
        {
            string cleanName = SanitizeName(state.descriptiveName);
            string folder = GetOutputFolderPath();
            string path = $"{folder}/T_{cleanName}_HeightMask.asset";

            var hs = new[] { state.heightR, state.heightG, state.heightB, state.heightA };
            var present = hs.Where(t => t != null).ToList();

            int w = present.Count > 0 ? present[0].width : 1;
            int h = present.Count > 0 ? present[0].height : 1;

            var restore = new List<(string path, bool wasReadable)>();
            Texture2D outTex = null;

            try
            {
                foreach (var t in present)
                    EnsureReadableIfNeeded(t, restore);

                outTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false, linear: true)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                const byte mid = 128;

                if (present.Count == 0)
                {
                    outTex.SetPixels32(new[] { new Color32(mid, mid, mid, mid) });
                    outTex.Apply(false, false);

                    if (!TryCreateOrReplaceAsset(outTex, path, "HeightMask", out _))
                        return false;

                    ApplyGeneratedMaskImportSettings(path);
                    return true;
                }

                Color32[] r = (state.heightR != null) ? SafeGetPixels32(state.heightR) : null;
                Color32[] g = (state.heightG != null) ? SafeGetPixels32(state.heightG) : null;
                Color32[] b = (state.heightB != null) ? SafeGetPixels32(state.heightB) : null;
                Color32[] a = (state.heightA != null) ? SafeGetPixels32(state.heightA) : null;

                int len = w * h;
                var packed = new Color32[len];

                for (int i = 0; i < len; i++)
                {
                    byte rr = (r != null && r.Length == len) ? r[i].r : mid;
                    byte gg = (g != null && g.Length == len) ? g[i].r : mid;
                    byte bb = (b != null && b.Length == len) ? b[i].r : mid;
                    byte aa = (a != null && a.Length == len) ? a[i].r : mid;
                    packed[i] = new Color32(rr, gg, bb, aa);
                }

                outTex.SetPixels32(packed);
                outTex.Apply(false, false);

                if (!TryCreateOrReplaceAsset(outTex, path, "HeightMask", out _))
                    return false;

                ApplyGeneratedMaskImportSettings(path);
                return true;
            }
            finally
            {
                RestoreReadableFlags(restore);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        #endregion

        #region Prefs

        private void RestorePrefs()
        {
            string folderPath = EditorPrefs.GetString(PrefKeyOutputFolder, "");
            if (!string.IsNullOrEmpty(folderPath))
            {
                var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath);
                if (folder != null && AssetDatabase.IsValidFolder(folderPath))
                    state.outputFolder = folder;
            }

            state.descriptiveName = EditorPrefs.GetString(PrefKeyDescriptiveName, state.descriptiveName ?? "");
            state.maxTextureSize = EditorPrefs.GetInt(PrefKeyMaxTextureSize, state.maxTextureSize);

            state.baseColorSuffix = EditorPrefs.GetString(PrefKeySuffix_BaseColor, state.baseColorSuffix);
            state.normalSuffix = EditorPrefs.GetString(PrefKeySuffix_Normal, state.normalSuffix);
            state.unityMaskMapSuffix = EditorPrefs.GetString(PrefKeySuffix_UnityMaskMap, state.unityMaskMapSuffix);
            state.heightSuffix = EditorPrefs.GetString(PrefKeySuffix_Height, state.heightSuffix);

            state.armSuffix = EditorPrefs.GetString(PrefKeySuffix_ARM, state.armSuffix);
            state.ormSuffix = EditorPrefs.GetString(PrefKeySuffix_ORM, state.ormSuffix);
            state.metallicSuffix = EditorPrefs.GetString(PrefKeySuffix_Metallic, state.metallicSuffix);
            state.aoSuffix = EditorPrefs.GetString(PrefKeySuffix_AO, state.aoSuffix);
            state.smoothnessSuffix = EditorPrefs.GetString(PrefKeySuffix_Smoothness, state.smoothnessSuffix);
            state.roughnessSuffix = EditorPrefs.GetString(PrefKeySuffix_Roughness, state.roughnessSuffix);

            state.flipGreenChannel = EditorPrefs.GetBool(PrefKeyFlipGreen, state.flipGreenChannel);
            lastSelectedPresetGuid = EditorPrefs.GetString(PrefKeySelectedSuffixPresetGuid, "");
        }

        private void SavePrefs()
        {
            if (state.outputFolder != null)
            {
                string folderPath = AssetDatabase.GetAssetPath(state.outputFolder);
                if (!string.IsNullOrEmpty(folderPath))
                    EditorPrefs.SetString(PrefKeyOutputFolder, folderPath);
            }

            EditorPrefs.SetString(PrefKeyDescriptiveName, state.descriptiveName ?? "");
            EditorPrefs.SetInt(PrefKeyMaxTextureSize, state.maxTextureSize);
            SaveSuffixPrefs();
            EditorPrefs.SetBool(PrefKeyFlipGreen, state.flipGreenChannel);
            EditorPrefs.SetString(PrefKeySelectedSuffixPresetGuid, lastSelectedPresetGuid ?? "");
        }

        private void SaveSuffixPrefs()
        {
            EditorPrefs.SetString(PrefKeySuffix_BaseColor, state.baseColorSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_Normal, state.normalSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_UnityMaskMap, state.unityMaskMapSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_Height, state.heightSuffix ?? "");

            EditorPrefs.SetString(PrefKeySuffix_ARM, state.armSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_ORM, state.ormSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_Metallic, state.metallicSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_AO, state.aoSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_Smoothness, state.smoothnessSuffix ?? "");
            EditorPrefs.SetString(PrefKeySuffix_Roughness, state.roughnessSuffix ?? "");
        }
        #endregion

        #region Suffix Presets

        private void DrawSuffixPresetDropdownUI()
        {
            // Refresca si hace falta (y añade Custom(modified) al final si estamos dirty)
            if (cachedSuffixPresetLabels == null || cachedSuffixPresetLabels.Count == 0)
                RefreshSuffixPresets(includeModifiedEntry: suffixesDirtyFromPreset);

            if (cachedSuffixPresetLabels == null || cachedSuffixPresetLabels.Count == 0)
            {
                EditorGUILayout.HelpBox("No Suffix Presets found in the project.", MessageType.Info);
                return;
            }

            bool hasModifiedEntry = cachedSuffixPresetLabels.Count > 0 &&
                                    cachedSuffixPresetLabels[cachedSuffixPresetLabels.Count - 1] == PresetLabelCustomModified;

            // Si está dirty: aseguramos que Custom(modified) existe al final y lo seleccionamos (sin reventar la selección del usuario)
            if (suffixesDirtyFromPreset)
            {
                if (!hasModifiedEntry)
                {
                    RefreshSuffixPresets(includeModifiedEntry: true);
                    hasModifiedEntry = cachedSuffixPresetLabels.Count > 0 &&
                                       cachedSuffixPresetLabels[cachedSuffixPresetLabels.Count - 1] == PresetLabelCustomModified;
                }

                int desired = cachedSuffixPresetLabels.Count - 1; // último = Custom(modified)
                if (suffixPresetPopupIndex != desired)
                    suffixPresetPopupIndex = desired;
            }

            EditorGUI.BeginChangeCheck();
            suffixPresetPopupIndex = EditorGUILayout.Popup("Suffix Preset", suffixPresetPopupIndex, cachedSuffixPresetLabels.ToArray());
            bool changed = EditorGUI.EndChangeCheck();
            if (!changed) return;

            hasModifiedEntry = cachedSuffixPresetLabels.Count > 0 &&
                               cachedSuffixPresetLabels[cachedSuffixPresetLabels.Count - 1] == PresetLabelCustomModified;

            int modifiedIndex = hasModifiedEntry ? (cachedSuffixPresetLabels.Count - 1) : -1;

            // Si elige Custom(modified) (último), no cambiamos nada: mantiene los valores actuales
            if (hasModifiedEntry && suffixPresetPopupIndex == modifiedIndex)
            {
                // No tocamos lastSelectedPresetGuid: puede seguir indicando de qué preset veníamos.
                // Solo garantizamos que Unity recuerde el estado.
                EditorPrefs.SetString(PrefKeySelectedSuffixPresetGuid, lastSelectedPresetGuid ?? "");
                return;
            }

            // Si elige un preset real: índice directo, NO hay offsets
            int presetIndex = suffixPresetPopupIndex;
            if (presetIndex < 0 || presetIndex >= cachedSuffixPresets.Count) return;

            var preset = cachedSuffixPresets[presetIndex];
            if (preset == null) return;

            ApplySuffixPreset(preset);
        }

        private void RefreshSuffixPresets(bool includeModifiedEntry = false)
        {
            cachedSuffixPresets = new List<HeroAssetsSuffixPreset>();
            cachedSuffixPresetLabels = new List<string>();
            cachedSuffixPresetGuids = new List<string>();

            // Buscar presets
            string[] guids = AssetDatabase.FindAssets("t:HeroAssetsSuffixPreset");
            var all = new List<(HeroAssetsSuffixPreset preset, string guid)>();

            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var p = AssetDatabase.LoadAssetAtPath<HeroAssetsSuffixPreset>(path);
                if (p != null) all.Add((p, g));
            }

            var builtIn = all.Where(x => x.preset != null && x.preset.isBuiltIn).ToList();
            var user = all.Where(x => x.preset != null && !x.preset.isBuiltIn).ToList();

            builtIn.Sort((a, b) =>
            {
                int so = a.preset.sortOrder.CompareTo(b.preset.sortOrder);
                if (so != 0) return so;
                return string.Compare(a.preset.displayName, b.preset.displayName, StringComparison.OrdinalIgnoreCase);
            });

            user.Sort((a, b) =>
                string.Compare(a.preset.displayName, b.preset.displayName, StringComparison.OrdinalIgnoreCase));

            void AddPreset((HeroAssetsSuffixPreset preset, string guid) item)
            {
                cachedSuffixPresets.Add(item.preset);
                cachedSuffixPresetGuids.Add(item.guid);

                string label = string.IsNullOrWhiteSpace(item.preset.displayName) ? item.preset.name : item.preset.displayName;
                cachedSuffixPresetLabels.Add(label);
            }

            foreach (var p in builtIn) AddPreset(p);
            foreach (var p in user) AddPreset(p);

            // Custom(modified) SIEMPRE al final (si se pide)
            if (includeModifiedEntry)
                cachedSuffixPresetLabels.Add(PresetLabelCustomModified);

            RecomputeSuffixPresetPopupIndex();
        }

        private void RecomputeSuffixPresetPopupIndex()
        {
            if (cachedSuffixPresetLabels == null || cachedSuffixPresetLabels.Count == 0)
            {
                suffixPresetPopupIndex = 0;
                return;
            }

            bool hasModifiedEntry = cachedSuffixPresetLabels[cachedSuffixPresetLabels.Count - 1] == PresetLabelCustomModified;
            int modifiedIndex = hasModifiedEntry ? (cachedSuffixPresetLabels.Count - 1) : -1;

            if (suffixesDirtyFromPreset && hasModifiedEntry)
            {
                suffixPresetPopupIndex = modifiedIndex;
                return;
            }

            if (string.IsNullOrEmpty(lastSelectedPresetGuid))
            {
                // Si no hay preset seleccionado, selecciona el primero (si existe)
                suffixPresetPopupIndex = 0;
                return;
            }

            int idx = cachedSuffixPresetGuids.FindIndex(g => string.Equals(g, lastSelectedPresetGuid, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                // No encontrado -> fallback
                lastSelectedPresetGuid = "";
                suffixesDirtyFromPreset = false;
                suffixPresetPopupIndex = 0;
                return;
            }

            // Índice directo, sin offsets
            suffixPresetPopupIndex = Mathf.Clamp(idx, 0, cachedSuffixPresets.Count - 1);
        }

        private void RestoreSelectedSuffixPreset()
        {
            if (string.IsNullOrEmpty(lastSelectedPresetGuid))
            {
                suffixesDirtyFromPreset = false;
                RefreshSuffixPresets(includeModifiedEntry: false);
                return;
            }

            string path = AssetDatabase.GUIDToAssetPath(lastSelectedPresetGuid);
            var preset = AssetDatabase.LoadAssetAtPath<HeroAssetsSuffixPreset>(path);

            if (preset == null)
            {
                lastSelectedPresetGuid = "";
                suffixesDirtyFromPreset = false;
                RefreshSuffixPresets(includeModifiedEntry: false);
                return;
            }

            ApplySuffixPreset(preset);
        }

        private void ApplySuffixPreset(HeroAssetsSuffixPreset preset)
        {
            if (preset == null) return;

            var s = preset.suffixes;
            s.NormalizeNulls();

            state.baseColorSuffix = s.baseColor;
            state.normalSuffix = s.normal;
            state.unityMaskMapSuffix = s.unityMaskMap;
            state.heightSuffix = s.height;

            state.armSuffix = s.arm;
            state.ormSuffix = s.orm;
            state.metallicSuffix = s.metallic;
            state.aoSuffix = s.ao;
            state.smoothnessSuffix = s.smoothness;
            state.roughnessSuffix = s.roughness;

            lastAppliedPresetSuffixes = s;
            suffixesDirtyFromPreset = false;

            lastSelectedPresetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(preset));
            EditorPrefs.SetString(PrefKeySelectedSuffixPresetGuid, lastSelectedPresetGuid ?? "");

            // Al aplicar un preset, ya no necesitamos Custom(modified) en la lista
            RefreshSuffixPresets(includeModifiedEntry: false);

            SaveSuffixPrefs();
            AutoFillFromFolders();
            Repaint();
        }

        #endregion

        private SuffixesData GetCurrentSuffixesData()
        {
            var s = new SuffixesData
            {
                baseColor = state.baseColorSuffix,
                normal = state.normalSuffix,
                unityMaskMap = state.unityMaskMapSuffix,
                height = state.heightSuffix,

                arm = state.armSuffix,
                orm = state.ormSuffix,
                metallic = state.metallicSuffix,
                ao = state.aoSuffix,
                smoothness = state.smoothnessSuffix,
                roughness = state.roughnessSuffix
            };
            s.NormalizeNulls();
            return s;
        }

        private void SaveCurrentSuffixesAsPreset()
        {
            try
            {
                string defaultFolder = EditorPrefs.GetString(PrefKeyPresetSaveFolder, "Assets/Hero Assets/Presets/Suffixes");
                if (string.IsNullOrEmpty(defaultFolder)) defaultFolder = "Assets";

                // Project root = padre de /Assets
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
                if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
                {
                    EditorUtility.DisplayDialog("Error", "No se pudo determinar la ruta del proyecto.", "OK");
                    return;
                }

                string absInitial = Path.Combine(projectRoot, defaultFolder).Replace("\\", "/");
                if (!Directory.Exists(absInitial)) absInitial = projectRoot;

                string absPath = EditorUtility.SaveFilePanel(
                    "Save Suffix Preset",
                    absInitial,
                    "SuffixPreset_New",
                    "asset"
                );

                if (string.IsNullOrEmpty(absPath)) return;
                absPath = absPath.Replace("\\", "/");

                if (!absPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Invalid location", "Guarda el preset dentro de este proyecto de Unity.", "OK");
                    return;
                }

                // Convertir ABS -> REL (Assets/...)
                string dataPath = Application.dataPath.Replace("\\", "/"); // .../Project/Assets
                string relPath;

                if (absPath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                    relPath = "Assets" + absPath.Substring(dataPath.Length);
                else
                    relPath = "Assets" + absPath.Substring(projectRoot.Length);

                relPath = relPath.Replace("\\", "/");
                if (!relPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    relPath += ".asset";

                // Crear carpeta si no existe
                string relFolder = Path.GetDirectoryName(relPath)?.Replace("\\", "/");
                if (string.IsNullOrEmpty(relFolder)) relFolder = "Assets";
                EnsureUnityFolderExists(relFolder);

                EditorPrefs.SetString(PrefKeyPresetSaveFolder, relFolder);

                var preset = CreateInstance<HeroAssetsSuffixPreset>();
                preset.isBuiltIn = false;
                preset.sortOrder = 0;
                preset.displayName = Path.GetFileNameWithoutExtension(relPath);
                preset.description = "";
                preset.suffixes = GetCurrentSuffixesData();

                var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relPath);
                if (existing != null)
                {
                    int r = EditorUtility.DisplayDialogComplex(
                        "Overwrite preset?",
                        $"Ya existe un preset en:\n\n{relPath}\n\n¿Quieres sobrescribirlo?",
                        "Overwrite",
                        "Cancel",
                        null
                    );

                    if (r != 0)
                    {
                        DestroyImmediate(preset);
                        return;
                    }

                    AssetDatabase.DeleteAsset(relPath);
                }

                AssetDatabase.CreateAsset(preset, relPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                lastSelectedPresetGuid = AssetDatabase.AssetPathToGUID(relPath);
                EditorPrefs.SetString(PrefKeySelectedSuffixPresetGuid, lastSelectedPresetGuid ?? "");

                suffixesDirtyFromPreset = false;
                lastAppliedPresetSuffixes = preset.suffixes;

                RefreshSuffixPresets(includeModifiedEntry: false);
                RecomputeSuffixPresetPopupIndex();
                Repaint();
            }
            finally
            {
                // evita Invalid GUILayout state tras diálogos / crear assets
                GUIUtility.ExitGUI();
            }
        }

        // Crea carpetas tipo "Assets/..." recursivamente si faltan
        private static void EnsureUnityFolderExists(string assetsFolder)
        {
            if (string.IsNullOrEmpty(assetsFolder)) return;
            assetsFolder = assetsFolder.Replace("\\", "/");

            if (assetsFolder == "Assets") return;
            if (AssetDatabase.IsValidFolder(assetsFolder)) return;

            string parent = Path.GetDirectoryName(assetsFolder)?.Replace("\\", "/");
            string leaf = Path.GetFileName(assetsFolder);

            if (string.IsNullOrEmpty(parent)) parent = "Assets";
            EnsureUnityFolderExists(parent);

            if (!AssetDatabase.IsValidFolder(assetsFolder))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void MoveListItem<T>(List<T> list, int oldIndex, int newIndex)
        {
            if (list == null) return;
            if (oldIndex < 0 || oldIndex >= list.Count) return;
            if (newIndex < 0 || newIndex >= list.Count) return;

            var item = list[oldIndex];
            list.RemoveAt(oldIndex);
            if (newIndex > oldIndex) newIndex--;
            list.Insert(newIndex, item);
        }

        private void EnsureLayerListsSize(int layers)
        {
            while (state.maskSources.Count < layers) state.maskSources.Add(new MaskSources());
            while (state.maskSources.Count > layers) state.maskSources.RemoveAt(state.maskSources.Count - 1);

            if (state.genAlbedo) ResizeList(state.albedo, layers);
            if (state.genNormal) ResizeList(state.normal, layers);
            if (state.genMaskArray) ResizeList(state.mask, layers);

            // asegurar folder ref en maskSources
            for (int i = 0; i < layers; i++)
                state.maskSources[i].folder = state.variationFolders[i];
        }

        private void OverwriteLayerFromFolder(int layer)
        {
            if (layer < 0 || layer >= state.variationFolders.Count) return;

            var folderAsset = state.variationFolders[layer];
            if (folderAsset == null) return;

            string folderPath = AssetDatabase.GetAssetPath(folderAsset);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath)) return;

            EnsureLayerListsSize(state.variationFolders.Count);

            FindMapsInFolder(folderPath,
                out var baseColor,
                out var normal,
                out var unityMaskMap,
                out var height,
                out var arm,
                out var orm,
                out var metallic,
                out var ao,
                out var smooth,
                out var rough
            );

            // Arrays: overwrite
            if (state.genAlbedo) state.albedo[layer] = baseColor;
            if (state.genNormal) state.normal[layer] = normal;
            if (state.genMaskArray) state.mask[layer] = unityMaskMap;

            // MaskSources: overwrite total
            var ms = state.maskSources[layer] ?? (state.maskSources[layer] = new MaskSources());
            ms.folder = folderAsset;
            ms.unityMaskMap = unityMaskMap;
            ms.arm = arm;
            ms.orm = orm;
            ms.metallic = metallic;
            ms.ao = ao;
            ms.smoothness = smooth;
            ms.roughness = rough;
            ms.suggestedBaseName = SuggestBaseName(ms);

            // Height (solo si está en 0..3) — lo gestiona RefreshHeightSlots_Mixed()
        }

        private void RefreshAllFolderLayersFromNaming()
        {
            EnsureLayerListsSize(state.variationFolders.Count);

            for (int i = 0; i < state.variationFolders.Count; i++)
            {
                if (state.variationFolders[i] != null)
                    OverwriteLayerFromFolder(i);
            }
        }
        private void RefreshHeightSlots_Mixed()
        {
            // Slot 0 -> R
            if (state.variationFolders.Count > 0 && state.variationFolders[0] != null)
                state.heightR = FindHeightInFolderAtIndex(0);
            else
                state.heightR = null;

            // Slot 1 -> G
            if (state.variationFolders.Count > 1 && state.variationFolders[1] != null)
                state.heightG = FindHeightInFolderAtIndex(1);
            else
                state.heightG = null;

            // Slot 2 -> B
            if (state.variationFolders.Count > 2 && state.variationFolders[2] != null)
                state.heightB = FindHeightInFolderAtIndex(2);
            else
                state.heightB = null;

            // Slot 3 -> A
            if (state.variationFolders.Count > 3 && state.variationFolders[3] != null)
                state.heightA = FindHeightInFolderAtIndex(3);
            else
                state.heightA = null;
        }
        private void ResetAllManualDataAndRebuildFromFolders()
        {
            // 1) Limpiar TODO lo que el usuario pudo meter a mano
            ClearArrayLists();
            ClearPerFolderMaskSources();
            ClearHeightSlots();

            // 2) Reconstruir desde folders (pisa siempre)
            AutoFillFromFolders();

            Repaint();
        }
    }
}
#endif