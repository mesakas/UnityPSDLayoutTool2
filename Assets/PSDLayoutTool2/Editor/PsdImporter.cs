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
        /// The <see cref="GameObject"/> representing the current group (folder) we are processing.
        /// </summary>
        private static GameObject currentGroupGameObject;

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
            ImportConflictAnalysis conflictAnalysis = AnalyzeImportConflicts(tree, outputRelativePath, outputFullPath, prefabRelativePath);

            ImportConflictSelection effectiveSelection = forcedSelection;
            if (!skipConflictPrompt && conflictAnalysis.HasExistingTargets)
            {
                bool updateExistingFiles = PromptForUpdatingExistingFiles(conflictAnalysis);
                if (!updateExistingFiles)
                {
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
                    DeleteSelectedFiles(effectiveSelection.PathsToDelete, outputFullPath);
                }

                if (LayoutInScene || CreatePrefab)
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
                            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                            rootRect.pivot = new Vector2(0.5f, 0.5f);
                            rootRect.anchoredPosition = Vector2.zero;
                            rootRect.sizeDelta = GetScaledRootSize();
                        }
                        else
                        {
                            CreateUICanvas();
                            rootPsdGameObject = Canvas;
                        }
                    }
                    else
                    {
                        rootPsdGameObject = new GameObject(PsdName);
                    }

                    currentGroupGameObject = rootPsdGameObject;
                }

                ExportTree(tree);

                if (CreatePrefab)
                {
                    if (ShouldSavePrefab(prefabRelativePath))
                    {
                        PrefabUtility.SaveAsPrefabAsset(rootPsdGameObject, prefabRelativePath);
                    }

                    if (!LayoutInScene)
                    {
                        // if we are not flagged to layout in the scene, delete the GameObject used to generate the prefab
                        UnityEngine.Object.DestroyImmediate(rootPsdGameObject);
                    }
                }

                AssetDatabase.Refresh();
            }
            finally
            {
                ClearCurrentImportSelection();
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
            string prefabRelativePath)
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

            HashSet<string> generatedTexturePaths = CollectExpectedTexturePaths(tree, outputFullPath);
            HashSet<string> existingTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (analysis.HasExistingOutputDirectory)
            {
                string[] existingFiles = Directory.GetFiles(outputFullPath, "*.png", SearchOption.AllDirectories);
                foreach (string existingFile in existingFiles)
                {
                    existingTexturePaths.Add(NormalizePath(existingFile));
                }
            }

            foreach (string existingFile in existingTexturePaths)
            {
                if (generatedTexturePaths.Contains(existingFile))
                {
                    analysis.SameNamePaths.Add(existingFile);
                }
                else
                {
                    analysis.DeletedPaths.Add(existingFile);
                }
            }

            if (analysis.HasExistingPrefab)
            {
                analysis.SameNamePaths.Add(analysis.PrefabFullPath);
            }

            analysis.SameNamePaths = analysis.SameNamePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => ToDisplayPath(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            analysis.DeletedPaths = analysis.DeletedPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => ToDisplayPath(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

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
                selection.PathsToUpdate.Add(NormalizePath(path));
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
                messageBuilder.AppendLine("纹理目录: " + analysis.OutputRelativePath);
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
        /// Deletes selected stale files and their meta files from the output directory.
        /// </summary>
        /// <param name="pathsToDelete">Files selected for deletion.</param>
        /// <param name="outputFullPath">Import output root path.</param>
        private static void DeleteSelectedFiles(HashSet<string> pathsToDelete, string outputFullPath)
        {
            if (pathsToDelete == null || pathsToDelete.Count == 0 || !Directory.Exists(outputFullPath))
            {
                return;
            }

            string normalizedRoot = NormalizePath(outputFullPath).TrimEnd('/');

            foreach (string selectedPath in pathsToDelete)
            {
                string normalizedPath = NormalizePath(selectedPath);
                if (!IsPathInsideDirectory(normalizedPath, normalizedRoot))
                {
                    continue;
                }

                DeleteFileWithMeta(normalizedPath);
            }

            DeleteEmptySubDirectories(outputFullPath);
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

            if (!File.Exists(prefabFullPath))
            {
                return true;
            }

            if (!useExplicitUpdateSelection)
            {
                return true;
            }

            return selectedUpdatePathsForCurrentImport != null &&
                   selectedUpdatePathsForCurrentImport.Contains(prefabFullPath);
        }

        /// <summary>
        /// Collects all texture files that would be generated by exporting the given layer tree.
        /// </summary>
        /// <param name="tree">Layer tree for the PSD.</param>
        /// <param name="outputFullPath">Output root directory path.</param>
        /// <returns>Set of absolute generated texture paths.</returns>
        private static HashSet<string> CollectExpectedTexturePaths(List<Layer> tree, string outputFullPath)
        {
            HashSet<string> expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tree == null)
            {
                return expectedPaths;
            }

            for (int i = tree.Count - 1; i >= 0; i--)
            {
                CollectExpectedTexturePathsForLayer(tree[i], outputFullPath, expectedPaths);
            }

            return expectedPaths;
        }

        /// <summary>
        /// Recursively collects generated texture paths for a single layer.
        /// </summary>
        /// <param name="layer">Layer to inspect.</param>
        /// <param name="currentDirectory">Current output directory for this layer.</param>
        /// <param name="result">Destination set for generated texture paths.</param>
        private static void CollectExpectedTexturePathsForLayer(Layer layer, string currentDirectory, HashSet<string> result)
        {
            string safeLayerName = MakeNameSafe(layer.Name);
            if (layer.Children.Count > 0 || layer.Rect.width == 0)
            {
                CollectExpectedTexturePathsForFolderLayer(layer, safeLayerName, currentDirectory, result);
                return;
            }

            if (layer.IsTextLayer || layer.Rect.width <= 0)
            {
                return;
            }

            string texturePath = Path.Combine(currentDirectory, safeLayerName + ".png");
            result.Add(NormalizePath(texturePath));
        }

        /// <summary>
        /// Collects generated texture paths for folder/group layers.
        /// </summary>
        /// <param name="layer">Folder layer to inspect.</param>
        /// <param name="safeLayerName">Layer name after <see cref="MakeNameSafe(string)"/>.</param>
        /// <param name="currentDirectory">Current output directory for this layer.</param>
        /// <param name="result">Destination set for generated texture paths.</param>
        private static void CollectExpectedTexturePathsForFolderLayer(
            Layer layer,
            string safeLayerName,
            string currentDirectory,
            HashSet<string> result)
        {
            if (safeLayerName.ContainsIgnoreCase("|Button"))
            {
                CollectButtonTexturePaths(layer, currentDirectory, result);
                return;
            }

            if (safeLayerName.ContainsIgnoreCase("|Animation"))
            {
                string animationLayerName = safeLayerName.ReplaceIgnoreCase("|Animation", string.Empty);
                string[] nameParts = animationLayerName.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                string animationFolderName = nameParts.Length > 0 ? nameParts[0] : animationLayerName;
                if (string.IsNullOrEmpty(animationFolderName))
                {
                    animationFolderName = "Animation";
                }

                string animationFolderPath = Path.Combine(currentDirectory, animationFolderName);
                foreach (Layer child in layer.Children)
                {
                    if (child.Children.Count == 0 && child.Rect.width > 0)
                    {
                        string path = Path.Combine(animationFolderPath, child.Name + ".png");
                        result.Add(NormalizePath(path));
                    }
                }

                return;
            }

            string childDirectory = Path.Combine(currentDirectory, safeLayerName);
            for (int i = layer.Children.Count - 1; i >= 0; i--)
            {
                CollectExpectedTexturePathsForLayer(layer.Children[i], childDirectory, result);
            }
        }

        /// <summary>
        /// Collects texture outputs produced by <c>|Button</c> layers.
        /// </summary>
        /// <param name="layer">Button layer.</param>
        /// <param name="currentDirectory">Current output directory.</param>
        /// <param name="result">Destination set for generated texture paths.</param>
        private static void CollectButtonTexturePaths(Layer layer, string currentDirectory, HashSet<string> result)
        {
            foreach (Layer child in layer.Children)
            {
                string textureName;
                if (!TryGetButtonTextureName(child, out textureName))
                {
                    continue;
                }

                if (child.Children.Count > 0 || child.Rect.width <= 0)
                {
                    continue;
                }

                string path = Path.Combine(currentDirectory, textureName + ".png");
                result.Add(NormalizePath(path));
            }
        }

        /// <summary>
        /// Attempts to resolve the generated texture name for a button child layer.
        /// </summary>
        /// <param name="layer">Button child layer.</param>
        /// <param name="textureName">Resolved output texture name.</param>
        /// <returns>True if this child creates a texture; otherwise false.</returns>
        private static bool TryGetButtonTextureName(Layer layer, out string textureName)
        {
            textureName = layer.Name;

            if (layer.Name.ContainsIgnoreCase("|Disabled"))
            {
                textureName = layer.Name.ReplaceIgnoreCase("|Disabled", string.Empty);
                return true;
            }

            if (layer.Name.ContainsIgnoreCase("|Highlighted"))
            {
                textureName = layer.Name.ReplaceIgnoreCase("|Highlighted", string.Empty);
                return true;
            }

            if (layer.Name.ContainsIgnoreCase("|Pressed"))
            {
                textureName = layer.Name.ReplaceIgnoreCase("|Pressed", string.Empty);
                return true;
            }

            if (layer.Name.ContainsIgnoreCase("|Default") ||
                layer.Name.ContainsIgnoreCase("|Enabled") ||
                layer.Name.ContainsIgnoreCase("|Normal") ||
                layer.Name.ContainsIgnoreCase("|Up"))
            {
                textureName = layer.Name.ReplaceIgnoreCase("|Default", string.Empty);
                textureName = textureName.ReplaceIgnoreCase("|Enabled", string.Empty);
                textureName = textureName.ReplaceIgnoreCase("|Normal", string.Empty);
                textureName = textureName.ReplaceIgnoreCase("|Up", string.Empty);
                return true;
            }

            if (layer.Name.ContainsIgnoreCase("|Text") && !layer.IsTextLayer)
            {
                textureName = layer.Name.ReplaceIgnoreCase("|Text", string.Empty);
                return true;
            }

            return false;
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
            // replace all special characters with an underscore
            Regex pattern = new Regex("[/:&.<>,$¢;+]");
            string newName = pattern.Replace(name, "_");

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
            layer.Name = MakeNameSafe(layer.Name);
            if (layer.Children.Count > 0 || layer.Rect.width == 0)
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
            if (layer.Name.ContainsIgnoreCase("|Button"))
            {
                layer.Name = layer.Name.ReplaceIgnoreCase("|Button", string.Empty);

                if (UseUnityUI)
                {
                    CreateUIButton(layer);
                }
                else
                {
                    ////CreateGUIButton(layer);
                }
            }
            else if (layer.Name.ContainsIgnoreCase("|Animation"))
            {
                layer.Name = layer.Name.ReplaceIgnoreCase("|Animation", string.Empty);

                string oldPath = currentPath;
                GameObject oldGroupObject = currentGroupGameObject;

                currentPath = Path.Combine(currentPath, layer.Name.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)[0]);
                Directory.CreateDirectory(currentPath);

                if (UseUnityUI)
                {
                    ////CreateUIAnimation(layer);
                }
                else
                {
                    CreateAnimation(layer);
                }

                currentPath = oldPath;
                currentGroupGameObject = oldGroupObject;
            }
            else
            {
                // it is a "normal" folder layer that contains children layers
                string oldPath = currentPath;
                GameObject oldGroupObject = currentGroupGameObject;

                currentPath = Path.Combine(currentPath, layer.Name);
                Directory.CreateDirectory(currentPath);

                if (LayoutInScene || CreatePrefab)
                {
                    if (UseUnityUI && UseTargetCanvasCoordinates)
                    {
                        currentGroupGameObject = new GameObject(layer.Name, typeof(RectTransform));
                        RectTransform groupTransform = currentGroupGameObject.GetComponent<RectTransform>();
                        groupTransform.SetParent(oldGroupObject.transform, false);
                        groupTransform.anchorMin = new Vector2(0.5f, 0.5f);
                        groupTransform.anchorMax = new Vector2(0.5f, 0.5f);
                        groupTransform.pivot = new Vector2(0.5f, 0.5f);
                        groupTransform.anchoredPosition = Vector2.zero;
                    }
                    else
                    {
                        currentGroupGameObject = new GameObject(layer.Name);
                        currentGroupGameObject.transform.parent = oldGroupObject.transform;
                    }
                }

                ExportTree(layer.Children);

                currentPath = oldPath;
                currentGroupGameObject = oldGroupObject;
            }
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
            if (!layer.IsTextLayer)
            {
                if (LayoutInScene || CreatePrefab)
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
                else
                {
                    // it is not being laid out in the scene, so simply save out the .png file
                    CreatePNG(layer);
                }
            }
            else
            {
                // it is a text layer
                if (LayoutInScene || CreatePrefab)
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
            }
        }

        /// <summary>
        /// Saves the given <see cref="Layer"/> as a PNG on the hard drive.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to save as a PNG.</param>
        /// <returns>The filepath to the created PNG file.</returns>
        private static string CreatePNG(Layer layer)
        {
            string file = string.Empty;

            if (layer.Children.Count == 0 && layer.Rect.width > 0)
            {
                file = Path.Combine(currentPath, layer.Name + ".png");
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
                sprite = ImportSprite(GetRelativePath(file), packingTag);
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

            GameObject gameObject = new GameObject(layer.Name);
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

            GameObject gameObject = new GameObject(layer.Name);
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
            float fps = 30;

            string[] args = layer.Name.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string arg in args)
            {
                if (arg.ContainsIgnoreCase("FPS="))
                {
                    layer.Name = layer.Name.Replace("|" + arg, string.Empty);

                    string[] fpsArgs = arg.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!float.TryParse(fpsArgs[1], out fps))
                    {
                        Debug.LogError(string.Format("Unable to parse FPS: \"{0}\"", arg));
                    }
                }
            }

            List<Sprite> frames = new List<Sprite>();

            Layer firstChild = layer.Children.First();
            SpriteRenderer spriteRenderer = CreateSpriteGameObject(firstChild);
            spriteRenderer.name = layer.Name;

            foreach (Layer child in layer.Children)
            {
                frames.Add(CreateSprite(child, layer.Name));
            }

            spriteRenderer.sprite = frames[0];

