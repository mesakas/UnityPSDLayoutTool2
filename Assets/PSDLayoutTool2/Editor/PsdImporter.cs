namespace PsdLayoutTool2
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using PhotoshopFile;
    using UnityEditor;
    using UnityEditorInternal;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    /// <summary>
    /// Handles all of the importing for a PSD file (exporting textures, creating prefabs, etc).
    /// </summary>
    public static class PsdImporter
    {
        /// <summary>
        /// Controls where generated assets are saved.
        /// </summary>
        public enum OutputDirectoryMode
        {
            /// <summary>
            /// Save generated files into a subfolder next to the PSD.
            /// </summary>
            PsdDirectory,

            /// <summary>
            /// Save generated files into a subfolder under the Assets root.
            /// </summary>
            AssetsRoot
        }

        /// <summary>
        /// Controls where the generated prefab is saved.
        /// </summary>
        public enum PrefabOutputMode
        {
            /// <summary>
            /// Save the prefab next to the generated output folder (default).
            /// </summary>
            SiblingToOutputFolder,

            /// <summary>
            /// Save the prefab inside the generated output folder.
            /// </summary>
            InsideOutputFolder
        }

        /// <summary>
        /// The current file path to use to save layers as .png files
        /// </summary>
        private static string currentPath;

        /// <summary>
        /// The <see cref="GameObject"/> representing the root PSD layer.  It contains all of the other layers as children GameObjects.
        /// </summary>
        private static GameObject rootPsdGameObject;

        /// <summary>
        /// The top-level object that should be saved as prefab or destroyed after import.
        /// </summary>
        private static GameObject importRootGameObject;

        /// <summary>
        /// The <see cref="GameObject"/> representing the current group (folder) we are processing.
        /// </summary>
        private static GameObject currentGroupGameObject;

        /// <summary>
        /// The current UI layout context used to place child RectTransforms.
        /// </summary>
        private static UiLayoutContext currentGroupLayoutContext;

        /// <summary>
        /// The current depth (Z axis position) that sprites will be placed on.  It is initialized to the MaximumDepth ("back" depth) and it is automatically
        /// decremented as the PSD file is processed, back to front.
        /// </summary>
        private static float currentDepth;

        /// <summary>
        /// The amount that the depth decrements for each layer.  This is automatically calculated from the number of layers in the PSD file and the MaximumDepth.
        /// </summary>
        private static float depthStep;

        /// <summary>
        /// Deterministic render order used for SpriteRenderer/TextMesh so layer order does not depend on camera angle.
        /// </summary>
        private static int currentSortingOrder;

        /// <summary>
        /// Stores explicit update selections for the active import run.
        /// If null or disabled, existing files are overwritten as before.
        /// </summary>
        private static HashSet<string> selectedUpdatePathsForCurrentImport;

        /// <summary>
        /// Indicates whether current import should respect explicit overwrite selections.
        /// </summary>
        private static bool useExplicitUpdateSelection;

        /// <summary>
        /// Prevents opening multiple conflict-selection dialogs at the same time.
        /// </summary>
        private static bool isConflictSelectionDialogOpen;

        /// <summary>
        /// Stores resolved import metadata for layers in the current import run.
        /// </summary>
        private static Dictionary<Layer, LayerImportInfo> currentLayerInfos;

        /// <summary>
        /// Cached set of invalid filesystem characters for generated file and folder names.
        /// </summary>
        private static readonly HashSet<char> InvalidGeneratedNameChars = new HashSet<char>(
            Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));

        /// <summary>
        /// Reserved DOS device names that cannot be used as generated file or folder names on Windows.
        /// </summary>
        private static readonly HashSet<string> ReservedGeneratedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        };

        /// <summary>
        /// Represents how a button-group child should be interpreted.
        /// </summary>
        private enum ButtonChildRole
        {
            None,
            Default,
            Pressed,
            Highlighted,
            Disabled,
            TextImage
        }

        /// <summary>
        /// Supported anchor presets parsed from layer or folder names.
        /// </summary>
        private enum AnchorNamePreset
        {
            None,
            Global,
            TopLeft,
            BottomLeft,
            TopRight,
            BottomRight,
            Center,
            LeftMiddle,
            RightMiddle,
            TopMiddle,
            BottomMiddle
        }

        /// <summary>
        /// Describes how one parent RectTransform maps PSD space into its local space.
        /// </summary>
        private struct UiLayoutContext
        {
            /// <summary>
            /// Gets or sets the PSD-space rectangle represented by this layout context.
            /// </summary>
            public Rect PsdReferenceRect { get; set; }

            /// <summary>
            /// Gets or sets the full local rect size of the current parent RectTransform.
            /// </summary>
            public Vector2 LocalRectSize { get; set; }

            /// <summary>
            /// Gets or sets the PSD content display rect within the parent local space.
            /// </summary>
            public Rect LocalDisplayRect { get; set; }
        }

        /// <summary>
        /// Stores resolved import metadata for one PSD layer.
        /// </summary>
        private sealed class LayerImportInfo
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="LayerImportInfo"/> class.
            /// </summary>
            /// <param name="layer">PSD layer.</param>
            public LayerImportInfo(Layer layer)
            {
                Layer = layer;
            }

            /// <summary>
            /// Gets the source PSD layer.
            /// </summary>
            public Layer Layer { get; private set; }

            /// <summary>
            /// Gets or sets the resolved parent info.
            /// </summary>
            public LayerImportInfo Parent { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this layer is visible after inheriting parent visibility.
            /// </summary>
            public bool EffectiveVisible { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this layer behaves like a folder/group.
            /// </summary>
            public bool IsFolderLike { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this layer is a |Button group.
            /// </summary>
            public bool IsButtonGroup { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this layer is a |Animation group.
            /// </summary>
            public bool IsAnimationGroup { get; set; }

            /// <summary>
            /// Gets or sets the parsed button-child role when parent is a button group.
            /// </summary>
            public ButtonChildRole ButtonRole { get; set; }

            /// <summary>
            /// Gets or sets the unique stable name for this layer among siblings.
            /// </summary>
            public string UniqueSelfName { get; set; }

            /// <summary>
            /// Gets or sets the unique stable texture/file base name in the current output directory.
            /// </summary>
            public string UniqueTextureName { get; set; }

            /// <summary>
            /// Gets or sets the parsed animation frame rate.
            /// </summary>
            public float AnimationFps { get; set; }

            /// <summary>
            /// Gets or sets the parsed anchor preset from the source layer name.
            /// </summary>
            public AnchorNamePreset AnchorPreset { get; set; }

            /// <summary>
            /// Gets or sets the explicitly parsed anchor preset from the source layer name before inheritance.
            /// </summary>
            public AnchorNamePreset ExplicitAnchorPreset { get; set; }

            /// <summary>
            /// Gets or sets the resolved layout rect used for UI placement.
            /// </summary>
            public Rect LayoutRect { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether <see cref="LayoutRect"/> contains a usable rect.
            /// </summary>
            public bool HasLayoutRect { get; set; }
        }

        /// <summary>
        /// Initializes static members of the <see cref="PsdImporter"/> class.
        /// </summary>
        static PsdImporter()
        {
            MaximumDepth = 10;
            PixelsToUnits = 100;
            OutputMode = OutputDirectoryMode.PsdDirectory;
            OutputFolderName = string.Empty;
            PrefabMode = PrefabOutputMode.SiblingToOutputFolder;
            ScaleToTargetCanvas = true;
            PreserveAspectWhenScalingToCanvas = true;
            EnableAutoAnchorByName = true;
            RootUseGlobalAnchorByDefault = true;
        }

        /// <summary>
        /// Gets or sets the maximum depth.  This is where along the Z axis the back will be, with the front being at 0.
        /// </summary>
        public static float MaximumDepth { get; set; }

        /// <summary>
        /// Gets or sets the number of pixels per Unity unit value.  Defaults to 100 (which matches Unity's Sprite default).
        /// </summary>
        public static float PixelsToUnits { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use the Unity 4.6+ UI system or not.
        /// </summary>
        public static bool UseUnityUI { get; set; }

        /// <summary>
        /// Gets or sets the hierarchy path of the target canvas to align generated UI under.
        /// Empty means creating a dedicated world-space canvas as before.
        /// </summary>
        public static string TargetCanvasPath { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the generated UI should be scaled to the selected target canvas size.
        /// </summary>
        public static bool ScaleToTargetCanvas { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether scaling to target canvas should preserve PSD aspect ratio.
        /// </summary>
        public static bool PreserveAspectWhenScalingToCanvas { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether UI anchors should be inferred from layer names.
        /// </summary>
        public static bool EnableAutoAnchorByName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the generated UI root should default to a global stretch anchor.
        /// </summary>
        public static bool RootUseGlobalAnchorByDefault { get; set; }

        /// <summary>
        /// Gets or sets the generated files output mode.
        /// </summary>
        public static OutputDirectoryMode OutputMode { get; set; }

        /// <summary>
        /// Gets or sets the output folder name. Empty means using the PSD file name.
        /// </summary>
        public static string OutputFolderName { get; set; }

        /// <summary>
        /// Gets or sets where the prefab is generated.
        /// </summary>
        public static PrefabOutputMode PrefabMode { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the import process should create <see cref="GameObject"/>s in the scene.
        /// </summary>
        private static bool LayoutInScene { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the import process should create a prefab in the project's assets.
        /// </summary>
        private static bool CreatePrefab { get; set; }

        /// <summary>
        /// Gets or sets the size (in pixels) of the entire PSD canvas.
        /// </summary>
        private static Vector2 CanvasSize { get; set; }

        /// <summary>
        /// Gets or sets the name of the current 
        /// </summary>
        private static string PsdName { get; set; }

        /// <summary>
        /// Gets or sets the Unity 4.6+ UI canvas.
        /// </summary>
        private static GameObject Canvas { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether UI elements should use anchored-position placement for target canvas alignment.
        /// </summary>
        private static bool UseTargetCanvasCoordinates { get; set; }

        /// <summary>
        /// Gets or sets the reference canvas size used for target-canvas coordinate mapping.
        /// </summary>
        private static Vector2 TargetCanvasSize { get; set; }

        /// <summary>
        /// Gets or sets the current <see cref="PsdFile"/> that is being imported.
        /// </summary>
        ////private static PsdFile CurrentPsdFile { get; set; }

        /// <summary>
        /// Exports each of the art layers in the PSD file as separate textures (.png files) in the project's assets.
        /// </summary>
        /// <param name="assetPath">The path of to the .psd file relative to the project.</param>
        public static void ExportLayersAsTextures(string assetPath)
        {
            LayoutInScene = false;
            CreatePrefab = false;
            Import(assetPath);
        }

        /// <summary>
        /// Lays out sprites in the current scene to match the PSD's layout.  Each layer is exported as Sprite-type textures in the project's assets.
        /// </summary>
        /// <param name="assetPath">The path of to the .psd file relative to the project.</param>
        public static void LayoutInCurrentScene(string assetPath)
        {
            LayoutInScene = true;
            CreatePrefab = false;
            Import(assetPath);
        }

        /// <summary>
        /// Generates a prefab consisting of sprites laid out to match the PSD's layout. Each layer is exported as Sprite-type textures in the project's assets.
        /// </summary>
        /// <param name="assetPath">The path of to the .psd file relative to the project.</param>
        public static void GeneratePrefab(string assetPath)
        {
            LayoutInScene = false;
            CreatePrefab = true;
            Import(assetPath);
        }

        /// <summary>
        /// Imports a Photoshop document (.psd) file at the given path.
        /// </summary>
        /// <param name="asset">The path of to the .psd file relative to the project.</param>
        private static void Import(string asset)
        {
            Import(asset, null, false);
        }

        /// <summary>
        /// Imports a Photoshop document (.psd) file at the given path with optional preselected conflict handling.
        /// </summary>
        /// <param name="asset">The path of to the .psd file relative to the project.</param>
        /// <param name="forcedSelection">Preselected conflict actions. Null means no explicit selection.</param>
        /// <param name="skipConflictPrompt">True to bypass conflict prompts and apply <paramref name="forcedSelection"/> directly.</param>
        private static void Import(string asset, ImportConflictSelection forcedSelection, bool skipConflictPrompt)
        {
            currentDepth = MaximumDepth;
            currentSortingOrder = 0;
            UseTargetCanvasCoordinates = false;
            currentLayerInfos = null;
            string normalizedAssetPath = asset.Replace('\\', '/');
            string fullPath = Path.Combine(GetFullProjectPath(), normalizedAssetPath);

            PsdFile psd = new PsdFile(fullPath);
            CanvasSize = new Vector2(psd.Width, psd.Height);
            TargetCanvasSize = CanvasSize;

            // Set the depth step based on the layer count.  If there are no layers, default to 0.1f.
            depthStep = psd.Layers.Count != 0 ? MaximumDepth / psd.Layers.Count : 0.1f;

            PsdName = Path.GetFileNameWithoutExtension(normalizedAssetPath);

            string outputRelativePath = GetOutputRootRelativePath(normalizedAssetPath);
            string outputFullPath = Path.Combine(GetFullProjectPath(), outputRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string prefabRelativePath = CreatePrefab ? GetPrefabRelativePath(outputRelativePath) : string.Empty;

            List<Layer> tree = BuildLayerTree(psd.Layers) ?? new List<Layer>();
            currentLayerInfos = BuildLayerImportInfoMap(tree);
            bool hasVisibleRuntimeObjects = HasVisibleRuntimeContent(tree);
            ImportConflictAnalysis conflictAnalysis = AnalyzeImportConflicts(
                tree,
                outputRelativePath,
                outputFullPath,
                prefabRelativePath,
                hasVisibleRuntimeObjects);

            ImportConflictSelection effectiveSelection = forcedSelection;
            if (!skipConflictPrompt && conflictAnalysis.HasExistingTargets)
            {
                bool updateExistingFiles = PromptForUpdatingExistingFiles(conflictAnalysis);
                if (!updateExistingFiles)
                {
                    currentLayerInfos = null;
                    return;
                }

                if (conflictAnalysis.HasSelectableEntries)
                {
                    if (isConflictSelectionDialogOpen)
                    {
                        EditorUtility.DisplayDialog(
                            "PSDLayoutTool2",
                            "已有一个更新/删除确认窗口正在打开，请先完成该操作。",
                            "确定");
                        currentLayerInfos = null;
                        return;
                    }

                    isConflictSelectionDialogOpen = true;
                    ImportConflictSelection defaultSelection = CreateDefaultConflictSelection(conflictAnalysis);
                    ImportConflictSelectionWindow.ShowDialog(
                        conflictAnalysis,
                        defaultSelection,
                        selection =>
                        {
                            isConflictSelectionDialogOpen = false;
                            if (selection == null || !selection.Confirmed)
                            {
                                return;
                            }

                            Import(asset, selection, true);
                        });
                    currentLayerInfos = null;
                    return;
                }

                effectiveSelection = CreateDefaultConflictSelection(conflictAnalysis);
            }

            ConfigureCurrentImportSelection(effectiveSelection);

            try
            {
                currentPath = outputFullPath;
                Directory.CreateDirectory(currentPath);

                if (effectiveSelection != null)
                {
                    DeleteSelectedFiles(effectiveSelection.PathsToDelete, outputFullPath, conflictAnalysis.PrefabFullPath);
                }

                rootPsdGameObject = null;
                importRootGameObject = null;
                currentGroupGameObject = null;
                currentGroupLayoutContext = default(UiLayoutContext);

                if ((LayoutInScene || CreatePrefab) && hasVisibleRuntimeObjects)
                {
                    if (UseUnityUI)
                    {
                        CreateUIEventSystem();
                        Canvas targetCanvas = ResolveTargetCanvas();
                        if (targetCanvas != null)
                        {
                            UseTargetCanvasCoordinates = true;
                            TargetCanvasSize = GetTargetCanvasRectSize(targetCanvas);
                            rootPsdGameObject = new GameObject(PsdName, typeof(RectTransform));
                            RectTransform rootRect = rootPsdGameObject.GetComponent<RectTransform>();
                            rootRect.SetParent(targetCanvas.transform, false);
                            currentGroupLayoutContext = ApplyRootUILayout(rootRect);
                            importRootGameObject = rootPsdGameObject;
                        }
                        else
                        {
                            CreateUICanvas();
                            rootPsdGameObject = new GameObject(PsdName, typeof(RectTransform));
                            RectTransform rootRect = rootPsdGameObject.GetComponent<RectTransform>();
                            rootRect.SetParent(Canvas.transform, false);
                            currentGroupLayoutContext = ApplyRootUILayout(rootRect);
                            importRootGameObject = Canvas;
                        }
                    }
                    else
                    {
                        rootPsdGameObject = new GameObject(PsdName);
                        importRootGameObject = rootPsdGameObject;
                    }

                    currentGroupGameObject = rootPsdGameObject;
                }

                ExportTree(tree);

                if (CreatePrefab && importRootGameObject != null)
                {
                    if (ShouldSavePrefab(prefabRelativePath))
                    {
                        PrefabUtility.SaveAsPrefabAsset(importRootGameObject, prefabRelativePath);
                    }

                    if (!LayoutInScene && importRootGameObject != null)
                    {
                        // if we are not flagged to layout in the scene, delete the GameObject used to generate the prefab
                        UnityEngine.Object.DestroyImmediate(importRootGameObject);
                    }
                }

                AssetDatabase.Refresh();
            }
            finally
            {
                ClearCurrentImportSelection();
                currentLayerInfos = null;
            }
        }

        /// <summary>
        /// Compares generated texture outputs against existing files to compute update/delete candidates.
        /// </summary>
        /// <param name="tree">Layer tree for current PSD import.</param>
        /// <param name="outputRelativePath">Output directory relative to project.</param>
        /// <param name="outputFullPath">Output directory absolute path.</param>
        /// <param name="prefabRelativePath">Prefab path relative to project.</param>
        /// <returns>Conflict analysis data for this import run.</returns>
        private static ImportConflictAnalysis AnalyzeImportConflicts(
            List<Layer> tree,
            string outputRelativePath,
            string outputFullPath,
            string prefabRelativePath,
            bool hasVisibleRuntimeObjects)
        {
            ImportConflictAnalysis analysis = new ImportConflictAnalysis();
            analysis.OutputRelativePath = outputRelativePath;
            analysis.OutputFullPath = NormalizePath(outputFullPath);
            analysis.PrefabRelativePath = prefabRelativePath;
            if (!string.IsNullOrEmpty(prefabRelativePath))
            {
                analysis.PrefabFullPath = NormalizePath(
                    Path.Combine(GetFullProjectPath(), prefabRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            analysis.HasExistingOutputDirectory = Directory.Exists(outputFullPath);
            analysis.HasExistingPrefab = !string.IsNullOrEmpty(analysis.PrefabFullPath) && File.Exists(analysis.PrefabFullPath);

            HashSet<string> generatedAssetPaths = CollectExpectedGeneratedAssetPaths(
                tree,
                outputFullPath,
                analysis.PrefabFullPath,
                hasVisibleRuntimeObjects);
            HashSet<string> existingGeneratedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (analysis.HasExistingOutputDirectory)
            {
                string[] existingFiles = Directory.GetFiles(outputFullPath, "*.*", SearchOption.AllDirectories);
                foreach (string existingFile in existingFiles)
                {
                    string extension = Path.GetExtension(existingFile);
                    if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".anim", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".controller", StringComparison.OrdinalIgnoreCase))
                    {
                        existingGeneratedPaths.Add(NormalizePath(existingFile));
                    }
                }
            }

            if (analysis.HasExistingPrefab)
            {
                existingGeneratedPaths.Add(NormalizePath(analysis.PrefabFullPath));
            }

            foreach (string existingFile in existingGeneratedPaths)
            {
                if (generatedAssetPaths.Contains(existingFile))
                {
                    analysis.SameNamePaths.Add(existingFile);
                }
                else
                {
                    analysis.DeletedPaths.Add(existingFile);
                }
            }

            List<string> sortedSameNamePaths = analysis.SameNamePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => ToDisplayPath(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> sortedDeletedPaths = analysis.DeletedPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => ToDisplayPath(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            analysis.SameNamePaths.Clear();
            analysis.SameNamePaths.AddRange(sortedSameNamePaths);
            analysis.DeletedPaths.Clear();
            analysis.DeletedPaths.AddRange(sortedDeletedPaths);

            return analysis;
        }

        /// <summary>
        /// Creates the default conflict selection, which updates same-name files and deletes stale files.
        /// </summary>
        /// <param name="analysis">Current conflict analysis.</param>
        /// <returns>Default conflict selection.</returns>
        private static ImportConflictSelection CreateDefaultConflictSelection(ImportConflictAnalysis analysis)
        {
            ImportConflictSelection selection = new ImportConflictSelection
            {
                Confirmed = true
            };

            foreach (string path in analysis.SameNamePaths)
            {
                if (!string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    selection.PathsToUpdate.Add(NormalizePath(path));
                }
            }

            foreach (string path in analysis.DeletedPaths)
            {
                selection.PathsToDelete.Add(NormalizePath(path));
            }

            return selection;
        }

        /// <summary>
        /// Prompts the user to confirm whether existing targets should be updated.
        /// </summary>
        /// <param name="analysis">Current conflict analysis.</param>
        /// <returns>True if user wants to continue updating; otherwise false.</returns>
        private static bool PromptForUpdatingExistingFiles(ImportConflictAnalysis analysis)
        {
            StringBuilder messageBuilder = new StringBuilder();
            messageBuilder.AppendLine("检测到已有同名导入目标：");
            if (analysis.HasExistingOutputDirectory)
            {
                messageBuilder.AppendLine("输出目录: " + analysis.OutputRelativePath);
            }

            if (analysis.HasExistingPrefab)
            {
                messageBuilder.AppendLine("预制体: " + analysis.PrefabRelativePath);
            }

            messageBuilder.AppendLine();
            messageBuilder.AppendLine("是否要更新现有文件？");

            return EditorUtility.DisplayDialog(
                "PSDLayoutTool2",
                messageBuilder.ToString(),
                "更新",
                "取消");
        }

        /// <summary>
        /// Configures overwrite-selection state for the active import run.
        /// </summary>
        /// <param name="selection">Selected update/delete actions for this run.</param>
        private static void ConfigureCurrentImportSelection(ImportConflictSelection selection)
        {
            useExplicitUpdateSelection = selection != null;
            selectedUpdatePathsForCurrentImport = selection != null
                ? new HashSet<string>(selection.PathsToUpdate, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        /// <summary>
        /// Clears overwrite-selection state after an import run ends.
        /// </summary>
        private static void ClearCurrentImportSelection()
        {
            useExplicitUpdateSelection = false;
            selectedUpdatePathsForCurrentImport = null;
        }

        /// <summary>
        /// Deletes selected stale generated files and their meta files.
        /// </summary>
        /// <param name="pathsToDelete">Files selected for deletion.</param>
        /// <param name="outputFullPath">Import output root path.</param>
        /// <param name="prefabFullPath">Resolved prefab full path, if any.</param>
        private static void DeleteSelectedFiles(HashSet<string> pathsToDelete, string outputFullPath, string prefabFullPath)
        {
            if (pathsToDelete == null || pathsToDelete.Count == 0)
            {
                return;
            }

            string normalizedRoot = NormalizePath(outputFullPath).TrimEnd('/');
            string normalizedPrefabPath = string.IsNullOrEmpty(prefabFullPath) ? string.Empty : NormalizePath(prefabFullPath);

            foreach (string selectedPath in pathsToDelete)
            {
                string normalizedPath = NormalizePath(selectedPath);
                if (!IsPathInsideDirectory(normalizedPath, normalizedRoot) &&
                    !string.Equals(normalizedPath, normalizedPrefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DeleteFileWithMeta(normalizedPath);
            }

            if (Directory.Exists(outputFullPath))
            {
                DeleteEmptySubDirectories(outputFullPath);
            }
        }

        /// <summary>
        /// Determines whether the current import run can overwrite an existing generated file.
        /// </summary>
        /// <param name="filePath">Absolute path to the generated file.</param>
        /// <returns>True if writing is allowed; otherwise false.</returns>
        private static bool ShouldOverwriteExistingGeneratedFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return true;
            }

            if (!useExplicitUpdateSelection)
            {
                return true;
            }

            return selectedUpdatePathsForCurrentImport != null &&
                   selectedUpdatePathsForCurrentImport.Contains(NormalizePath(filePath));
        }

        /// <summary>
        /// Prepares an asset path for CreateAsset by honoring overwrite selection and deleting the old asset when allowed.
        /// </summary>
        /// <param name="assetRelativePath">Asset path relative to project root.</param>
        /// <returns>True if a new asset should be created at this path; otherwise false.</returns>
        private static bool PrepareAssetPathForCreate(string assetRelativePath)
        {
            string assetFullPath = NormalizePath(
                Path.Combine(GetFullProjectPath(), assetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!ShouldOverwriteExistingGeneratedFile(assetFullPath))
            {
                return false;
            }

            if (File.Exists(assetFullPath))
            {
                if (!AssetDatabase.DeleteAsset(assetRelativePath))
                {
                    DeleteFileWithMeta(assetFullPath);
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether the prefab should be saved for this import run.
        /// </summary>
        /// <param name="prefabRelativePath">Prefab path relative to project.</param>
        /// <returns>True if prefab should be created/updated; otherwise false.</returns>
        private static bool ShouldSavePrefab(string prefabRelativePath)
        {
            if (string.IsNullOrEmpty(prefabRelativePath))
            {
                return false;
            }

            string prefabFullPath = NormalizePath(
                Path.Combine(GetFullProjectPath(), prefabRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            return ShouldOverwriteExistingGeneratedFile(prefabFullPath);
        }

        /// <summary>
        /// Collects all generated asset paths for the current import.
        /// </summary>
        /// <param name="tree">Layer tree for the PSD.</param>
        /// <param name="outputFullPath">Output root directory path.</param>
        /// <param name="prefabFullPath">Resolved prefab full path.</param>
        /// <param name="hasVisibleRuntimeObjects">Whether runtime content will be generated.</param>
        /// <returns>Set of absolute generated asset paths.</returns>
        private static HashSet<string> CollectExpectedGeneratedAssetPaths(
            List<Layer> tree,
            string outputFullPath,
            string prefabFullPath,
            bool hasVisibleRuntimeObjects)
        {
            HashSet<string> expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tree != null)
            {
                for (int i = tree.Count - 1; i >= 0; i--)
                {
                    CollectExpectedGeneratedAssetPathsForLayer(tree[i], outputFullPath, expectedPaths);
                }
            }

            if (CreatePrefab && hasVisibleRuntimeObjects && !string.IsNullOrEmpty(prefabFullPath))
            {
                expectedPaths.Add(NormalizePath(prefabFullPath));
            }

            return expectedPaths;
        }

        /// <summary>
        /// Recursively collects generated asset paths for one layer.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <param name="currentDirectory">Current output directory for this layer.</param>
        /// <param name="result">Destination set for generated assets.</param>
        private static void CollectExpectedGeneratedAssetPathsForLayer(Layer layer, string currentDirectory, HashSet<string> result)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return;
            }

            if (info.IsButtonGroup)
            {
                CollectExpectedGeneratedAssetPathsForButtonGroup(layer, currentDirectory, result);
                return;
            }

            if (DoesLayerCreateOutputDirectory(info))
            {
                string childDirectory = Path.Combine(currentDirectory, GetOutputFolderName(layer));
                for (int i = layer.Children.Count - 1; i >= 0; i--)
                {
                    CollectExpectedGeneratedAssetPathsForLayer(layer.Children[i], childDirectory, result);
                }

                if ((LayoutInScene || CreatePrefab) &&
                    info.EffectiveVisible &&
                    info.IsAnimationGroup &&
                    !UseUnityUI &&
                    GetVisibleAnimationFrameLayers(layer).Count > 0)
                {
                    string assetBaseName = GetOutputFolderName(layer);
                    result.Add(NormalizePath(Path.Combine(childDirectory, assetBaseName + ".anim")));
                    result.Add(NormalizePath(Path.Combine(childDirectory, assetBaseName + ".controller")));
                }

                return;
            }

            if (ShouldLayerEmitTextureFile(info))
            {
                string texturePath = Path.Combine(currentDirectory, GetTextureBaseName(layer) + ".png");
                result.Add(NormalizePath(texturePath));
            }
        }

        /// <summary>
        /// Collects generated asset paths produced by a button group.
        /// </summary>
        /// <param name="layer">Button group layer.</param>
        /// <param name="currentDirectory">Current output directory.</param>
        /// <param name="result">Destination set for generated assets.</param>
        private static void CollectExpectedGeneratedAssetPathsForButtonGroup(
            Layer layer,
            string currentDirectory,
            HashSet<string> result)
        {
            foreach (Layer child in layer.Children)
            {
                LayerImportInfo childInfo = GetLayerInfo(child);
                if (!ShouldButtonGroupChildEmitTexture(childInfo))
                {
                    continue;
                }

                string path = Path.Combine(currentDirectory, GetTextureBaseName(child) + ".png");
                result.Add(NormalizePath(path));
            }
        }

        /// <summary>
        /// Converts full file paths to normalized display paths.
        /// </summary>
        /// <param name="fullPath">Absolute file path.</param>
        /// <returns>Path relative to project where possible.</returns>
        private static string ToDisplayPath(string fullPath)
        {
            string normalizedFullPath = NormalizePath(fullPath);
            string projectPath = NormalizePath(GetFullProjectPath()).TrimEnd('/');
            if (normalizedFullPath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFullPath.Substring(projectPath.Length).TrimStart('/');
            }

            return normalizedFullPath;
        }

        /// <summary>
        /// Normalizes a path for case-insensitive comparison.
        /// </summary>
        /// <param name="path">Path to normalize.</param>
        /// <returns>Normalized absolute path using forward slashes.</returns>
        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }

        /// <summary>
        /// Returns whether the given path is under the specified root directory.
        /// </summary>
        /// <param name="path">Path to check.</param>
        /// <param name="rootDirectory">Root directory path.</param>
        /// <returns>True if inside root; otherwise false.</returns>
        private static bool IsPathInsideDirectory(string path, string rootDirectory)
        {
            string normalizedRoot = rootDirectory.TrimEnd('/');
            string normalizedPath = path.TrimEnd('/');
            return normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Deletes a file and its meta file if they exist.
        /// </summary>
        /// <param name="filePath">Absolute file path.</param>
        private static void DeleteFileWithMeta(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                string metaPath = filePath + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format("Failed to delete file '{0}': {1}", filePath, ex.Message));
            }
        }

        /// <summary>
        /// Deletes empty subdirectories under the specified root directory.
        /// </summary>
        /// <param name="rootDirectory">Root directory to clean.</param>
        private static void DeleteEmptySubDirectories(string rootDirectory)
        {
            if (!Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (string subDirectory in Directory.GetDirectories(rootDirectory))
            {
                DeleteEmptySubDirectories(subDirectory);

                bool hasFiles = Directory.GetFiles(subDirectory).Length > 0;
                bool hasDirectories = Directory.GetDirectories(subDirectory).Length > 0;
                if (hasFiles || hasDirectories)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(subDirectory);
                    string metaPath = subDirectory + ".meta";
                    if (File.Exists(metaPath))
                    {
                        File.Delete(metaPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(string.Format("Failed to remove directory '{0}': {1}", subDirectory, ex.Message));
                }
            }
        }

        /// <summary>
        /// Resolves cached import metadata for a layer.
        /// </summary>
        /// <param name="layer">PSD layer.</param>
        /// <returns>Layer metadata if available.</returns>
        private static LayerImportInfo GetLayerInfo(Layer layer)
        {
            if (layer == null || currentLayerInfos == null)
            {
                return null;
            }

            LayerImportInfo info;
            return currentLayerInfos.TryGetValue(layer, out info) ? info : null;
        }

        /// <summary>
        /// Builds import metadata for the current layer tree.
        /// </summary>
        /// <param name="tree">Top-level layer tree.</param>
        /// <returns>Metadata keyed by PSD layer instance.</returns>
        private static Dictionary<Layer, LayerImportInfo> BuildLayerImportInfoMap(List<Layer> tree)
        {
            Dictionary<Layer, LayerImportInfo> infoMap = new Dictionary<Layer, LayerImportInfo>();
            if (tree == null)
            {
                return infoMap;
            }

            foreach (Layer layer in tree)
            {
                CreateLayerImportInfo(layer, null, true, infoMap);
            }

            AssignUniqueSelfNamesRecursively(tree, infoMap);
            AssignUniqueTextureNamesForScope(tree, infoMap);
            return infoMap;
        }

        /// <summary>
        /// Creates import metadata for one layer and its descendants.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <param name="parent">Parent metadata.</param>
        /// <param name="parentVisible">Inherited parent visibility.</param>
        /// <param name="infoMap">Destination map.</param>
        private static void CreateLayerImportInfo(
            Layer layer,
            LayerImportInfo parent,
            bool parentVisible,
            Dictionary<Layer, LayerImportInfo> infoMap)
        {
            LayerImportInfo info = new LayerImportInfo(layer)
            {
                Parent = parent,
                EffectiveVisible = parentVisible && layer.Visible,
                IsFolderLike = layer.Children.Count > 0 || layer.Rect.width == 0,
                AnimationFps = GetAnimationFps(layer.Name)
            };

            info.IsButtonGroup = info.IsFolderLike && layer.Name.ContainsIgnoreCase("|Button");
            info.IsAnimationGroup = info.IsFolderLike && layer.Name.ContainsIgnoreCase("|Animation");
            info.ButtonRole = parent != null && parent.IsButtonGroup ? GetButtonChildRole(layer) : ButtonChildRole.None;
            info.ExplicitAnchorPreset = ParseAnchorPreset(GetAnchorParsingName(info));
            info.AnchorPreset = ResolveAnchorPreset(info);

            infoMap[layer] = info;

            foreach (Layer child in layer.Children)
            {
                CreateLayerImportInfo(child, info, info.EffectiveVisible, infoMap);
            }

            Rect layoutRect;
            info.HasLayoutRect = TryResolveLayerLayoutRect(info, infoMap, out layoutRect);
            info.LayoutRect = layoutRect;
        }

        /// <summary>
        /// Resolves the effective layout rect used to place one layer in Unity UI.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <param name="infoMap">Layer metadata map.</param>
        /// <param name="layoutRect">Resolved rect when available.</param>
        /// <returns>True when a valid layout rect exists.</returns>
        private static bool TryResolveLayerLayoutRect(
            LayerImportInfo info,
            Dictionary<Layer, LayerImportInfo> infoMap,
            out Rect layoutRect)
        {
            layoutRect = default(Rect);
            if (info == null || info.Layer == null)
            {
                return false;
            }

            if (!info.IsFolderLike)
            {
                if (info.Layer.Rect.width > 0f && info.Layer.Rect.height > 0f)
                {
                    layoutRect = info.Layer.Rect;
                    return true;
                }

                return false;
            }

            bool hasBounds = false;
            Rect combinedRect = default(Rect);
            foreach (Layer child in info.Layer.Children)
            {
                LayerImportInfo childInfo;
                if (!infoMap.TryGetValue(child, out childInfo) || childInfo == null || !childInfo.EffectiveVisible || !childInfo.HasLayoutRect)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    combinedRect = childInfo.LayoutRect;
                    hasBounds = true;
                }
                else
                {
                    combinedRect = CombineRects(combinedRect, childInfo.LayoutRect);
                }
            }

            if (hasBounds)
            {
                layoutRect = combinedRect;
                return true;
            }

            if (info.Layer.Rect.width > 0f && info.Layer.Rect.height > 0f)
            {
                layoutRect = info.Layer.Rect;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Combines two rects into one bounding rect.
        /// </summary>
        /// <param name="first">First rect.</param>
        /// <param name="second">Second rect.</param>
        /// <returns>Bounding rect containing both inputs.</returns>
        private static Rect CombineRects(Rect first, Rect second)
        {
            float xMin = Mathf.Min(first.xMin, second.xMin);
            float yMin = Mathf.Min(first.yMin, second.yMin);
            float xMax = Mathf.Max(first.xMax, second.xMax);
            float yMax = Mathf.Max(first.yMax, second.yMax);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        /// <summary>
        /// Assigns stable unique names among siblings for all layers.
        /// </summary>
        /// <param name="siblings">Sibling layers.</param>
        /// <param name="infoMap">Layer metadata map.</param>
        private static void AssignUniqueSelfNamesRecursively(List<Layer> siblings, Dictionary<Layer, LayerImportInfo> infoMap)
        {
            if (siblings == null || siblings.Count == 0)
            {
                return;
            }

            List<LayerImportInfo> siblingInfos = siblings.Select(layer => infoMap[layer]).ToList();
            AssignUniqueNames(
                siblingInfos,
                GetStableSelfBaseName,
                (info, uniqueName) => info.UniqueSelfName = uniqueName,
                "Layer");

            foreach (Layer sibling in siblings)
            {
                AssignUniqueSelfNamesRecursively(sibling.Children, infoMap);
            }
        }

        /// <summary>
        /// Assigns unique texture/file names inside one output directory scope.
        /// </summary>
        /// <param name="siblings">Sibling layers that share one output directory scope.</param>
        /// <param name="infoMap">Layer metadata map.</param>
        private static void AssignUniqueTextureNamesForScope(List<Layer> siblings, Dictionary<Layer, LayerImportInfo> infoMap)
        {
            if (siblings == null || siblings.Count == 0)
            {
                return;
            }

            List<LayerImportInfo> fileEmitters = CollectFileEmittersForScope(siblings, infoMap);
            AssignUniqueNames(
                fileEmitters,
                GetPreferredTextureBaseName,
                (info, uniqueName) => info.UniqueTextureName = uniqueName,
                "Layer");

            foreach (Layer sibling in siblings)
            {
                LayerImportInfo info = infoMap[sibling];
                if (DoesLayerCreateOutputDirectory(info))
                {
                    AssignUniqueTextureNamesForScope(sibling.Children, infoMap);
                }
            }
        }

        /// <summary>
        /// Collects all layers that export texture files in the current output directory.
        /// </summary>
        /// <param name="siblings">Sibling layers in the current scope.</param>
        /// <param name="infoMap">Layer metadata map.</param>
        /// <returns>Ordered file emitters for the current directory.</returns>
        private static List<LayerImportInfo> CollectFileEmittersForScope(List<Layer> siblings, Dictionary<Layer, LayerImportInfo> infoMap)
        {
            List<LayerImportInfo> emitters = new List<LayerImportInfo>();

            foreach (Layer sibling in siblings)
            {
                LayerImportInfo info = infoMap[sibling];
                if (info.IsButtonGroup)
                {
                    foreach (Layer child in sibling.Children)
                    {
                        LayerImportInfo childInfo = infoMap[child];
                        if (ShouldButtonGroupChildEmitTexture(childInfo))
                        {
                            emitters.Add(childInfo);
                        }
                    }

                    continue;
                }

                if (!info.IsFolderLike && ShouldLayerEmitTextureFile(info))
                {
                    emitters.Add(info);
                }
            }

            return emitters;
        }

        /// <summary>
        /// Assigns unique suffixes like _2/_3 while preserving the first occurrence.
        /// </summary>
        /// <typeparam name="T">Item type.</typeparam>
        /// <param name="items">Items in stable order.</param>
        /// <param name="baseNameSelector">Gets the base name for one item.</param>
        /// <param name="assign">Applies the resolved unique name.</param>
        /// <param name="fallbackBaseName">Fallback when the base name is empty.</param>
        private static void AssignUniqueNames<T>(
            IEnumerable<T> items,
            Func<T, string> baseNameSelector,
            Action<T, string> assign,
            string fallbackBaseName)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (T item in items)
            {
                string baseName = SanitizeStableName(baseNameSelector(item), fallbackBaseName);
                int currentCount;
                counts.TryGetValue(baseName, out currentCount);
                currentCount++;
                counts[baseName] = currentCount;

                assign(item, currentCount == 1 ? baseName : string.Format("{0}_{1}", baseName, currentCount));
            }
        }

        /// <summary>
        /// Gets the stable sibling-name base for a layer.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <returns>Tag-stripped, sanitized base name.</returns>
        private static string GetStableSelfBaseName(LayerImportInfo info)
        {
            if (info == null)
            {
                return "Layer";
            }

            if (info.IsAnimationGroup)
            {
                return SanitizeStableName(GetAnimationLayerBaseName(info.Layer.Name), "Animation");
            }

            if (info.IsButtonGroup)
            {
                return SanitizeStableName(RemoveTagIgnoreCase(info.Layer.Name, "|Button"), "Button");
            }

            if (info.Parent != null && info.Parent.IsButtonGroup)
            {
                return SanitizeStableName(GetButtonChildBaseName(info.Layer), info.Layer.IsTextLayer ? "Text" : "Layer");
            }

            return SanitizeStableName(info.Layer.Name, info.IsFolderLike ? "Folder" : "Layer");
        }

        /// <summary>
        /// Gets the preferred texture base name inside the current output directory.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <returns>Preferred texture base name.</returns>
        private static string GetPreferredTextureBaseName(LayerImportInfo info)
        {
            if (info == null)
            {
                return "Layer";
            }

            if (info.Parent != null && info.Parent.IsButtonGroup)
            {
                string parentName = info.Parent.UniqueSelfName ?? GetStableSelfBaseName(info.Parent);
                string childName = info.UniqueSelfName ?? GetStableSelfBaseName(info);
                return SanitizeStableName(string.Format("{0}_{1}", parentName, childName), "Layer");
            }

            return info.UniqueSelfName ?? GetStableSelfBaseName(info);
        }

        /// <summary>
        /// Gets the base name used for animation folders/assets.
        /// </summary>
        /// <param name="name">Original layer name.</param>
        /// <returns>Animation base name.</returns>
        private static string GetAnimationLayerBaseName(string name)
        {
            string strippedName = RemoveAnimationTags(name);
            string[] nameParts = strippedName.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            string baseName = nameParts.Length > 0 ? nameParts[0] : strippedName;
            return string.IsNullOrWhiteSpace(baseName) ? "Animation" : baseName.Trim();
        }

        /// <summary>
        /// Removes animation-related tags from a layer name.
        /// </summary>
        /// <param name="name">Layer name.</param>
        /// <returns>Name without animation tags.</returns>
        private static string RemoveAnimationTags(string name)
        {
            string strippedName = RemoveTagIgnoreCase(name, "|Animation");
            return Regex.Replace(strippedName, "\\|FPS=[^|]+", string.Empty, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Parses animation FPS from the layer name.
        /// </summary>
        /// <param name="name">Layer name.</param>
        /// <returns>Frame rate, defaulting to 30 when unspecified.</returns>
        private static float GetAnimationFps(string name)
        {
            float fps = 30f;
            if (string.IsNullOrEmpty(name))
            {
                return fps;
            }

            string[] args = name.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string arg in args)
            {
                if (!arg.StartsWith("FPS=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                float parsedFps;
                if (float.TryParse(arg.Substring(4), out parsedFps))
                {
                    fps = parsedFps;
                }
                else
                {
                    Debug.LogError(string.Format("Unable to parse FPS: \"{0}\"", arg));
                }

                break;
            }

            return fps;
        }

        /// <summary>
        /// Resolves the parsed anchor preset for one layer.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <returns>Parsed preset or <see cref="AnchorNamePreset.None"/> when no prefix applies.</returns>
        private static AnchorNamePreset ResolveAnchorPreset(LayerImportInfo info)
        {
            if (!EnableAutoAnchorByName || info == null)
            {
                return AnchorNamePreset.None;
            }

            if (info.ExplicitAnchorPreset != AnchorNamePreset.None)
            {
                return info.ExplicitAnchorPreset;
            }

            if (info.Parent != null &&
                info.Parent.IsFolderLike &&
                info.Parent.AnchorPreset != AnchorNamePreset.None)
            {
                return info.Parent.AnchorPreset;
            }

            return AnchorNamePreset.None;
        }

        /// <summary>
        /// Gets the source name used for anchor-prefix parsing.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <returns>Name without tool tags.</returns>
        private static string GetAnchorParsingName(LayerImportInfo info)
        {
            if (info == null || info.Layer == null || string.IsNullOrEmpty(info.Layer.Name))
            {
                return string.Empty;
            }

            string name = info.Layer.Name;
            if (info.IsAnimationGroup)
            {
                return GetAnimationLayerBaseName(name);
            }

            if (info.IsButtonGroup)
            {
                return RemoveTagIgnoreCase(name, "|Button");
            }

            if (info.Parent != null && info.Parent.IsButtonGroup)
            {
                return GetButtonChildBaseName(info.Layer);
            }

            int pipeIndex = name.IndexOf('|');
            return pipeIndex >= 0 ? name.Substring(0, pipeIndex) : name;
        }

        /// <summary>
        /// Parses a layer-name prefix into an anchor preset.
        /// </summary>
        /// <param name="name">Layer or folder name without tool tags.</param>
        /// <returns>Resolved anchor preset.</returns>
        private static AnchorNamePreset ParseAnchorPreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return AnchorNamePreset.None;
            }

            string trimmedName = name.TrimStart();
            if (trimmedName.StartsWith("全局", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.Global;
            }

            if (trimmedName.StartsWith("左上", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.TopLeft;
            }

            if (trimmedName.StartsWith("左下", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.BottomLeft;
            }

            if (trimmedName.StartsWith("右上", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.TopRight;
            }

            if (trimmedName.StartsWith("右下", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.BottomRight;
            }

            if (trimmedName.StartsWith("中间", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.Center;
            }

            if (trimmedName.StartsWith("左中", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.LeftMiddle;
            }

            if (trimmedName.StartsWith("右中", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.RightMiddle;
            }

            if (trimmedName.StartsWith("上中", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.TopMiddle;
            }

            if (trimmedName.StartsWith("下中", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.BottomMiddle;
            }

            if (trimmedName.StartsWith("上", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.TopMiddle;
            }

            if (trimmedName.StartsWith("下", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.BottomMiddle;
            }

            if (trimmedName.StartsWith("左", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.LeftMiddle;
            }

            if (trimmedName.StartsWith("右", StringComparison.OrdinalIgnoreCase))
            {
                return AnchorNamePreset.RightMiddle;
            }

            return AnchorNamePreset.None;
        }

        /// <summary>
        /// Gets the role of a button child layer.
        /// </summary>
        /// <param name="layer">Button child layer.</param>
        /// <returns>Resolved button role.</returns>
        private static ButtonChildRole GetButtonChildRole(Layer layer)
        {
            if (layer == null)
            {
                return ButtonChildRole.None;
            }

            if (layer.Name.ContainsIgnoreCase("|Disabled"))
            {
                return ButtonChildRole.Disabled;
            }

            if (layer.Name.ContainsIgnoreCase("|Highlighted"))
            {
                return ButtonChildRole.Highlighted;
            }

            if (layer.Name.ContainsIgnoreCase("|Pressed"))
            {
                return ButtonChildRole.Pressed;
            }

            if (layer.Name.ContainsIgnoreCase("|Default") ||
                layer.Name.ContainsIgnoreCase("|Enabled") ||
                layer.Name.ContainsIgnoreCase("|Normal") ||
                layer.Name.ContainsIgnoreCase("|Up"))
            {
                return ButtonChildRole.Default;
            }

            if (layer.Name.ContainsIgnoreCase("|Text") && !layer.IsTextLayer)
            {
                return ButtonChildRole.TextImage;
            }

            return ButtonChildRole.None;
        }

        /// <summary>
        /// Gets a button child name with button-state tags removed.
        /// </summary>
        /// <param name="layer">Button child layer.</param>
        /// <returns>Tag-stripped base name.</returns>
        private static string GetButtonChildBaseName(Layer layer)
        {
            string name = layer != null ? layer.Name : string.Empty;
            name = RemoveTagIgnoreCase(name, "|Disabled");
            name = RemoveTagIgnoreCase(name, "|Highlighted");
            name = RemoveTagIgnoreCase(name, "|Pressed");
            name = RemoveTagIgnoreCase(name, "|Default");
            name = RemoveTagIgnoreCase(name, "|Enabled");
            name = RemoveTagIgnoreCase(name, "|Normal");
            name = RemoveTagIgnoreCase(name, "|Up");

            if (layer != null && !layer.IsTextLayer)
            {
                name = RemoveTagIgnoreCase(name, "|Text");
            }

            return name;
        }

        /// <summary>
        /// Removes one tag from a name without case sensitivity.
        /// </summary>
        /// <param name="name">Source string.</param>
        /// <param name="tag">Tag to remove.</param>
        /// <returns>Updated string.</returns>
        private static string RemoveTagIgnoreCase(string name, string tag)
        {
            return string.IsNullOrEmpty(name) ? string.Empty : name.ReplaceIgnoreCase(tag, string.Empty);
        }

        /// <summary>
        /// Converts a name into a stable filesystem-safe identifier without extra logging.
        /// </summary>
        /// <param name="name">Raw name.</param>
        /// <param name="fallbackName">Fallback when the name is empty.</param>
        /// <returns>Sanitized stable name.</returns>
        private static string SanitizeStableName(string name, string fallbackName)
        {
            string safeName = MakeNameSafeSilently(string.IsNullOrWhiteSpace(name) ? fallbackName : name.Trim());
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = MakeNameSafeSilently(fallbackName);
            }

            return string.IsNullOrWhiteSpace(safeName) ? fallbackName : safeName;
        }

        /// <summary>
        /// Converts a name into a filesystem-safe identifier without logging.
        /// </summary>
        /// <param name="name">Name to sanitize.</param>
        /// <returns>Sanitized name.</returns>
        private static string MakeNameSafeSilently(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string trimmedName = name.Trim();
            StringBuilder builder = new StringBuilder(trimmedName.Length);
            foreach (char currentChar in trimmedName)
            {
                builder.Append(InvalidGeneratedNameChars.Contains(currentChar) || char.IsControl(currentChar) ? '_' : currentChar);
            }

            string sanitized = builder.ToString().Trim().TrimEnd('.');
            while (sanitized.EndsWith(" ", StringComparison.Ordinal))
            {
                sanitized = sanitized.Substring(0, sanitized.Length - 1);
            }

            if (ReservedGeneratedNames.Contains(sanitized))
            {
                sanitized += "_";
            }

            return sanitized;
        }

        /// <summary>
        /// Returns true if the layer creates a dedicated output subdirectory.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <returns>True if the layer writes into a dedicated subdirectory.</returns>
        private static bool DoesLayerCreateOutputDirectory(LayerImportInfo info)
        {
            return info != null && info.IsFolderLike && !info.IsButtonGroup;
        }

        /// <summary>
        /// Returns true if the layer exports its own texture file.
        /// </summary>
        /// <param name="info">Layer metadata.</param>
        /// <returns>True if the layer exports a texture file.</returns>
        private static bool ShouldLayerEmitTextureFile(LayerImportInfo info)
        {
            if (info == null || info.IsFolderLike || info.Layer.Rect.width <= 0 || info.Layer.Rect.height <= 0)
            {
                return false;
            }

            if (!info.Layer.IsTextLayer)
            {
                return true;
            }

            return !info.EffectiveVisible;
        }

        /// <summary>
        /// Returns true if a button child should export a texture file.
        /// </summary>
        /// <param name="childInfo">Button child metadata.</param>
        /// <returns>True if a texture should be exported.</returns>
        private static bool ShouldButtonGroupChildEmitTexture(LayerImportInfo childInfo)
        {
            if (childInfo == null || childInfo.IsFolderLike)
            {
                return false;
            }

            if (childInfo.ButtonRole != ButtonChildRole.None)
            {
                return !childInfo.Layer.IsTextLayer || !childInfo.EffectiveVisible;
            }

            return !childInfo.EffectiveVisible && ShouldLayerEmitTextureFile(childInfo);
        }

        /// <summary>
        /// Returns true if runtime generation already creates this button child's texture.
        /// </summary>
        /// <param name="childInfo">Button child metadata.</param>
        /// <returns>True if runtime creation already handles the texture export.</returns>
        private static bool IsButtonChildHandledByRuntime(LayerImportInfo childInfo)
        {
            return childInfo != null &&
                childInfo.EffectiveVisible &&
                childInfo.ButtonRole != ButtonChildRole.None &&
                !childInfo.Layer.IsTextLayer;
        }

        /// <summary>
        /// Gets all visible frame layers for an animation group.
        /// </summary>
        /// <param name="animationLayer">Animation group layer.</param>
        /// <returns>Visible art-layer frames in order.</returns>
        private static List<Layer> GetVisibleAnimationFrameLayers(Layer animationLayer)
        {
            List<Layer> frames = new List<Layer>();
            if (animationLayer == null)
            {
                return frames;
            }

            foreach (Layer child in animationLayer.Children)
            {
                LayerImportInfo childInfo = GetLayerInfo(child);
                if (childInfo == null || !childInfo.EffectiveVisible || childInfo.IsFolderLike || child.IsTextLayer)
                {
                    continue;
                }

                if (child.Rect.width <= 0 || child.Rect.height <= 0)
                {
                    continue;
                }

                frames.Add(child);
            }

            return frames;
        }

        /// <summary>
        /// Returns true if a button group still has visible runtime content after filtering hidden layers.
        /// </summary>
        /// <param name="buttonLayer">Button group layer.</param>
        /// <returns>True if the button object should be created.</returns>
        private static bool HasVisibleButtonRuntimeContent(Layer buttonLayer)
        {
            if (buttonLayer == null)
            {
                return false;
            }

            foreach (Layer child in buttonLayer.Children)
            {
                if (IsButtonChildHandledByRuntime(GetLayerInfo(child)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if any visible runtime content exists in the tree.
        /// </summary>
        /// <param name="tree">Top-level tree.</param>
        /// <returns>True if scene/prefab objects should be created.</returns>
        private static bool HasVisibleRuntimeContent(List<Layer> tree)
        {
            if (!(LayoutInScene || CreatePrefab) || tree == null)
            {
                return false;
            }

            foreach (Layer layer in tree)
            {
                if (HasVisibleRuntimeContent(layer))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if a layer or any descendants create visible runtime content.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <returns>True if runtime content exists.</returns>
        private static bool HasVisibleRuntimeContent(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null || !info.EffectiveVisible)
            {
                return false;
            }

            if (info.IsButtonGroup)
            {
                return UseUnityUI && HasVisibleButtonRuntimeContent(layer);
            }

            if (info.IsAnimationGroup)
            {
                return !UseUnityUI && GetVisibleAnimationFrameLayers(layer).Count > 0;
            }

            if (!info.IsFolderLike)
            {
                return true;
            }

            foreach (Layer child in layer.Children)
            {
                if (HasVisibleRuntimeContent(child))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the runtime object name for a layer.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <returns>Resolved runtime name.</returns>
        private static string GetRuntimeObjectName(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return MakeNameSafe(layer != null ? layer.Name : "Layer");
            }

            if (info.Parent != null &&
                info.Parent.IsButtonGroup &&
                info.ButtonRole == ButtonChildRole.TextImage &&
                !string.IsNullOrEmpty(info.UniqueTextureName))
            {
                return info.UniqueTextureName;
            }

            return string.IsNullOrEmpty(info.UniqueSelfName)
                ? SanitizeStableName(info.Layer.Name, info.IsFolderLike ? "Folder" : "Layer")
                : info.UniqueSelfName;
        }

        /// <summary>
        /// Gets the output folder name for a folder-like layer.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <returns>Resolved folder name.</returns>
        private static string GetOutputFolderName(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            return info != null && !string.IsNullOrEmpty(info.UniqueSelfName)
                ? SanitizeStableName(info.UniqueSelfName, "Folder")
                : MakeNameSafe(layer.Name);
        }

        /// <summary>
        /// Gets the texture base name for a layer.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <returns>Resolved texture base name.</returns>
        private static string GetTextureBaseName(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info != null && !string.IsNullOrEmpty(info.UniqueTextureName))
            {
                return SanitizeStableName(info.UniqueTextureName, layer != null && layer.IsTextLayer ? "Text" : "Layer");
            }

            if (info != null && !string.IsNullOrEmpty(info.UniqueSelfName))
            {
                return SanitizeStableName(info.UniqueSelfName, layer != null && layer.IsTextLayer ? "Text" : "Layer");
            }

            return MakeNameSafe(layer.Name);
        }

        /// <summary>
        /// Stores analyzed import conflicts for a single PSD import.
        /// </summary>
        private sealed class ImportConflictAnalysis
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ImportConflictAnalysis"/> class.
            /// </summary>
            public ImportConflictAnalysis()
            {
                SameNamePaths = new List<string>();
                DeletedPaths = new List<string>();
            }

            /// <summary>
            /// Gets or sets a value indicating whether the output folder already exists.
            /// </summary>
            public bool HasExistingOutputDirectory { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the target prefab already exists.
            /// </summary>
            public bool HasExistingPrefab { get; set; }

            /// <summary>
            /// Gets or sets the output folder path relative to project.
            /// </summary>
            public string OutputRelativePath { get; set; }

            /// <summary>
            /// Gets or sets the output folder path as a normalized full path.
            /// </summary>
            public string OutputFullPath { get; set; }

            /// <summary>
            /// Gets or sets the prefab path relative to project.
            /// </summary>
            public string PrefabRelativePath { get; set; }

            /// <summary>
            /// Gets or sets the prefab path as a normalized full path.
            /// </summary>
            public string PrefabFullPath { get; set; }

            /// <summary>
            /// Gets same-name files that can be updated.
            /// </summary>
            public List<string> SameNamePaths { get; private set; }

            /// <summary>
            /// Gets stale files that can be deleted.
            /// </summary>
            public List<string> DeletedPaths { get; private set; }

            /// <summary>
            /// Gets a value indicating whether any existing import target was found.
            /// </summary>
            public bool HasExistingTargets
            {
                get
                {
                    return HasExistingOutputDirectory || HasExistingPrefab;
                }
            }

            /// <summary>
            /// Gets a value indicating whether there are selectable entries for update/delete.
            /// </summary>
            public bool HasSelectableEntries
            {
                get
                {
                    return SameNamePaths.Count > 0 || DeletedPaths.Count > 0;
                }
            }
        }

        /// <summary>
        /// Stores user-selected update/delete operations for an import run.
        /// </summary>
        private sealed class ImportConflictSelection
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ImportConflictSelection"/> class.
            /// </summary>
            public ImportConflictSelection()
            {
                PathsToUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                PathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            /// <summary>
            /// Gets or sets a value indicating whether the selection is confirmed by user.
            /// </summary>
            public bool Confirmed { get; set; }

            /// <summary>
            /// Gets files selected for overwrite/update.
            /// </summary>
            public HashSet<string> PathsToUpdate { get; private set; }

            /// <summary>
            /// Gets files selected for deletion.
            /// </summary>
            public HashSet<string> PathsToDelete { get; private set; }
        }

        /// <summary>
        /// UI entry representing a selectable file operation.
        /// </summary>
        private sealed class ConflictPathOption
        {
            /// <summary>
            /// Gets or sets the normalized full file path.
            /// </summary>
            public string FullPath { get; set; }

            /// <summary>
            /// Gets or sets the display path shown in the dialog.
            /// </summary>
            public string DisplayPath { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this entry is selected.
            /// </summary>
            public bool Selected { get; set; }
        }

        /// <summary>
        /// Selection window used to choose which same-name files to update and which stale files to delete.
        /// </summary>
        private sealed class ImportConflictSelectionWindow : EditorWindow
        {
            /// <summary>
            /// Same-name options.
            /// </summary>
            private readonly List<ConflictPathOption> updateOptions = new List<ConflictPathOption>();

            /// <summary>
            /// Stale-file options.
            /// </summary>
            private readonly List<ConflictPathOption> deleteOptions = new List<ConflictPathOption>();

            /// <summary>
            /// Scroll position for list rendering.
            /// </summary>
            private Vector2 scrollPosition;

            /// <summary>
            /// Callback fired once dialog is closed.
            /// </summary>
            private Action<ImportConflictSelection> onClose;

            /// <summary>
            /// Guards against invoking callback more than once.
            /// </summary>
            private bool callbackSent;

            /// <summary>
            /// Opens the conflict selection window.
            /// </summary>
            /// <param name="analysis">Conflict analysis data.</param>
            /// <param name="defaultSelection">Default checked entries.</param>
            /// <param name="onCloseCallback">Callback invoked on confirm/cancel.</param>
            public static void ShowDialog(
                ImportConflictAnalysis analysis,
                ImportConflictSelection defaultSelection,
                Action<ImportConflictSelection> onCloseCallback)
            {
                ImportConflictSelectionWindow window = CreateInstance<ImportConflictSelectionWindow>();
                window.titleContent = new GUIContent("PSD 更新与删除");
                window.minSize = new Vector2(760f, 420f);
                window.Initialize(analysis, defaultSelection, onCloseCallback);
                window.ShowUtility();
                window.Focus();
            }

            /// <summary>
            /// Initializes this window with selectable entries.
            /// </summary>
            /// <param name="analysis">Conflict analysis data.</param>
            /// <param name="defaultSelection">Default checked entries.</param>
            /// <param name="onCloseCallback">Callback invoked on confirm/cancel.</param>
            private void Initialize(
                ImportConflictAnalysis analysis,
                ImportConflictSelection defaultSelection,
                Action<ImportConflictSelection> onCloseCallback)
            {
                onClose = onCloseCallback;

                HashSet<string> defaultUpdates = defaultSelection != null
                    ? defaultSelection.PathsToUpdate
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                HashSet<string> defaultDeletes = defaultSelection != null
                    ? defaultSelection.PathsToDelete
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string sameNamePath in analysis.SameNamePaths)
                {
                    string normalizedPath = NormalizePath(sameNamePath);
                    updateOptions.Add(new ConflictPathOption
                    {
                        FullPath = normalizedPath,
                        DisplayPath = ToDisplayPath(normalizedPath),
                        Selected = defaultUpdates.Contains(normalizedPath)
                    });
                }

                foreach (string stalePath in analysis.DeletedPaths)
                {
                    string normalizedPath = NormalizePath(stalePath);
                    deleteOptions.Add(new ConflictPathOption
                    {
                        FullPath = normalizedPath,
                        DisplayPath = ToDisplayPath(normalizedPath),
                        Selected = defaultDeletes.Contains(normalizedPath)
                    });
                }
            }

            /// <summary>
            /// Draws window GUI.
            /// </summary>
            private void OnGUI()
            {
                EditorGUILayout.HelpBox(
                    "勾选“同名文件”会覆盖现有文件；勾选“删除文件”会移除旧文件。未勾选项将保持不变。",
                    MessageType.Info);

                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                DrawOptionsSection("同名文件（勾选后更新）", updateOptions, "没有同名文件。");
                GUILayout.Space(8f);
                DrawOptionsSection("删除文件（勾选后删除）", deleteOptions, "没有可删除文件。");
                EditorGUILayout.EndScrollView();

                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("取消", GUILayout.Height(28f)))
                {
                    CloseWithCancel();
                }

                if (GUILayout.Button("确定", GUILayout.Height(28f)))
                {
                    CloseWithSelection();
                }

                EditorGUILayout.EndHorizontal();
            }

            /// <summary>
            /// Ensures cancellation callback is emitted when window is closed directly.
            /// </summary>
            private void OnDestroy()
            {
                if (!callbackSent)
                {
                    NotifyClose(new ImportConflictSelection { Confirmed = false });
                }
            }

            /// <summary>
            /// Draws one selectable section.
            /// </summary>
            /// <param name="title">Section title.</param>
            /// <param name="options">Selectable options.</param>
            /// <param name="emptyMessage">Message shown when no options exist.</param>
            private static void DrawOptionsSection(string title, List<ConflictPathOption> options, string emptyMessage)
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                if (options.Count == 0)
                {
                    EditorGUILayout.LabelField(emptyMessage);
                    return;
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("全选", GUILayout.Width(70f)))
                {
                    SetSelection(options, true);
                }

                if (GUILayout.Button("全不选", GUILayout.Width(70f)))
                {
                    SetSelection(options, false);
                }

                EditorGUILayout.EndHorizontal();

                foreach (ConflictPathOption option in options)
                {
                    option.Selected = EditorGUILayout.ToggleLeft(option.DisplayPath, option.Selected);
                }
            }

            /// <summary>
            /// Sets selection state for all options in one section.
            /// </summary>
            /// <param name="options">Options to update.</param>
            /// <param name="selected">Target selected state.</param>
            private static void SetSelection(List<ConflictPathOption> options, bool selected)
            {
                foreach (ConflictPathOption option in options)
                {
                    option.Selected = selected;
                }
            }

            /// <summary>
            /// Closes window and emits confirmed selection.
            /// </summary>
            private void CloseWithSelection()
            {
                ImportConflictSelection selection = new ImportConflictSelection
                {
                    Confirmed = true
                };

                foreach (ConflictPathOption option in updateOptions.Where(option => option.Selected))
                {
                    selection.PathsToUpdate.Add(option.FullPath);
                }

                foreach (ConflictPathOption option in deleteOptions.Where(option => option.Selected))
                {
                    selection.PathsToDelete.Add(option.FullPath);
                }

                NotifyClose(selection);
                Close();
            }

            /// <summary>
            /// Closes window and emits cancellation.
            /// </summary>
            private void CloseWithCancel()
            {
                NotifyClose(new ImportConflictSelection { Confirmed = false });
                Close();
            }

            /// <summary>
            /// Emits close callback once.
            /// </summary>
            /// <param name="selection">Selection result.</param>
            private void NotifyClose(ImportConflictSelection selection)
            {
                if (callbackSent)
                {
                    return;
                }

                callbackSent = true;
                Action<ImportConflictSelection> callback = onClose;
                onClose = null;
                if (callback != null)
                {
                    callback(selection);
                }
            }
        }

        /// <summary>
        /// Resolves the configured target canvas in the current scene.
        /// </summary>
        /// <returns>The matching canvas if found; otherwise null.</returns>
        private static Canvas ResolveTargetCanvas()
        {
            if (string.IsNullOrEmpty(TargetCanvasPath))
            {
                return null;
            }

            Canvas[] canvases = FindAllCanvases();
            foreach (Canvas canvas in canvases)
            {
                if (GetHierarchyPath(canvas.transform) == TargetCanvasPath)
                {
                    return canvas;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds all canvases in the loaded scene(s), using the newest available Unity API.
        /// </summary>
        /// <returns>Array of canvases.</returns>
        private static Canvas[] FindAllCanvases()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<Canvas>();
#endif
        }

        /// <summary>
        /// Gets the rect size of the target canvas if possible; otherwise falls back to PSD canvas size.
        /// </summary>
        /// <param name="targetCanvas">The target canvas.</param>
        /// <returns>Canvas rect size for mapping.</returns>
        private static Vector2 GetTargetCanvasRectSize(Canvas targetCanvas)
        {
            if (targetCanvas == null)
            {
                return CanvasSize;
            }

            CanvasScaler scaler = GetCanvasScaler(targetCanvas);
            if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                Vector2 referenceResolution = scaler.referenceResolution;
                if (referenceResolution.x > 0 && referenceResolution.y > 0)
                {
                    // For Scale With Screen Size, layout authoring coordinates are based on reference resolution.
                    return referenceResolution;
                }
            }

            RectTransform canvasRectTransform = targetCanvas.transform as RectTransform;
            if (canvasRectTransform == null)
            {
                return CanvasSize;
            }

            Rect rect = canvasRectTransform.rect;
            if (rect.width <= 0 || rect.height <= 0)
            {
                return CanvasSize;
            }

            return rect.size;
        }

        /// <summary>
        /// Gets the most relevant <see cref="CanvasScaler"/> for a target canvas.
        /// </summary>
        /// <param name="targetCanvas">The target canvas.</param>
        /// <returns>The canvas scaler if found; otherwise null.</returns>
        private static CanvasScaler GetCanvasScaler(Canvas targetCanvas)
        {
            if (targetCanvas == null)
            {
                return null;
            }

            CanvasScaler scaler = targetCanvas.GetComponent<CanvasScaler>();
            if (scaler != null)
            {
                return scaler;
            }

            Canvas rootCanvas = targetCanvas.rootCanvas;
            return rootCanvas != null ? rootCanvas.GetComponent<CanvasScaler>() : null;
        }

        /// <summary>
        /// Gets a hierarchy path for the given transform in the form "Root/Child/SubChild".
        /// </summary>
        /// <param name="transform">The transform to build a path for.</param>
        /// <returns>The hierarchy path string.</returns>
        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            List<string> pathParts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                pathParts.Add(current.name);
                current = current.parent;
            }

            pathParts.Reverse();
            return string.Join("/", pathParts.ToArray());
        }

        /// <summary>
        /// Gets the output folder path relative to the Unity project for generated assets.
        /// </summary>
        /// <param name="assetPath">The PSD asset path, like "Assets/UI/Menu.psd".</param>
        /// <returns>The output folder path, like "Assets/UI/Menu".</returns>
        private static string GetOutputRootRelativePath(string assetPath)
        {
            string assetDirectory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(assetDirectory))
            {
                assetDirectory = "Assets";
            }

            assetDirectory = assetDirectory.Replace('\\', '/');

            string basePath = OutputMode == OutputDirectoryMode.AssetsRoot ? "Assets" : assetDirectory;
            string folderName = string.IsNullOrEmpty(OutputFolderName) ? PsdName : OutputFolderName.Trim();
            if (string.IsNullOrEmpty(folderName))
            {
                folderName = PsdName;
            }

            folderName = MakeNameSafe(folderName);
            return string.Format("{0}/{1}", basePath.TrimEnd('/'), folderName).Replace('\\', '/');
        }

        /// <summary>
        /// Gets prefab output path relative to project.
        /// </summary>
        /// <param name="outputRelativePath">The generated output folder path.</param>
        /// <returns>Prefab asset path relative to project.</returns>
        private static string GetPrefabRelativePath(string outputRelativePath)
        {
            if (PrefabMode == PrefabOutputMode.InsideOutputFolder)
            {
                return string.Format("{0}/{1}.prefab", outputRelativePath, PsdName);
            }

            string outputParent = Path.GetDirectoryName(outputRelativePath);
            if (string.IsNullOrEmpty(outputParent))
            {
                outputParent = "Assets";
            }

            outputParent = outputParent.Replace('\\', '/').TrimEnd('/');
            return string.Format("{0}/{1}.prefab", outputParent, PsdName);
        }

        /// <summary>
        /// Constructs a tree collection based on the PSD layer groups from the raw list of layers.
        /// </summary>
        /// <param name="flatLayers">The flat list of all layers.</param>
        /// <returns>The layers reorganized into a tree structure based on the layer groups.</returns>
        private static List<Layer> BuildLayerTree(List<Layer> flatLayers)
        {
            // There is no tree to create if there are no layers
            if (flatLayers == null)
            {
                return null;
            }

            // PSD layers are stored backwards (with End Groups before Start Groups), so we must reverse them
            flatLayers.Reverse();

            List<Layer> tree = new List<Layer>();
            Layer currentGroupLayer = null;
            Stack<Layer> previousLayers = new Stack<Layer>();

            foreach (Layer layer in flatLayers)
            {
                if (IsEndGroup(layer))
                {
                    if (previousLayers.Count > 0)
                    {
                        Layer previousLayer = previousLayers.Pop();
                        previousLayer.Children.Add(currentGroupLayer);
                        currentGroupLayer = previousLayer;
                    }
                    else if (currentGroupLayer != null)
                    {
                        tree.Add(currentGroupLayer);
                        currentGroupLayer = null;
                    }
                }
                else if (IsStartGroup(layer))
                {
                    // push the current layer
                    if (currentGroupLayer != null)
                    {
                        previousLayers.Push(currentGroupLayer);
                    }

                    currentGroupLayer = layer;
                }
                else if (layer.Rect.width != 0 && layer.Rect.height != 0)
                {
                    // It must be a text layer or image layer
                    if (currentGroupLayer != null)
                    {
                        currentGroupLayer.Children.Add(layer);
                    }
                    else
                    {
                        tree.Add(layer);
                    }
                }
            }

            // if there are any dangling layers, add them to the tree
            if (tree.Count == 0 && currentGroupLayer != null && currentGroupLayer.Children.Count > 0)
            {
                tree.Add(currentGroupLayer);
            }

            return tree;
        }

        /// <summary>
        /// Fixes any layer names that would cause problems.
        /// </summary>
        /// <param name="name">The name of the layer</param>
        /// <returns>The fixed layer name</returns>
        private static string MakeNameSafe(string name)
        {
            string newName = MakeNameSafeSilently(name);

            if (name != newName)
            {
                Debug.Log(string.Format("Layer name \"{0}\" was changed to \"{1}\"", name, newName));
            }

            return newName;
        }

        /// <summary>
        /// Returns true if the given <see cref="Layer"/> is marking the start of a layer group.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to check if it's the start of a group</param>
        /// <returns>True if the layer starts a group, otherwise false.</returns>
        private static bool IsStartGroup(Layer layer)
        {
            return layer.IsPixelDataIrrelevant;
        }

        /// <summary>
        /// Returns true if the given <see cref="Layer"/> is marking the end of a layer group.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to check if it's the end of a group.</param>
        /// <returns>True if the layer ends a group, otherwise false.</returns>
        private static bool IsEndGroup(Layer layer)
        {
            return layer.Name.Contains("</Layer set>") ||
                layer.Name.Contains("</Layer group>") ||
                (layer.Name == " copy" && layer.Rect.height == 0);
        }

        /// <summary>
        /// Gets full path to the current Unity project. In the form "C:/Project/".
        /// </summary>
        /// <returns>The full path to the current Unity project.</returns>
        private static string GetFullProjectPath()
        {
            string projectDirectory = Application.dataPath;

            // remove the Assets folder from the end since each imported asset has it already in its local path
            if (projectDirectory.EndsWith("Assets"))
            {
                projectDirectory = projectDirectory.Remove(projectDirectory.Length - "Assets".Length);
            }

            return projectDirectory;
        }

        /// <summary>
        /// Gets the relative path of a full path to an asset.
        /// </summary>
        /// <param name="fullPath">The full path to the asset.</param>
        /// <returns>The relative path to the asset.</returns>
        private static string GetRelativePath(string fullPath)
        {
            return fullPath.Replace(GetFullProjectPath(), string.Empty).Replace('\\', '/');
        }

        #region Layer Exporting Methods

        /// <summary>
        /// Processes and saves the layer tree.
        /// </summary>
        /// <param name="tree">The layer tree to export.</param>
        private static void ExportTree(List<Layer> tree)
        {
            // we must go through the tree in reverse order since Unity draws from back to front, but PSDs are stored front to back
            for (int i = tree.Count - 1; i >= 0; i--)
            {
                ExportLayer(tree[i]);
            }
        }

        /// <summary>
        /// Exports a single layer from the tree.
        /// </summary>
        /// <param name="layer">The layer to export.</param>
        private static void ExportLayer(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return;
            }

            if (info.IsFolderLike)
            {
                ExportFolderLayer(layer);
            }
            else
            {
                ExportArtLayer(layer);
            }
        }

        /// <summary>
        /// Exports a <see cref="Layer"/> that is a folder containing child layers.
        /// </summary>
        /// <param name="layer">The layer that is a folder.</param>
        private static void ExportFolderLayer(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return;
            }

            if (info.IsButtonGroup)
            {
                bool createRuntimeButton =
                    (LayoutInScene || CreatePrefab) &&
                    UseUnityUI &&
                    info.EffectiveVisible &&
                    HasVisibleButtonRuntimeContent(layer);

                if (createRuntimeButton)
                {
                    CreateUIButton(layer);
                }

                foreach (Layer child in layer.Children)
                {
                    LayerImportInfo childInfo = GetLayerInfo(child);
                    if (!ShouldButtonGroupChildEmitTexture(childInfo))
                    {
                        continue;
                    }

                    if (createRuntimeButton && IsButtonChildHandledByRuntime(childInfo))
                    {
                        continue;
                    }

                    ExportLayerTexturesOnly(child);
                }

                return;
            }

            if (info.IsAnimationGroup)
            {
                string oldPath = currentPath;
                GameObject oldGroupObject = currentGroupGameObject;
                List<Layer> visibleFrames = GetVisibleAnimationFrameLayers(layer);
                bool createRuntimeAnimation =
                    (LayoutInScene || CreatePrefab) &&
                    !UseUnityUI &&
                    info.EffectiveVisible &&
                    visibleFrames.Count > 0;

                currentPath = Path.Combine(currentPath, GetOutputFolderName(layer));
                Directory.CreateDirectory(currentPath);

                if (createRuntimeAnimation)
                {
                    CreateAnimation(layer);
                }

                HashSet<Layer> runtimeFrames = new HashSet<Layer>(visibleFrames);
                foreach (Layer child in layer.Children)
                {
                    if (createRuntimeAnimation && runtimeFrames.Contains(child))
                    {
                        continue;
                    }

                    ExportLayerTexturesOnly(child);
                }

                currentPath = oldPath;
                currentGroupGameObject = oldGroupObject;
                return;
            }

            // it is a "normal" folder layer that contains children layers
            string oldDirectory = currentPath;
            GameObject oldGroup = currentGroupGameObject;
            UiLayoutContext oldLayoutContext = currentGroupLayoutContext;

            currentPath = Path.Combine(currentPath, GetOutputFolderName(layer));
            Directory.CreateDirectory(currentPath);

            bool createGroupObject =
                (LayoutInScene || CreatePrefab) &&
                info.EffectiveVisible &&
                HasVisibleRuntimeContent(layer);

            if (createGroupObject)
            {
                if (UseUnityUI)
                {
                    currentGroupGameObject = new GameObject(GetRuntimeObjectName(layer), typeof(RectTransform));
                    RectTransform groupTransform = currentGroupGameObject.GetComponent<RectTransform>();
                    if (oldGroup != null)
                    {
                        groupTransform.SetParent(oldGroup.transform, false);
                    }

                    currentGroupLayoutContext = ApplyLayerUILayout(groupTransform, layer, info.AnchorPreset);
                }
                else
                {
                    currentGroupGameObject = new GameObject(GetRuntimeObjectName(layer));
                    if (oldGroup != null)
                    {
                        currentGroupGameObject.transform.parent = oldGroup.transform;
                    }
                }
            }

            ExportTree(layer.Children);

            currentPath = oldDirectory;
            currentGroupGameObject = oldGroup;
            currentGroupLayoutContext = oldLayoutContext;
        }

        /// <summary>
        /// Checks if the string contains the given string, while ignoring any casing.
        /// </summary>
        /// <param name="source">The source string to check.</param>
        /// <param name="toCheck">The string to search for in the source string.</param>
        /// <returns>True if the string contains the search string, otherwise false.</returns>
        private static bool ContainsIgnoreCase(this string source, string toCheck)
        {
            return source.IndexOf(toCheck, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Replaces any instance of the given string in this string with the given string.
        /// </summary>
        /// <param name="str">The string to replace sections in.</param>
        /// <param name="oldValue">The string to search for.</param>
        /// <param name="newValue">The string to replace the search string with.</param>
        /// <returns>The replaced string.</returns>
        private static string ReplaceIgnoreCase(this string str, string oldValue, string newValue)
        {
            StringBuilder sb = new StringBuilder();

            int previousIndex = 0;
            int index = str.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            while (index != -1)
            {
                sb.Append(str.Substring(previousIndex, index - previousIndex));
                sb.Append(newValue);
                index += oldValue.Length;

                previousIndex = index;
                index = str.IndexOf(oldValue, index, StringComparison.OrdinalIgnoreCase);
            }

            sb.Append(str.Substring(previousIndex));

            return sb.ToString();
        }

        /// <summary>
        /// Exports an art layer as an image file and sprite.  It can also generate text meshes from text layers.
        /// </summary>
        /// <param name="layer">The art layer to export.</param>
        private static void ExportArtLayer(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return;
            }

            bool createRuntimeObject = (LayoutInScene || CreatePrefab) && info.EffectiveVisible;
            bool exportTextureOnly = ShouldLayerEmitTextureFile(info);

            if (!layer.IsTextLayer)
            {
                if (createRuntimeObject)
                {
                    // create a sprite from the layer to lay it out in the scene
                    if (!UseUnityUI)
                    {
                        CreateSpriteGameObject(layer);
                    }
                    else
                    {
                        CreateUIImage(layer);
                    }
                }
                else if (exportTextureOnly)
                {
                    CreateTextureAssetWithoutGameObject(layer);
                }
            }
            else
            {
                // it is a text layer
                if (createRuntimeObject)
                {
                    // create text mesh
                    if (!UseUnityUI)
                    {
                        CreateTextGameObject(layer);
                    }
                    else
                    {
                        CreateUIText(layer);
                    }
                }
                else if (exportTextureOnly)
                {
                    CreateTextureAssetWithoutGameObject(layer);
                }
            }
        }

        /// <summary>
        /// Exports only generated assets for a layer subtree without creating runtime objects.
        /// </summary>
        /// <param name="layer">Layer to export.</param>
        private static void ExportLayerTexturesOnly(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return;
            }

            if (info.IsButtonGroup)
            {
                foreach (Layer child in layer.Children)
                {
                    if (ShouldButtonGroupChildEmitTexture(GetLayerInfo(child)))
                    {
                        ExportLayerTexturesOnly(child);
                    }
                }

                return;
            }

            if (DoesLayerCreateOutputDirectory(info))
            {
                string oldPath = currentPath;
                currentPath = Path.Combine(currentPath, GetOutputFolderName(layer));
                Directory.CreateDirectory(currentPath);

                foreach (Layer child in layer.Children)
                {
                    ExportLayerTexturesOnly(child);
                }

                currentPath = oldPath;
                return;
            }

            if (ShouldLayerEmitTextureFile(info) || ShouldButtonGroupChildEmitTexture(info))
            {
                CreateTextureAssetWithoutGameObject(layer);
            }
        }

        /// <summary>
        /// Saves the given <see cref="Layer"/> as a PNG on the hard drive.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to save as a PNG.</param>
        /// <returns>The filepath to the created PNG file.</returns>
        private static string CreatePNG(Layer layer, bool allowTextLayer = false)
        {
            string file = string.Empty;

            if (layer.Children.Count == 0 && layer.Rect.width > 0 && layer.Rect.height > 0 && (!layer.IsTextLayer || allowTextLayer))
            {
                file = Path.Combine(currentPath, GetTextureBaseName(layer) + ".png");
                if (!ShouldOverwriteExistingGeneratedFile(file))
                {
                    return file;
                }

                // decode the layer into a texture
                Texture2D texture = ImageDecoder.DecodeImage(layer);

                File.WriteAllBytes(file, texture.EncodeToPNG());
            }

            return file;
        }

        /// <summary>
        /// Exports a texture asset without creating a scene or prefab object.
        /// </summary>
        /// <param name="layer">Layer to export.</param>
        private static void CreateTextureAssetWithoutGameObject(Layer layer)
        {
            string file = CreatePNG(layer, true);
            if (!string.IsNullOrEmpty(file) && (LayoutInScene || CreatePrefab))
            {
                ImportSprite(GetRelativePath(file), PsdName);
            }
        }

        /// <summary>
        /// Creates a <see cref="Sprite"/> from the given <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to use to create a <see cref="Sprite"/>.</param>
        /// <returns>The created <see cref="Sprite"/> object.</returns>
        private static Sprite CreateSprite(Layer layer)
        {
            return CreateSprite(layer, PsdName);
        }

        /// <summary>
        /// Creates a <see cref="Sprite"/> from the given <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to use to create a <see cref="Sprite"/>.</param>
        /// <param name="packingTag">The tag used for Unity's atlas packer.</param>
        /// <returns>The created <see cref="Sprite"/> object.</returns>
        private static Sprite CreateSprite(Layer layer, string packingTag)
        {
            Sprite sprite = null;

            if (layer.Children.Count == 0 && layer.Rect.width > 0)
            {
                string file = CreatePNG(layer);
                if (!string.IsNullOrEmpty(file))
                {
                    sprite = ImportSprite(GetRelativePath(file), packingTag);
                }
            }

            return sprite;
        }

        /// <summary>
        /// Imports the <see cref="Sprite"/> at the given path, relative to the Unity project. For example "Assets/Textures/texture.png".
        /// </summary>
        /// <param name="relativePathToSprite">The path to the sprite, relative to the Unity project "Assets/Textures/texture.png".</param>
        /// <param name="packingTag">The tag to use for Unity's atlas packing.</param>
        /// <returns>The imported image as a <see cref="Sprite"/> object.</returns>
        private static Sprite ImportSprite(string relativePathToSprite, string packingTag)
        {
            _ = packingTag;
            relativePathToSprite = relativePathToSprite.Replace('\\', '/');
            AssetDatabase.ImportAsset(relativePathToSprite, ImportAssetOptions.ForceUpdate);

            // change the importer to make the texture a sprite
            TextureImporter textureImporter = AssetImporter.GetAtPath(relativePathToSprite) as TextureImporter;
            if (textureImporter != null)
            {
                textureImporter.textureType = TextureImporterType.Sprite;
                textureImporter.mipmapEnabled = false;
                textureImporter.spriteImportMode = SpriteImportMode.Single;
                textureImporter.spritePivot = new Vector2(0.5f, 0.5f);
                textureImporter.maxTextureSize = 2048;
                textureImporter.spritePixelsPerUnit = PixelsToUnits;
            }

            AssetDatabase.ImportAsset(relativePathToSprite, ImportAssetOptions.ForceUpdate);

            Sprite sprite = (Sprite)AssetDatabase.LoadAssetAtPath(relativePathToSprite, typeof(Sprite));
            return sprite;
        }

        /// <summary>
        /// Resolves a font for text layers, preferring the PSD font and falling back to common CJK fonts.
        /// </summary>
        /// <param name="layer">The text layer.</param>
        /// <returns>A usable Unity font.</returns>
        private static Font GetFontForLayer(Layer layer)
        {
            List<string> fontCandidates = new List<string>();
            if (!string.IsNullOrEmpty(layer.FontName))
            {
                fontCandidates.Add(layer.FontName.Trim());
            }

            fontCandidates.Add("Microsoft YaHei");
            fontCandidates.Add("SimHei");
            fontCandidates.Add("SimSun");
            fontCandidates.Add("PingFang SC");
            fontCandidates.Add("Heiti SC");
            fontCandidates.Add("Noto Sans CJK SC");
            fontCandidates.Add("Arial Unicode MS");
            fontCandidates.Add("Arial");

            foreach (string fontName in fontCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(fontName))
                {
                    continue;
                }

                try
                {
                    Font font = Font.CreateDynamicFontFromOSFont(fontName, 16);
                    if (font != null)
                    {
                        return font;
                    }
                }
                catch
                {
                    // Ignore unavailable fonts and try the next candidate.
                }
            }

            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        /// <summary>
        /// Creates a <see cref="GameObject"/> with a <see cref="TextMesh"/> from the given <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to create a <see cref="TextMesh"/> from.</param>
        private static void CreateTextGameObject(Layer layer)
        {
            Color color = ApplyLayerOpacity(layer.FillColor, layer);

            float x = layer.Rect.x / PixelsToUnits;
            float y = layer.Rect.y / PixelsToUnits;
            y = (CanvasSize.y / PixelsToUnits) - y;
            float width = layer.Rect.width / PixelsToUnits;
            float height = layer.Rect.height / PixelsToUnits;

            GameObject gameObject = new GameObject(GetRuntimeObjectName(layer));
            gameObject.transform.position = new Vector3(x + (width / 2), y - (height / 2), currentDepth);
            gameObject.transform.parent = currentGroupGameObject.transform;

            currentDepth -= depthStep;

            Font font = GetFontForLayer(layer);

            MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.material = font.material;
            meshRenderer.sortingOrder = currentSortingOrder++;

            TextMesh textMesh = gameObject.AddComponent<TextMesh>();
            textMesh.text = layer.Text;
            textMesh.font = font;
            textMesh.fontSize = 0;
            textMesh.characterSize = layer.FontSize / PixelsToUnits;
            textMesh.color = color;
            textMesh.anchor = TextAnchor.MiddleCenter;

            switch (layer.Justification)
            {
                case TextJustification.Left:
                    textMesh.alignment = TextAlignment.Left;
                    break;
                case TextJustification.Right:
                    textMesh.alignment = TextAlignment.Right;
                    break;
                case TextJustification.Center:
                    textMesh.alignment = TextAlignment.Center;
                    break;
            }
        }

        /// <summary>
        /// Creates a <see cref="GameObject"/> with a sprite from the given <see cref="Layer"/>
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to create the sprite from.</param>
        /// <returns>The <see cref="SpriteRenderer"/> component attached to the new sprite <see cref="GameObject"/>.</returns>
        private static SpriteRenderer CreateSpriteGameObject(Layer layer)
        {
            float x = layer.Rect.x / PixelsToUnits;
            float y = layer.Rect.y / PixelsToUnits;
            y = (CanvasSize.y / PixelsToUnits) - y;
            float width = layer.Rect.width / PixelsToUnits;
            float height = layer.Rect.height / PixelsToUnits;

            GameObject gameObject = new GameObject(GetRuntimeObjectName(layer));
            gameObject.transform.position = new Vector3(x + (width / 2), y - (height / 2), currentDepth);
            gameObject.transform.parent = currentGroupGameObject.transform;

            currentDepth -= depthStep;

            SpriteRenderer spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = CreateSprite(layer);
            spriteRenderer.sortingOrder = currentSortingOrder++;
            return spriteRenderer;
        }

        /// <summary>
        /// Creates a Unity sprite animation from the given <see cref="Layer"/> that is a group layer.  It grabs all of the children art
        /// layers and uses them as the frames of the animation.
        /// </summary>
        /// <param name="layer">The group <see cref="Layer"/> to use to create the sprite animation.</param>
        private static void CreateAnimation(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info == null)
            {
                return;
            }

            List<Sprite> frames = new List<Sprite>();
            List<Layer> visibleFrames = GetVisibleAnimationFrameLayers(layer);
            if (visibleFrames.Count == 0)
            {
                return;
            }

            string animationAssetName = GetOutputFolderName(layer);
            float fps = info.AnimationFps;

            Layer firstChild = visibleFrames[0];
            SpriteRenderer spriteRenderer = CreateSpriteGameObject(firstChild);
            spriteRenderer.name = GetRuntimeObjectName(layer);

            foreach (Layer child in visibleFrames)
            {
                Sprite frame = CreateSprite(child, animationAssetName);
                if (frame != null)
                {
                    frames.Add(frame);
                }
            }

            if (frames.Count == 0)
            {
                UnityEngine.Object.DestroyImmediate(spriteRenderer.gameObject);
                return;
            }

            spriteRenderer.sprite = frames[0];

#if UNITY_5_3_OR_NEWER
            // Create Animator Controller with an Animation Clip
            UnityEditor.Animations.AnimatorController controller = new UnityEditor.Animations.AnimatorController();
            controller.AddLayer("Base Layer");

            UnityEditor.Animations.AnimatorControllerLayer controllerLayer = controller.layers[0];
            UnityEditor.Animations.AnimatorState state = controllerLayer.stateMachine.AddState(animationAssetName);
            state.motion = CreateSpriteAnimationClip(animationAssetName, frames, fps);

            string controllerPath = GetRelativePath(currentPath) + "/" + animationAssetName + ".controller";
            RuntimeAnimatorController runtimeController = controller;
            if (PrepareAssetPathForCreate(controllerPath))
            {
                AssetDatabase.CreateAsset(controller, controllerPath);
            }
            else
            {
                RuntimeAnimatorController existingController =
                    AssetDatabase.LoadAssetAtPath(controllerPath, typeof(RuntimeAnimatorController)) as RuntimeAnimatorController;
                if (existingController != null)
                {
                    runtimeController = existingController;
                }
            }
#else // Unity 4
            // Create Animator Controller with an Animation Clip
            UnityEditor.Animations.AnimatorController controller = new UnityEditor.Animations.AnimatorController();
            UnityEditor.Animations.AnimatorControllerLayer controllerLayer = controller.AddLayer("Base Layer");

            UnityEditor.Animations.AnimatorState state = controllerLayer.stateMachine.AddState(animationAssetName);
            state.SetAnimationClip(CreateSpriteAnimationClip(animationAssetName, frames, fps));

            string controllerPath = GetRelativePath(currentPath) + "/" + animationAssetName + ".controller";
            RuntimeAnimatorController runtimeController = controller;
            if (PrepareAssetPathForCreate(controllerPath))
            {
                AssetDatabase.CreateAsset(controller, controllerPath);
            }
            else
            {
                RuntimeAnimatorController existingController =
                    AssetDatabase.LoadAssetAtPath(controllerPath, typeof(RuntimeAnimatorController)) as RuntimeAnimatorController;
                if (existingController != null)
                {
                    runtimeController = existingController;
                }
            }
#endif

            // Add an Animator and assign it the controller
            Animator animator = spriteRenderer.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = runtimeController;
        }

        /// <summary>
        /// Creates an <see cref="AnimationClip"/> of a sprite animation using the given <see cref="Sprite"/> frames and frames per second.
        /// </summary>
        /// <param name="name">The name of the animation to create.</param>
        /// <param name="sprites">The list of <see cref="Sprite"/> objects making up the frames of the animation.</param>
        /// <param name="fps">The frames per second for the animation.</param>
        /// <returns>The newly constructed <see cref="AnimationClip"/></returns>
        private static AnimationClip CreateSpriteAnimationClip(string name, IList<Sprite> sprites, float fps)
        {
            float frameLength = 1f / fps;

            AnimationClip clip = new AnimationClip();
            clip.name = name;
            clip.frameRate = fps;
            clip.wrapMode = WrapMode.Loop;

            // The AnimationClipSettings cannot be set in Unity (as of 4.6) and must be editted via SerializedProperty
            // from: http://forum.unity3d.com/threads/can-mecanim-animation-clip-properties-be-edited-in-script.251772/
            SerializedObject serializedClip = new SerializedObject(clip);
            SerializedProperty serializedSettings = serializedClip.FindProperty("m_AnimationClipSettings");
            serializedSettings.FindPropertyRelative("m_LoopTime").boolValue = true;
            serializedClip.ApplyModifiedProperties();

            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.type = typeof(SpriteRenderer);
            curveBinding.propertyName = "m_Sprite";

            ObjectReferenceKeyframe[] keyFrames = new ObjectReferenceKeyframe[sprites.Count];

            for (int i = 0; i < sprites.Count; i++)
            {
                ObjectReferenceKeyframe kf = new ObjectReferenceKeyframe();
                kf.time = i * frameLength;
                kf.value = sprites[i];
                keyFrames[i] = kf;
            }

#if UNITY_5_3_OR_NEWER
            AnimationUtility.SetObjectReferenceCurve(clip, curveBinding, keyFrames);
#else // Unity 4
            AnimationUtility.SetAnimationType(clip, ModelImporterAnimationType.Generic);
            AnimationUtility.SetObjectReferenceCurve(clip, curveBinding, keyFrames);

            clip.ValidateIfRetargetable(true);
#endif

            string clipPath = GetRelativePath(currentPath) + "/" + name + ".anim";
            if (PrepareAssetPathForCreate(clipPath))
            {
                AssetDatabase.CreateAsset(clip, clipPath);
                return clip;
            }

            AnimationClip existingClip = AssetDatabase.LoadAssetAtPath(clipPath, typeof(AnimationClip)) as AnimationClip;
            if (existingClip != null)
            {
                return existingClip;
            }

            return clip;
        }

        #endregion

        #region Unity UI
        /// <summary>
        /// Creates the Unity UI event system game object that handles all input.
        /// </summary>
        private static void CreateUIEventSystem()
        {
            if (!GameObject.Find("EventSystem"))
            {
                GameObject gameObject = new GameObject("EventSystem");
                gameObject.AddComponent<EventSystem>();
                gameObject.AddComponent<StandaloneInputModule>();
            }
        }

        /// <summary>
        /// Creates a Unity UI <see cref="Canvas"/>.
        /// </summary>
        private static void CreateUICanvas()
        {
            Canvas = new GameObject(PsdName);

            Canvas canvas = Canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            RectTransform transform = Canvas.GetComponent<RectTransform>();
            Vector2 scaledCanvasSize = new Vector2(CanvasSize.x / PixelsToUnits, CanvasSize.y / PixelsToUnits);
            transform.sizeDelta = scaledCanvasSize;

            CanvasScaler scaler = Canvas.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = PixelsToUnits;
            scaler.referencePixelsPerUnit = PixelsToUnits;

            Canvas.AddComponent<GraphicRaycaster>();
        }

        /// <summary>
        /// Creates a Unity UI <see cref="UnityEngine.UI.Image"/> <see cref="GameObject"/> with a <see cref="Sprite"/> from a PSD <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to use to create the UI Image.</param>
        /// <returns>The newly constructed Image object.</returns>
        private static Image CreateUIImage(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            AnchorNamePreset preset = info != null ? info.AnchorPreset : AnchorNamePreset.None;

            GameObject uiObject = new GameObject(GetRuntimeObjectName(layer), typeof(RectTransform));
            uiObject.transform.SetParent(currentGroupGameObject.transform, false);

            RectTransform uiTransform = uiObject.GetComponent<RectTransform>();
            ApplyLayerUILayout(uiTransform, layer, preset);

            Image uiImage = uiObject.AddComponent<Image>();
            uiImage.sprite = CreateSprite(layer);
            ApplyImageLayoutBehavior(uiImage, preset);
            return uiImage;
        }

        /// <summary>
        /// Creates a Unity UI <see cref="UnityEngine.UI.Text"/> <see cref="GameObject"/> with the text from a PSD <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> used to create the <see cref="UnityEngine.UI.Text"/> from.</param>
        private static void CreateUIText(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            AnchorNamePreset preset = info != null ? info.AnchorPreset : AnchorNamePreset.None;

            Color color = ApplyLayerOpacity(layer.FillColor, layer);

            GameObject uiObject = new GameObject(GetRuntimeObjectName(layer), typeof(RectTransform));
            uiObject.transform.SetParent(currentGroupGameObject.transform, false);

            RectTransform uiTransform = uiObject.GetComponent<RectTransform>();
            ApplyLayerUILayout(uiTransform, layer, preset);

            Font font = GetFontForLayer(layer);

            Text textUI = uiObject.AddComponent<Text>();
            textUI.text = layer.Text;
            textUI.font = font;

            float fontSize = GetUIFontSize(layer);
            float ceiling = Mathf.Ceil(fontSize);
            if (fontSize > 0f && fontSize < ceiling)
            {
                textUI.fontSize = (int)ceiling;
                if (!IsGlobalAnchorPreset(preset))
                {
                    float scaleFactor = ceiling / fontSize;
                    textUI.rectTransform.sizeDelta *= scaleFactor;
                    textUI.rectTransform.localScale /= scaleFactor;
                }
            }
            else
            {
                textUI.fontSize = Mathf.Max(1, (int)ceiling);
            }

            textUI.color = color;
            textUI.alignment = TextAnchor.MiddleCenter;

            switch (layer.Justification)
            {
                case TextJustification.Left:
                    textUI.alignment = TextAnchor.MiddleLeft;
                    break;
                case TextJustification.Right:
                    textUI.alignment = TextAnchor.MiddleRight;
                    break;
                case TextJustification.Center:
                    textUI.alignment = TextAnchor.MiddleCenter;
                    break;
            }
        }

        /// <summary>
        /// Creates a <see cref="UnityEngine.UI.Button"/> from the given <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The Layer to create the Button from.</param>
        private static void CreateUIButton(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            AnchorNamePreset buttonPreset = info != null ? info.AnchorPreset : AnchorNamePreset.None;

            // create an empty Image object with a Button behavior attached
            Image image = CreateUIImage(layer);
            Button button = image.gameObject.AddComponent<Button>();
            UiLayoutContext buttonLayoutContext = GetChildUILayoutContext(layer, buttonPreset, GetLayerLayoutRect(layer));

            // look through the children for a clip rect
            ////Rectangle? clipRect = null;
            ////foreach (Layer child in layer.Children)
            ////{
            ////    if (child.Name.ContainsIgnoreCase("|ClipRect"))
            ////    {
            ////        clipRect = child.Rect;
            ////    }
            ////}

            // look through the children for the sprite states
            foreach (Layer child in layer.Children)
            {
                LayerImportInfo childInfo = GetLayerInfo(child);
                if (childInfo == null || !childInfo.EffectiveVisible)
                {
                    continue;
                }

                if (childInfo.ButtonRole == ButtonChildRole.Disabled)
                {
                    button.transition = Selectable.Transition.SpriteSwap;

                    SpriteState spriteState = button.spriteState;
                    spriteState.disabledSprite = CreateSprite(child);
                    button.spriteState = spriteState;
                }
                else if (childInfo.ButtonRole == ButtonChildRole.Highlighted)
                {
                    button.transition = Selectable.Transition.SpriteSwap;

                    SpriteState spriteState = button.spriteState;
                    spriteState.highlightedSprite = CreateSprite(child);
                    button.spriteState = spriteState;
                }
                else if (childInfo.ButtonRole == ButtonChildRole.Pressed)
                {
                    button.transition = Selectable.Transition.SpriteSwap;

                    SpriteState spriteState = button.spriteState;
                    spriteState.pressedSprite = CreateSprite(child);
                    button.spriteState = spriteState;
                }
                else if (childInfo.ButtonRole == ButtonChildRole.Default)
                {
                    image.sprite = CreateSprite(child);
                    ApplyImageLayoutBehavior(image, buttonPreset);
                    button.targetGraphic = image;
                }
                else if (childInfo.ButtonRole == ButtonChildRole.TextImage)
                {
                    GameObject oldGroupObject = currentGroupGameObject;
                    UiLayoutContext oldLayoutContext = currentGroupLayoutContext;
                    currentGroupGameObject = button.gameObject;
                    currentGroupLayoutContext = buttonLayoutContext;

                    // If the "text" is a normal art layer, create an Image object from the "text"
                    CreateUIImage(child);

                    currentGroupGameObject = oldGroupObject;
                    currentGroupLayoutContext = oldLayoutContext;
                }

                if (child.IsTextLayer)
                {
                    // TODO: Create a child text game object
                }
            }
        }

        /// <summary>
        /// Applies the configured layout to the generated PSD root object.
        /// </summary>
        /// <param name="rootTransform">The root RectTransform.</param>
        /// <returns>Resolved root layout context.</returns>
        private static UiLayoutContext ApplyRootUILayout(RectTransform rootTransform)
        {
            Vector2 rootRectSize = GetRootRectSize();
            if (RootUseGlobalAnchorByDefault)
            {
                ApplyStretchLayout(rootTransform);
            }
            else
            {
                rootTransform.anchorMin = new Vector2(0.5f, 0.5f);
                rootTransform.anchorMax = new Vector2(0.5f, 0.5f);
                rootTransform.pivot = new Vector2(0.5f, 0.5f);
                rootTransform.anchoredPosition = Vector2.zero;
                rootTransform.sizeDelta = rootRectSize;
            }

            return new UiLayoutContext
            {
                PsdReferenceRect = new Rect(0f, 0f, CanvasSize.x, CanvasSize.y),
                LocalRectSize = rootRectSize,
                LocalDisplayRect = GetCenteredRect(GetRootDisplaySize(rootRectSize))
            };
        }

        /// <summary>
        /// Applies the configured layout to one generated UI node.
        /// </summary>
        /// <param name="transform">The RectTransform to place.</param>
        /// <param name="layer">Source PSD layer.</param>
        /// <param name="preset">Resolved anchor preset.</param>
        /// <returns>Resolved child layout context.</returns>
        private static UiLayoutContext ApplyLayerUILayout(RectTransform transform, Layer layer, AnchorNamePreset preset)
        {
            Rect layoutRect = GetLayerLayoutRect(layer);
            AnchorNamePreset effectivePreset = NormalizePointAnchorPreset(preset);
            UiLayoutContext childContext = GetChildUILayoutContext(layer, preset, layoutRect);

            if (IsGlobalAnchorPreset(preset))
            {
                ApplyStretchLayout(transform);
                return childContext;
            }

            Vector2 anchor = GetAnchorVector(effectivePreset);
            transform.anchorMin = anchor;
            transform.anchorMax = anchor;
            transform.pivot = anchor;
            transform.anchoredPosition = GetAnchoredPositionForLayer(layoutRect, currentGroupLayoutContext, effectivePreset);
            transform.sizeDelta = childContext.LocalRectSize;
            return childContext;
        }

        /// <summary>
        /// Applies stretch anchors with zero offsets to a RectTransform.
        /// </summary>
        /// <param name="transform">Target RectTransform.</param>
        private static void ApplyStretchLayout(RectTransform transform)
        {
            transform.anchorMin = Vector2.zero;
            transform.anchorMax = Vector2.one;
            transform.pivot = new Vector2(0.5f, 0.5f);
            transform.anchoredPosition = Vector2.zero;
            transform.sizeDelta = Vector2.zero;
            transform.offsetMin = Vector2.zero;
            transform.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Gets the child UI layout context produced by one layer.
        /// </summary>
        /// <param name="layer">Source PSD layer.</param>
        /// <param name="preset">Resolved anchor preset.</param>
        /// <returns>Layout context for the layer's children.</returns>
        private static UiLayoutContext GetChildUILayoutContext(Layer layer, AnchorNamePreset preset, Rect layoutRect)
        {
            if (IsGlobalAnchorPreset(preset))
            {
                return currentGroupLayoutContext;
            }

            Vector2 childSize = GetUiLayerSize(layoutRect);
            return new UiLayoutContext
            {
                PsdReferenceRect = layoutRect,
                LocalRectSize = childSize,
                LocalDisplayRect = GetCenteredRect(childSize)
            };
        }

        /// <summary>
        /// Applies the default image preserve-aspect behavior for generated UI images.
        /// </summary>
        /// <param name="image">The generated image.</param>
        /// <param name="preset">Resolved anchor preset.</param>
        private static void ApplyImageLayoutBehavior(Image image, AnchorNamePreset preset)
        {
            if (image == null)
            {
                return;
            }

            image.preserveAspect = true;

            AspectRatioFitter fitter = image.GetComponent<AspectRatioFitter>();
            if (!IsGlobalAnchorPreset(preset) || image.sprite == null || image.sprite.rect.height <= 0f)
            {
                if (fitter != null)
                {
                    UnityEngine.Object.DestroyImmediate(fitter);
                }

                return;
            }

            if (fitter == null)
            {
                fitter = image.gameObject.AddComponent<AspectRatioFitter>();
            }

            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = image.sprite.rect.width / image.sprite.rect.height;
        }

        /// <summary>
        /// Gets scaled layer size for UI layout.
        /// </summary>
        /// <param name="rect">The PSD layer rectangle.</param>
        /// <returns>Scaled width and height.</returns>
        private static Vector2 GetUiLayerSize(Rect rect)
        {
            if (UseTargetCanvasCoordinates)
            {
                return new Vector2(rect.width * GetTargetCanvasScaleX(), rect.height * GetTargetCanvasScaleY());
            }

            return new Vector2(rect.width / PixelsToUnits, rect.height / PixelsToUnits);
        }

        /// <summary>
        /// Gets scale ratio for X axis when mapping PSD pixels to target canvas.
        /// </summary>
        /// <returns>Scale factor on X axis.</returns>
        private static float GetTargetCanvasScaleX()
        {
            if (!(UseTargetCanvasCoordinates && ScaleToTargetCanvas))
            {
                return 1f;
            }

            if (PreserveAspectWhenScalingToCanvas)
            {
                return GetTargetCanvasFitScale();
            }

            return CanvasSize.x > 0 ? TargetCanvasSize.x / CanvasSize.x : 1f;
        }

        /// <summary>
        /// Gets scale ratio for Y axis when mapping PSD pixels to target canvas.
        /// </summary>
        /// <returns>Scale factor on Y axis.</returns>
        private static float GetTargetCanvasScaleY()
        {
            if (!(UseTargetCanvasCoordinates && ScaleToTargetCanvas))
            {
                return 1f;
            }

            if (PreserveAspectWhenScalingToCanvas)
            {
                return GetTargetCanvasFitScale();
            }

            return CanvasSize.y > 0 ? TargetCanvasSize.y / CanvasSize.y : 1f;
        }

        /// <summary>
        /// Gets a uniform scale ratio for text/font scaling.
        /// </summary>
        /// <returns>Uniform scale ratio.</returns>
        private static float GetTargetCanvasUniformScale()
        {
            return Mathf.Min(GetTargetCanvasScaleX(), GetTargetCanvasScaleY());
        }

        /// <summary>
        /// Gets fit scale that preserves PSD aspect ratio inside the target canvas.
        /// </summary>
        /// <returns>Uniform fit scale.</returns>
        private static float GetTargetCanvasFitScale()
        {
            float scaleX = CanvasSize.x > 0 ? TargetCanvasSize.x / CanvasSize.x : 1f;
            float scaleY = CanvasSize.y > 0 ? TargetCanvasSize.y / CanvasSize.y : 1f;
            return Mathf.Min(scaleX, scaleY);
        }

        /// <summary>
        /// Gets root rect size for the generated PSD root under target canvas.
        /// </summary>
        /// <returns>Root rect size.</returns>
        private static Vector2 GetScaledRootSize()
        {
            if (!(UseTargetCanvasCoordinates && ScaleToTargetCanvas))
            {
                return CanvasSize;
            }

            if (!PreserveAspectWhenScalingToCanvas)
            {
                return TargetCanvasSize;
            }

            float fitScale = GetTargetCanvasFitScale();
            return CanvasSize * fitScale;
        }

        /// <summary>
        /// Gets the actual root RectTransform size.
        /// </summary>
        /// <returns>Root RectTransform size.</returns>
        private static Vector2 GetRootRectSize()
        {
            if (UseTargetCanvasCoordinates)
            {
                return RootUseGlobalAnchorByDefault ? TargetCanvasSize : GetScaledRootSize();
            }

            return new Vector2(CanvasSize.x / PixelsToUnits, CanvasSize.y / PixelsToUnits);
        }

        /// <summary>
        /// Gets the PSD content display size inside the current root RectTransform.
        /// </summary>
        /// <param name="rootRectSize">The actual root RectTransform size.</param>
        /// <returns>PSD content display size.</returns>
        private static Vector2 GetRootDisplaySize(Vector2 rootRectSize)
        {
            if (!UseTargetCanvasCoordinates)
            {
                return rootRectSize;
            }

            return GetScaledRootSize();
        }

        /// <summary>
        /// Gets the UI font size used by generated Unity UI text.
        /// </summary>
        /// <param name="layer">Source PSD text layer.</param>
        /// <returns>Scaled UI font size.</returns>
        private static float GetUIFontSize(Layer layer)
        {
            if (UseTargetCanvasCoordinates)
            {
                return layer.FontSize * GetTargetCanvasUniformScale();
            }

            return layer.FontSize / PixelsToUnits;
        }

        /// <summary>
        /// Gets the effective layout rect for a layer, falling back to the raw PSD rect when needed.
        /// </summary>
        /// <param name="layer">Source PSD layer.</param>
        /// <returns>Resolved layout rect.</returns>
        private static Rect GetLayerLayoutRect(Layer layer)
        {
            LayerImportInfo info = GetLayerInfo(layer);
            if (info != null && info.HasLayoutRect)
            {
                return info.LayoutRect;
            }

            return layer != null ? layer.Rect : default(Rect);
        }

        /// <summary>
        /// Applies Photoshop layer opacity to a Unity color.
        /// </summary>
        /// <param name="color">Base color.</param>
        /// <param name="layer">Source PSD layer.</param>
        /// <returns>Color with layer opacity applied on alpha.</returns>
        private static Color ApplyLayerOpacity(Color color, Layer layer)
        {
            float layerOpacity = layer != null ? layer.Opacity / (float)byte.MaxValue : 1f;
            color.a = Mathf.Clamp01(color.a) * layerOpacity;
            return color;
        }

        /// <summary>
        /// Converts a PSD layer rect to a local anchored position relative to the current parent layout context.
        /// </summary>
        /// <param name="rect">The PSD layer rectangle.</param>
        /// <param name="parentContext">The current parent layout context.</param>
        /// <param name="preset">The anchor preset used by the child.</param>
        /// <returns>Local anchored position for the generated UI element.</returns>
        private static Vector2 GetAnchoredPositionForLayer(Rect rect, UiLayoutContext parentContext, AnchorNamePreset preset)
        {
            Vector2 localPoint = MapPsdPointToLocalSpace(GetPsdPresetPoint(rect, preset), parentContext);
            Vector2 anchorPoint = GetLocalPresetPoint(parentContext.LocalRectSize, preset);
            return localPoint - anchorPoint;
        }

        /// <summary>
        /// Maps a PSD point into the local coordinate space of the current parent RectTransform.
        /// </summary>
        /// <param name="psdPoint">PSD-space point.</param>
        /// <param name="context">Current UI layout context.</param>
        /// <returns>Local point in parent center-space coordinates.</returns>
        private static Vector2 MapPsdPointToLocalSpace(Vector2 psdPoint, UiLayoutContext context)
        {
            if (context.PsdReferenceRect.width <= 0f || context.PsdReferenceRect.height <= 0f)
            {
                return Vector2.zero;
            }

            float normalizedX = (psdPoint.x - context.PsdReferenceRect.xMin) / context.PsdReferenceRect.width;
            float normalizedY = (psdPoint.y - context.PsdReferenceRect.yMin) / context.PsdReferenceRect.height;

            float x = context.LocalDisplayRect.xMin + (normalizedX * context.LocalDisplayRect.width);
            float y = context.LocalDisplayRect.yMax - (normalizedY * context.LocalDisplayRect.height);
            return new Vector2(x, y);
        }

        /// <summary>
        /// Gets the PSD-space point for one anchor preset.
        /// </summary>
        /// <param name="rect">PSD-space rect.</param>
        /// <param name="preset">Anchor preset.</param>
        /// <returns>PSD-space anchor point.</returns>
        private static Vector2 GetPsdPresetPoint(Rect rect, AnchorNamePreset preset)
        {
            switch (NormalizePointAnchorPreset(preset))
            {
                case AnchorNamePreset.TopLeft:
                    return new Vector2(rect.xMin, rect.yMin);
                case AnchorNamePreset.BottomLeft:
                    return new Vector2(rect.xMin, rect.yMax);
                case AnchorNamePreset.TopRight:
                    return new Vector2(rect.xMax, rect.yMin);
                case AnchorNamePreset.BottomRight:
                    return new Vector2(rect.xMax, rect.yMax);
                case AnchorNamePreset.LeftMiddle:
                    return new Vector2(rect.xMin, rect.yMin + (rect.height * 0.5f));
                case AnchorNamePreset.RightMiddle:
                    return new Vector2(rect.xMax, rect.yMin + (rect.height * 0.5f));
                case AnchorNamePreset.TopMiddle:
                    return new Vector2(rect.xMin + (rect.width * 0.5f), rect.yMin);
                case AnchorNamePreset.BottomMiddle:
                    return new Vector2(rect.xMin + (rect.width * 0.5f), rect.yMax);
                case AnchorNamePreset.Center:
                default:
                    return rect.center;
            }
        }

        /// <summary>
        /// Gets the local-space anchor point for one anchor preset in a parent rect.
        /// </summary>
        /// <param name="size">Parent local rect size.</param>
        /// <param name="preset">Anchor preset.</param>
        /// <returns>Local-space anchor point.</returns>
        private static Vector2 GetLocalPresetPoint(Vector2 size, AnchorNamePreset preset)
        {
            Rect localRect = GetCenteredRect(size);
            switch (NormalizePointAnchorPreset(preset))
            {
                case AnchorNamePreset.TopLeft:
                    return new Vector2(localRect.xMin, localRect.yMax);
                case AnchorNamePreset.BottomLeft:
                    return new Vector2(localRect.xMin, localRect.yMin);
                case AnchorNamePreset.TopRight:
                    return new Vector2(localRect.xMax, localRect.yMax);
                case AnchorNamePreset.BottomRight:
                    return new Vector2(localRect.xMax, localRect.yMin);
                case AnchorNamePreset.LeftMiddle:
                    return new Vector2(localRect.xMin, 0f);
                case AnchorNamePreset.RightMiddle:
                    return new Vector2(localRect.xMax, 0f);
                case AnchorNamePreset.TopMiddle:
                    return new Vector2(0f, localRect.yMax);
                case AnchorNamePreset.BottomMiddle:
                    return new Vector2(0f, localRect.yMin);
                case AnchorNamePreset.Center:
                default:
                    return Vector2.zero;
            }
        }

        /// <summary>
        /// Gets the anchor vector used by RectTransform for a preset.
        /// </summary>
        /// <param name="preset">Anchor preset.</param>
        /// <returns>Unity anchor vector.</returns>
        private static Vector2 GetAnchorVector(AnchorNamePreset preset)
        {
            switch (NormalizePointAnchorPreset(preset))
            {
                case AnchorNamePreset.TopLeft:
                    return new Vector2(0f, 1f);
                case AnchorNamePreset.BottomLeft:
                    return new Vector2(0f, 0f);
                case AnchorNamePreset.TopRight:
                    return new Vector2(1f, 1f);
                case AnchorNamePreset.BottomRight:
                    return new Vector2(1f, 0f);
                case AnchorNamePreset.LeftMiddle:
                    return new Vector2(0f, 0.5f);
                case AnchorNamePreset.RightMiddle:
                    return new Vector2(1f, 0.5f);
                case AnchorNamePreset.TopMiddle:
                    return new Vector2(0.5f, 1f);
                case AnchorNamePreset.BottomMiddle:
                    return new Vector2(0.5f, 0f);
                case AnchorNamePreset.Center:
                default:
                    return new Vector2(0.5f, 0.5f);
            }
        }

        /// <summary>
        /// Normalizes a preset so regular point placement falls back to center.
        /// </summary>
        /// <param name="preset">Parsed preset.</param>
        /// <returns>Point-placement preset.</returns>
        private static AnchorNamePreset NormalizePointAnchorPreset(AnchorNamePreset preset)
        {
            return preset == AnchorNamePreset.None || preset == AnchorNamePreset.Global
                ? AnchorNamePreset.Center
                : preset;
        }

        /// <summary>
        /// Determines whether a preset represents global stretch anchoring.
        /// </summary>
        /// <param name="preset">Parsed preset.</param>
        /// <returns>True when the preset is global.</returns>
        private static bool IsGlobalAnchorPreset(AnchorNamePreset preset)
        {
            return preset == AnchorNamePreset.Global;
        }

        /// <summary>
        /// Creates a centered local rect for the given size.
        /// </summary>
        /// <param name="size">Rect size.</param>
        /// <returns>Centered rect.</returns>
        private static Rect GetCenteredRect(Vector2 size)
        {
            return new Rect(-size.x * 0.5f, -size.y * 0.5f, size.x, size.y);
        }
        #endregion
    }
}