#if UNITY_5_3_OR_NEWER
            // Create Animator Controller with an Animation Clip
            UnityEditor.Animations.AnimatorController controller = new UnityEditor.Animations.AnimatorController();
            controller.AddLayer("Base Layer");

            UnityEditor.Animations.AnimatorControllerLayer controllerLayer = controller.layers[0];
            UnityEditor.Animations.AnimatorState state = controllerLayer.stateMachine.AddState(layer.Name);
            state.motion = CreateSpriteAnimationClip(layer.Name, frames, fps);

            AssetDatabase.CreateAsset(controller, GetRelativePath(currentPath) + "/" + layer.Name + ".controller");
#else // Unity 4
            // Create Animator Controller with an Animation Clip
            UnityEditor.Animations.AnimatorController controller = new UnityEditor.Animations.AnimatorController();
            UnityEditor.Animations.AnimatorControllerLayer controllerLayer = controller.AddLayer("Base Layer");

            UnityEditor.Animations.AnimatorState state = controllerLayer.stateMachine.AddState(layer.Name);
            state.SetAnimationClip(CreateSpriteAnimationClip(layer.Name, frames, fps));

            AssetDatabase.CreateAsset(controller, GetRelativePath(currentPath) + "/" + layer.Name + ".controller");
#endif

            // Add an Animator and assign it the controller
            Animator animator = spriteRenderer.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
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

            AssetDatabase.CreateAsset(clip, GetRelativePath(currentPath) + "/" + name + ".anim");

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
            if (UseTargetCanvasCoordinates)
            {
                Vector2 scaledSize = GetScaledLayerSize(layer.Rect);
                float uiWidthPixels = scaledSize.x;
                float uiHeightPixels = scaledSize.y;

                GameObject uiObject = new GameObject(layer.Name, typeof(RectTransform));
                uiObject.transform.SetParent(currentGroupGameObject.transform, false);

                RectTransform uiTransform = uiObject.GetComponent<RectTransform>();
                uiTransform.anchorMin = new Vector2(0.5f, 0.5f);
                uiTransform.anchorMax = new Vector2(0.5f, 0.5f);
                uiTransform.pivot = new Vector2(0.5f, 0.5f);
                uiTransform.anchoredPosition = GetLocalAnchoredPosition(layer.Rect, currentGroupGameObject);
                uiTransform.sizeDelta = new Vector2(uiWidthPixels, uiHeightPixels);

                Image uiImage = uiObject.AddComponent<Image>();
                uiImage.sprite = CreateSprite(layer);
                return uiImage;
            }

            float x = layer.Rect.x / PixelsToUnits;
            float y = layer.Rect.y / PixelsToUnits;

            // Photoshop increase Y while going down. Unity increases Y while going up.  So, we need to reverse the Y position.
            y = (CanvasSize.y / PixelsToUnits) - y;

            // Photoshop uses the upper left corner as the pivot (0,0).  Unity defaults to use the center as (0,0), so we must offset the positions.
            x = x - ((CanvasSize.x / 2) / PixelsToUnits);
            y = y - ((CanvasSize.y / 2) / PixelsToUnits);

            float width = layer.Rect.width / PixelsToUnits;
            float height = layer.Rect.height / PixelsToUnits;

            GameObject gameObject = new GameObject(layer.Name);
            gameObject.transform.position = new Vector3(x + (width / 2), y - (height / 2), currentDepth);
            gameObject.transform.parent = currentGroupGameObject.transform;

            // if the current group object actually has a position (not a normal Photoshop folder layer), then offset the position accordingly
            gameObject.transform.position = new Vector3(gameObject.transform.position.x + currentGroupGameObject.transform.position.x, gameObject.transform.position.y + currentGroupGameObject.transform.position.y, gameObject.transform.position.z);

            currentDepth -= depthStep;

            Image image = gameObject.AddComponent<Image>();
            image.sprite = CreateSprite(layer);

            RectTransform transform = gameObject.GetComponent<RectTransform>();
            transform.sizeDelta = new Vector2(width, height);

            return image;
        }

        /// <summary>
        /// Creates a Unity UI <see cref="UnityEngine.UI.Text"/> <see cref="GameObject"/> with the text from a PSD <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> used to create the <see cref="UnityEngine.UI.Text"/> from.</param>
        private static void CreateUIText(Layer layer)
        {
            if (UseTargetCanvasCoordinates)
            {
                Color uiColor = layer.FillColor;
                uiColor = ApplyLayerOpacity(uiColor, layer);
                Vector2 scaledSize = GetScaledLayerSize(layer.Rect);
                float uiWidthPixels = scaledSize.x;
                float uiHeightPixels = scaledSize.y;

                GameObject uiObject = new GameObject(layer.Name, typeof(RectTransform));
                uiObject.transform.SetParent(currentGroupGameObject.transform, false);

                RectTransform uiTransform = uiObject.GetComponent<RectTransform>();
                uiTransform.anchorMin = new Vector2(0.5f, 0.5f);
                uiTransform.anchorMax = new Vector2(0.5f, 0.5f);
                uiTransform.pivot = new Vector2(0.5f, 0.5f);
                uiTransform.anchoredPosition = GetLocalAnchoredPosition(layer.Rect, currentGroupGameObject);
                uiTransform.sizeDelta = new Vector2(uiWidthPixels, uiHeightPixels);

                Font uiFont = GetFontForLayer(layer);
                Text uiText = uiObject.AddComponent<Text>();
                uiText.text = layer.Text;
                uiText.font = uiFont;
                uiText.rectTransform.sizeDelta = new Vector2(uiWidthPixels, uiHeightPixels);

                float uiFontSize = layer.FontSize * GetTargetCanvasUniformScale();
                float uiCeiling = Mathf.Ceil(uiFontSize);
                if (uiFontSize < uiCeiling)
                {
                    float scaleFactor = uiCeiling / uiFontSize;
                    uiText.fontSize = (int)uiCeiling;
                    uiText.rectTransform.sizeDelta *= scaleFactor;
                    uiText.rectTransform.localScale /= scaleFactor;
                }
                else
                {
                    uiText.fontSize = (int)uiFontSize;
                }

                uiText.color = uiColor;
                uiText.alignment = TextAnchor.MiddleCenter;

                switch (layer.Justification)
                {
                    case TextJustification.Left:
                        uiText.alignment = TextAnchor.MiddleLeft;
                        break;
                    case TextJustification.Right:
                        uiText.alignment = TextAnchor.MiddleRight;
                        break;
                    case TextJustification.Center:
                        uiText.alignment = TextAnchor.MiddleCenter;
                        break;
                }

                return;
            }

            Color color = ApplyLayerOpacity(layer.FillColor, layer);

            float x = layer.Rect.x / PixelsToUnits;
            float y = layer.Rect.y / PixelsToUnits;

            // Photoshop increase Y while going down. Unity increases Y while going up.  So, we need to reverse the Y position.
            y = (CanvasSize.y / PixelsToUnits) - y;

            // Photoshop uses the upper left corner as the pivot (0,0).  Unity defaults to use the center as (0,0), so we must offset the positions.
            x = x - ((CanvasSize.x / 2) / PixelsToUnits);
            y = y - ((CanvasSize.y / 2) / PixelsToUnits);

            float width = layer.Rect.width / PixelsToUnits;
            float height = layer.Rect.height / PixelsToUnits;

            GameObject gameObject = new GameObject(layer.Name);
            gameObject.transform.position = new Vector3(x + (width / 2), y - (height / 2), currentDepth);
            gameObject.transform.parent = currentGroupGameObject.transform;

            currentDepth -= depthStep;

            Font font = GetFontForLayer(layer);

            Text textUI = gameObject.AddComponent<Text>();
            textUI.text = layer.Text;
            textUI.font = font;
            textUI.rectTransform.sizeDelta = new Vector2(width, height);

            float fontSize = layer.FontSize / PixelsToUnits;
            float ceiling = Mathf.Ceil(fontSize);
            if (fontSize < ceiling)
            {
                // Unity UI Text doesn't support floating point font sizes, so we have to round to the next size and scale everything else
                float scaleFactor = ceiling / fontSize;
                textUI.fontSize = (int)ceiling;
                textUI.rectTransform.sizeDelta *= scaleFactor;
                textUI.rectTransform.localScale /= scaleFactor;
            }
            else
            {
                textUI.fontSize = (int)fontSize;
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
            // create an empty Image object with a Button behavior attached
            Image image = CreateUIImage(layer);
            Button button = image.gameObject.AddComponent<Button>();

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
                if (child.Name.ContainsIgnoreCase("|Disabled"))
                {
                    child.Name = child.Name.ReplaceIgnoreCase("|Disabled", string.Empty);
                    button.transition = Selectable.Transition.SpriteSwap;

                    SpriteState spriteState = button.spriteState;
                    spriteState.disabledSprite = CreateSprite(child);
                    button.spriteState = spriteState;
                }
                else if (child.Name.ContainsIgnoreCase("|Highlighted"))
                {
                    child.Name = child.Name.ReplaceIgnoreCase("|Highlighted", string.Empty);
                    button.transition = Selectable.Transition.SpriteSwap;

                    SpriteState spriteState = button.spriteState;
                    spriteState.highlightedSprite = CreateSprite(child);
                    button.spriteState = spriteState;
                }
                else if (child.Name.ContainsIgnoreCase("|Pressed"))
                {
                    child.Name = child.Name.ReplaceIgnoreCase("|Pressed", string.Empty);
                    button.transition = Selectable.Transition.SpriteSwap;

                    SpriteState spriteState = button.spriteState;
                    spriteState.pressedSprite = CreateSprite(child);
                    button.spriteState = spriteState;
                }
                else if (child.Name.ContainsIgnoreCase("|Default") ||
                         child.Name.ContainsIgnoreCase("|Enabled") ||
                         child.Name.ContainsIgnoreCase("|Normal") ||
                         child.Name.ContainsIgnoreCase("|Up"))
                {
                    child.Name = child.Name.ReplaceIgnoreCase("|Default", string.Empty);
                    child.Name = child.Name.ReplaceIgnoreCase("|Enabled", string.Empty);
                    child.Name = child.Name.ReplaceIgnoreCase("|Normal", string.Empty);
                    child.Name = child.Name.ReplaceIgnoreCase("|Up", string.Empty);

                    image.sprite = CreateSprite(child);

                    RectTransform transform = image.gameObject.GetComponent<RectTransform>();
                    if (UseTargetCanvasCoordinates)
                    {
                        Vector2 scaledSize = GetScaledLayerSize(child.Rect);
                        transform.anchorMin = new Vector2(0.5f, 0.5f);
                        transform.anchorMax = new Vector2(0.5f, 0.5f);
                        transform.pivot = new Vector2(0.5f, 0.5f);
                        transform.anchoredPosition = GetLocalAnchoredPosition(
                            child.Rect,
                            button.gameObject.transform.parent != null ? button.gameObject.transform.parent.gameObject : null);
                        transform.sizeDelta = scaledSize;
                    }
                    else
                    {
                        float x = child.Rect.x / PixelsToUnits;
                        float y = child.Rect.y / PixelsToUnits;

                        // Photoshop increase Y while going down. Unity increases Y while going up.  So, we need to reverse the Y position.
                        y = (CanvasSize.y / PixelsToUnits) - y;

                        // Photoshop uses the upper left corner as the pivot (0,0).  Unity defaults to use the center as (0,0), so we must offset the positions.
                        x = x - ((CanvasSize.x / 2) / PixelsToUnits);
                        y = y - ((CanvasSize.y / 2) / PixelsToUnits);

                        float width = child.Rect.width / PixelsToUnits;
                        float height = child.Rect.height / PixelsToUnits;

                        image.gameObject.transform.position = new Vector3(x + (width / 2), y - (height / 2), currentDepth);
                        transform.sizeDelta = new Vector2(width, height);
                    }

                    button.targetGraphic = image;
                }
                else if (child.Name.ContainsIgnoreCase("|Text") && !child.IsTextLayer)
                {
                    child.Name = child.Name.ReplaceIgnoreCase("|Text", string.Empty);

                    GameObject oldGroupObject = currentGroupGameObject;
                    currentGroupGameObject = button.gameObject;

                    // If the "text" is a normal art layer, create an Image object from the "text"
                    CreateUIImage(child);

                    currentGroupGameObject = oldGroupObject;
                }

                if (child.IsTextLayer)
                {
                    // TODO: Create a child text game object
                }
            }
        }

        /// <summary>
        /// Converts a PSD layer rectangle to an anchored position relative to canvas center.
        /// </summary>
        /// <param name="rect">The PSD layer rectangle.</param>
        /// <returns>Anchored position in UI pixels.</returns>
        private static Vector2 GetAnchoredPositionFromLayerRect(Rect rect)
        {
            float scaleX = GetTargetCanvasScaleX();
            float scaleY = GetTargetCanvasScaleY();
            float x = (rect.x + (rect.width * 0.5f) - (CanvasSize.x * 0.5f)) * scaleX;
            float y = ((CanvasSize.y * 0.5f) - (rect.y + (rect.height * 0.5f))) * scaleY;
            return new Vector2(x, y);
        }

        /// <summary>
        /// Gets scaled layer size for target canvas mapping.
        /// </summary>
        /// <param name="rect">The PSD layer rectangle.</param>
        /// <returns>Scaled width and height.</returns>
        private static Vector2 GetScaledLayerSize(Rect rect)
        {
            return new Vector2(rect.width * GetTargetCanvasScaleX(), rect.height * GetTargetCanvasScaleY());
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
        /// Converts a PSD layer rect to a local anchored position relative to the specified parent.
        /// </summary>
        /// <param name="rect">The PSD layer rectangle.</param>
        /// <param name="parentObject">The intended parent object.</param>
        /// <returns>Local anchored position for the created UI element.</returns>
        private static Vector2 GetLocalAnchoredPosition(Rect rect, GameObject parentObject)
        {
            Vector2 absolute = GetAnchoredPositionFromLayerRect(rect);
            Vector2 parentAbsolute = GetAbsoluteAnchoredPosition(parentObject);
            return absolute - parentAbsolute;
        }

        /// <summary>
        /// Gets cumulative anchored position from the current transform up to the root.
        /// </summary>
        /// <param name="currentObject">The object to accumulate from.</param>
        /// <returns>Cumulative anchored position.</returns>
        private static Vector2 GetAbsoluteAnchoredPosition(GameObject currentObject)
        {
            if (currentObject == null)
            {
                return Vector2.zero;
            }

            Vector2 offset = Vector2.zero;
            Transform current = currentObject.transform;
            Transform stopAt = rootPsdGameObject != null ? rootPsdGameObject.transform.parent : null;
            while (current != null && current != stopAt)
            {
                RectTransform rectTransform = current as RectTransform;
                if (rectTransform != null)
                {
                    offset += rectTransform.anchoredPosition;
                }

                current = current.parent;
            }

            return offset;
        }
        #endregion
    }
}

