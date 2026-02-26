namespace PsdLayoutTool2
{
    using System;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// A custom Inspector to allow PSD files to be turned into prefabs and separate textures per layer.
    /// </summary>
    /// <remarks>
    /// Unity isn't able to draw the custom inspector for a TextureImporter (even if calling the base
    /// method or calling DrawDefaultInspector).  It comes out as just a generic, hard to use mess of GUI
    /// items.  To add in the buttons we want without disrupting the normal GUI for TextureImporter, we have
    /// to do some reflection "magic".
    /// Thanks to DeadNinja: http://forum.unity3d.com/threads/custom-textureimporterinspector.260833/
    /// </remarks>
    [CustomEditor(typeof(TextureImporter))]
    public class PsdInspector : Editor
    {
        /// <summary>
        /// Supported inspector display languages.
        /// </summary>
        private enum InspectorLanguage
        {
            /// <summary>
            /// Chinese UI.
            /// </summary>
            Chinese = 0,

            /// <summary>
            /// English UI.
            /// </summary>
            English = 1
        }

        /// <summary>
        /// EditorPrefs key for output mode.
        /// </summary>
        private const string OutputModePrefKey = "PsdLayoutTool2.OutputMode";

        /// <summary>
        /// EditorPrefs key for output folder name.
        /// </summary>
        private const string OutputFolderNamePrefKey = "PsdLayoutTool2.OutputFolderName";

        /// <summary>
        /// EditorPrefs key for prefab output mode.
        /// </summary>
        private const string PrefabModePrefKey = "PsdLayoutTool2.PrefabMode";

        /// <summary>
        /// EditorPrefs key for target canvas hierarchy path.
        /// </summary>
        private const string TargetCanvasPathPrefKey = "PsdLayoutTool2.TargetCanvasPath";

        /// <summary>
        /// EditorPrefs key for scaling generated UI to target canvas size.
        /// </summary>
        private const string ScaleToTargetCanvasPrefKey = "PsdLayoutTool2.ScaleToTargetCanvas";

        /// <summary>
        /// EditorPrefs key for preserving aspect ratio while scaling to target canvas.
        /// </summary>
        private const string PreserveAspectPrefKey = "PsdLayoutTool2.PreserveAspectWhenScalingToCanvas";

        /// <summary>
        /// EditorPrefs key for inspector display language.
        /// </summary>
        private const string LanguagePrefKey = "PsdLayoutTool2.InspectorLanguage";

        /// <summary>
        /// Language options displayed in dropdown.
        /// </summary>
        private static readonly string[] LanguageOptions = { "中文", "English" };

        /// <summary>
        /// The native Unity editor used to render the <see cref="TextureImporter"/>'s Inspector.
        /// </summary>
        private Editor nativeEditor;

        /// <summary>
        /// The style used to draw the section header text.
        /// </summary>
        private GUIStyle guiStyle;

        /// <summary>
        /// Current inspector display language.
        /// </summary>
        private static InspectorLanguage CurrentLanguage { get; set; } = InspectorLanguage.Chinese;

        /// <summary>
        /// Called by Unity when any Texture file is first clicked on and the Inspector is populated.
        /// </summary>
        public void OnEnable()
        {
            // use reflection to get the default Inspector
            Type type = Type.GetType("UnityEditor.TextureImporterInspector, UnityEditor");
            nativeEditor = type != null ? CreateEditor(target, type) : null;

            if (EditorPrefs.HasKey(OutputModePrefKey))
            {
                PsdImporter.OutputMode = (PsdImporter.OutputDirectoryMode)EditorPrefs.GetInt(OutputModePrefKey, (int)PsdImporter.OutputDirectoryMode.PsdDirectory);
            }

            if (EditorPrefs.HasKey(OutputFolderNamePrefKey))
            {
                PsdImporter.OutputFolderName = EditorPrefs.GetString(OutputFolderNamePrefKey, string.Empty);
            }

            if (EditorPrefs.HasKey(PrefabModePrefKey))
            {
                PsdImporter.PrefabMode = (PsdImporter.PrefabOutputMode)EditorPrefs.GetInt(PrefabModePrefKey, (int)PsdImporter.PrefabOutputMode.SiblingToOutputFolder);
            }

            if (EditorPrefs.HasKey(TargetCanvasPathPrefKey))
            {
                PsdImporter.TargetCanvasPath = EditorPrefs.GetString(TargetCanvasPathPrefKey, string.Empty);
            }

            if (EditorPrefs.HasKey(ScaleToTargetCanvasPrefKey))
            {
                PsdImporter.ScaleToTargetCanvas = EditorPrefs.GetBool(ScaleToTargetCanvasPrefKey, true);
            }

            if (EditorPrefs.HasKey(PreserveAspectPrefKey))
            {
                PsdImporter.PreserveAspectWhenScalingToCanvas = EditorPrefs.GetBool(PreserveAspectPrefKey, true);
            }

            if (EditorPrefs.HasKey(LanguagePrefKey))
            {
                CurrentLanguage = (InspectorLanguage)EditorPrefs.GetInt(LanguagePrefKey, (int)InspectorLanguage.Chinese);
            }
        }

        /// <summary>
        /// Draws the Inspector GUI for the TextureImporter.
        /// Normal Texture files should appear as they normally do, however PSD files will have additional items.
        /// </summary>
        public override void OnInspectorGUI()
        {
            EnsureHeaderStyle();

            if (nativeEditor != null)
            {
                // check if it is a PSD file selected
                string assetPath = ((TextureImporter)target).assetPath;

                if (assetPath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase))
                {
                    GUIContent languageLabel = LocalizedContent(
                        "界面语言",
                        "Inspector Language",
                        "切换此插件 Inspector 的显示语言。",
                        "Switch the display language for this plugin inspector.");
                    int languageIndex = EditorGUILayout.Popup(languageLabel, (int)CurrentLanguage, LanguageOptions);
                    if (languageIndex != (int)CurrentLanguage)
                    {
                        CurrentLanguage = (InspectorLanguage)languageIndex;
                        EditorPrefs.SetInt(LanguagePrefKey, languageIndex);
                    }

                    GUILayout.Label(Localize("<b>PSD 布局工具 2</b>", "<b>PSD Layout Tool 2</b>"), guiStyle, GUILayout.Height(23));

                    GUIContent maximumDepthLabel = LocalizedContent(
                        "最大深度（Z）",
                        "Maximum Depth (Z)",
                        "用于图层布局的最远 Z 值。导入时会从该值逐层递减到 0。\n数值越大，层与层之间的深度间隔越明显。",
                        "Farthest Z value for layer layout. Import decrements this value layer by layer toward 0.\nLarger values create more depth spacing between layers.");
                    PsdImporter.MaximumDepth = EditorGUILayout.FloatField(maximumDepthLabel, PsdImporter.MaximumDepth);

                    GUIContent pixelsToUnitsLabel = LocalizedContent(
                        "像素到单位（PPU）",
                        "Pixels To Units (PPU)",
                        "每多少像素对应 1 个 Unity 世界单位。\n通常建议与项目中 Sprite 的 Pixels Per Unit 保持一致（常见为 100）。",
                        "How many pixels equal 1 Unity world unit.\nUsually keep this consistent with your Sprite Pixels Per Unit (commonly 100).");
                    PsdImporter.PixelsToUnits = EditorGUILayout.FloatField(pixelsToUnitsLabel, PsdImporter.PixelsToUnits);

                    EditorGUI.BeginChangeCheck();
                    GUIContent useUnityUILabel = LocalizedContent(
                        "使用 Unity UI",
                        "Use Unity UI",
                        "开启后生成 Canvas/Image/Text/Button 等 UI 对象。\n关闭后生成 SpriteRenderer/TextMesh 等普通场景对象。",
                        "When enabled, generates Canvas/Image/Text/Button UI objects.\nWhen disabled, generates regular scene objects like SpriteRenderer/TextMesh.");
                    PsdImporter.UseUnityUI = EditorGUILayout.Toggle(useUnityUILabel, PsdImporter.UseUnityUI);

                    if (PsdImporter.UseUnityUI)
                    {
                        UnityEngine.Canvas currentCanvas = FindCanvasByHierarchyPath(PsdImporter.TargetCanvasPath);
                        GUIContent targetCanvasLabel = LocalizedContent(
                            "目标 Canvas（可选）",
                            "Target Canvas (Optional)",
                            "指定后：生成结果会挂到这个 Canvas 下，并按 Canvas 的像素坐标对齐。\n留空：按旧行为自动创建一个 World Space Canvas。",
                            "When set, generated results are parented under this canvas and aligned in canvas pixel coordinates.\nWhen empty, legacy behavior creates a World Space canvas automatically.");
                        UnityEngine.Canvas selectedCanvas = (UnityEngine.Canvas)EditorGUILayout.ObjectField(
                            targetCanvasLabel,
                            currentCanvas,
                            typeof(UnityEngine.Canvas),
                            true);

                        if (selectedCanvas != currentCanvas)
                        {
                            PsdImporter.TargetCanvasPath = selectedCanvas != null ? GetHierarchyPath(selectedCanvas.transform) : string.Empty;
                        }

                        if (!string.IsNullOrEmpty(PsdImporter.TargetCanvasPath))
                        {
                            EditorGUILayout.LabelField(Localize("Canvas 路径", "Canvas Path"), PsdImporter.TargetCanvasPath);
                            if (currentCanvas == null)
                            {
                                EditorGUILayout.HelpBox(
                                    Localize(
                                        "未在当前场景找到该 Canvas，将回退为自动创建 Canvas。请重新选择目标 Canvas。",
                                        "The canvas was not found in the current scene. Import will fall back to auto-created canvas. Please reselect the target canvas."),
                                    MessageType.Warning);
                            }
                        }

                        GUIContent scaleToCanvasLabel = LocalizedContent(
                            "匹配目标 Canvas 尺寸",
                            "Scale To Target Canvas",
                            "开启后会把 PSD 坐标与尺寸按目标 Canvas 尺寸缩放映射。\n关闭后保持 PSD 1:1 像素映射。",
                            "When enabled, PSD positions and sizes are scaled to target canvas size.\nWhen disabled, keeps strict 1:1 PSD pixel mapping.");
                        PsdImporter.ScaleToTargetCanvas = EditorGUILayout.Toggle(scaleToCanvasLabel, PsdImporter.ScaleToTargetCanvas);

                        EditorGUI.BeginDisabledGroup(!PsdImporter.ScaleToTargetCanvas);
                        GUIContent preserveAspectLabel = LocalizedContent(
                            "保持宽高比（不拉伸）",
                            "Preserve Aspect Ratio (No Stretch)",
                            "开启后使用等比缩放适配目标 Canvas，避免 X/Y 比例不一致导致的拉伸。\n关闭后按宽高分别缩放（可能拉伸）。",
                            "When enabled, uses uniform scaling to avoid stretch when X/Y ratios differ.\nWhen disabled, scales X and Y independently (may stretch).");
                        PsdImporter.PreserveAspectWhenScalingToCanvas = EditorGUILayout.Toggle(
                            preserveAspectLabel,
                            PsdImporter.PreserveAspectWhenScalingToCanvas);
                        EditorGUI.EndDisabledGroup();
                    }

                    GUIContent outputModeLabel = LocalizedContent(
                        "资源输出位置",
                        "Output Directory",
                        "控制导出的 PNG、动画片段（.anim）和控制器（.controller）保存到哪里。",
                        "Controls where exported PNGs, animation clips (.anim), and controllers (.controller) are saved.");
                    int outputModeIndex = EditorGUILayout.Popup(outputModeLabel, ToOutputModeIndex(PsdImporter.OutputMode), GetOutputModeOptions());
                    PsdImporter.OutputMode = ToOutputMode(outputModeIndex);

                    GUIContent outputFolderNameLabel = LocalizedContent(
                        "输出文件夹名",
                        "Output Folder Name",
                        "生成文件夹名称。留空时自动使用 PSD 文件名。\n可用于按模块命名，例如 UI_MainMenu、角色立绘等。",
                        "Name of generated output folder. Empty uses PSD file name automatically.\nUseful for module naming such as UI_MainMenu, character portraits, etc.");
                    PsdImporter.OutputFolderName = EditorGUILayout.TextField(outputFolderNameLabel, PsdImporter.OutputFolderName);

                    GUIContent prefabModeLabel = LocalizedContent(
                        "Prefab 输出位置",
                        "Prefab Output",
                        "控制 Generate Prefab 生成的预制体保存位置。",
                        "Controls where prefabs generated by Generate Prefab are saved.");
                    int prefabModeIndex = EditorGUILayout.Popup(prefabModeLabel, ToPrefabModeIndex(PsdImporter.PrefabMode), GetPrefabModeOptions());
                    PsdImporter.PrefabMode = ToPrefabMode(prefabModeIndex);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorPrefs.SetInt(OutputModePrefKey, (int)PsdImporter.OutputMode);
                        EditorPrefs.SetString(OutputFolderNamePrefKey, PsdImporter.OutputFolderName ?? string.Empty);
                        EditorPrefs.SetInt(PrefabModePrefKey, (int)PsdImporter.PrefabMode);
                        EditorPrefs.SetString(TargetCanvasPathPrefKey, PsdImporter.TargetCanvasPath ?? string.Empty);
                        EditorPrefs.SetBool(ScaleToTargetCanvasPrefKey, PsdImporter.ScaleToTargetCanvas);
                        EditorPrefs.SetBool(PreserveAspectPrefKey, PsdImporter.PreserveAspectWhenScalingToCanvas);
                    }

                    EditorGUILayout.HelpBox(
                        Localize(
                            "提示：标签匹配不区分大小写。|Button 仅在启用 Unity UI 时生效，|Animation 仅在非 UI 模式生效。",
                            "Tip: Tag matching is case-insensitive. |Button only works when Unity UI is enabled, and |Animation only works in non-UI mode."),
                        MessageType.Info);

                    // draw our custom buttons for PSD files
                    if (GUILayout.Button(Localize("导出图层为纹理", "Export Layers As Textures")))
                    {
                        PsdImporter.ExportLayersAsTextures(assetPath);
                    }

                    if (GUILayout.Button(Localize("在当前场景中布局", "Layout In Current Scene")))
                    {
                        PsdImporter.LayoutInCurrentScene(assetPath);
                    }

                    if (GUILayout.Button(Localize("生成预制体", "Generate Prefab")))
                    {
                        PsdImporter.GeneratePrefab(assetPath);
                    }

                    GUILayout.Space(3);

                    GUILayout.Box(string.Empty, GUILayout.Height(1), GUILayout.MaxWidth(Screen.width - 30));

                    GUILayout.Space(3);

                    GUILayout.Label(Localize("<b>Unity 纹理导入设置</b>", "<b>Unity Texture Import Settings</b>"), guiStyle, GUILayout.Height(23));

                    // draw the default Inspector for the PSD
                    nativeEditor.OnInspectorGUI();
                }
                else
                {
                    // It is a "normal" Texture, not a PSD
                    nativeEditor.OnInspectorGUI();
                }
            }

            // Unfortunately we cant hide the ImportedObject section because the interal InspectorWindow checks via
            // "if (editor is AssetImporterEditor)" and all flags that this check sets are method local variables
            // so aside from direct patching UnityEditor.dll, reflection cannot be used here.

            // Therefore we just move the ImportedObject section out of view
            ////GUILayout.Space(2048);
        }

        /// <summary>
        /// Converts output mode enum to popup index.
        /// </summary>
        /// <param name="mode">Current output mode.</param>
        /// <returns>Popup index.</returns>
        private static int ToOutputModeIndex(PsdImporter.OutputDirectoryMode mode)
        {
            return mode == PsdImporter.OutputDirectoryMode.AssetsRoot ? 1 : 0;
        }

        /// <summary>
        /// Converts popup index to output mode enum.
        /// </summary>
        /// <param name="index">Popup index.</param>
        /// <returns>Output mode enum.</returns>
        private static PsdImporter.OutputDirectoryMode ToOutputMode(int index)
        {
            return index == 1 ? PsdImporter.OutputDirectoryMode.AssetsRoot : PsdImporter.OutputDirectoryMode.PsdDirectory;
        }

        /// <summary>
        /// Converts prefab mode enum to popup index.
        /// </summary>
        /// <param name="mode">Current prefab mode.</param>
        /// <returns>Popup index.</returns>
        private static int ToPrefabModeIndex(PsdImporter.PrefabOutputMode mode)
        {
            return mode == PsdImporter.PrefabOutputMode.InsideOutputFolder ? 1 : 0;
        }

        /// <summary>
        /// Converts popup index to prefab mode enum.
        /// </summary>
        /// <param name="index">Popup index.</param>
        /// <returns>Prefab mode enum.</returns>
        private static PsdImporter.PrefabOutputMode ToPrefabMode(int index)
        {
            return index == 1 ? PsdImporter.PrefabOutputMode.InsideOutputFolder : PsdImporter.PrefabOutputMode.SiblingToOutputFolder;
        }

        /// <summary>
        /// Gets localized options for output directory mode popup.
        /// </summary>
        /// <returns>Localized options array.</returns>
        private static GUIContent[] GetOutputModeOptions()
        {
            return new[]
            {
                LocalizedContent(
                    "与 PSD 同目录",
                    "Same Directory As PSD",
                    "在 PSD 所在目录创建输出文件夹。",
                    "Create the output folder in the same directory as the PSD."),
                LocalizedContent(
                    "Assets 根目录",
                    "Assets Root",
                    "在项目 Assets 根目录创建输出文件夹。",
                    "Create the output folder under the project's Assets root.")
            };
        }

        /// <summary>
        /// Gets localized options for prefab output mode popup.
        /// </summary>
        /// <returns>Localized options array.</returns>
        private static GUIContent[] GetPrefabModeOptions()
        {
            return new[]
            {
                LocalizedContent(
                    "输出文件夹同级（默认）",
                    "Sibling To Output Folder (Default)",
                    "Prefab 与输出文件夹平级，便于按资源和预制体分开管理。",
                    "Save prefab next to the output folder for cleaner separation of generated assets and prefabs."),
                LocalizedContent(
                    "输出文件夹内部",
                    "Inside Output Folder",
                    "Prefab 放在输出文件夹内部，便于打包分发单个目录。",
                    "Save prefab inside the output folder to package and distribute a single directory.")
            };
        }

        /// <summary>
        /// Localizes text based on current inspector language.
        /// </summary>
        /// <param name="chinese">Chinese text.</param>
        /// <param name="english">English text.</param>
        /// <returns>Localized text.</returns>
        private static string Localize(string chinese, string english)
        {
            return CurrentLanguage == InspectorLanguage.English ? english : chinese;
        }

        /// <summary>
        /// Creates localized GUI content with tooltip.
        /// </summary>
        /// <param name="chineseText">Chinese text.</param>
        /// <param name="englishText">English text.</param>
        /// <param name="chineseTooltip">Chinese tooltip.</param>
        /// <param name="englishTooltip">English tooltip.</param>
        /// <returns>Localized GUI content.</returns>
        private static GUIContent LocalizedContent(string chineseText, string englishText, string chineseTooltip, string englishTooltip)
        {
            return new GUIContent(Localize(chineseText, englishText), Localize(chineseTooltip, englishTooltip));
        }

        /// <summary>
        /// Ensures the header GUI style exists even during editor initialization edge cases.
        /// </summary>
        private void EnsureHeaderStyle()
        {
            if (guiStyle != null)
            {
                return;
            }

            GUIStyle baseStyle = EditorStyles.label;
            guiStyle = baseStyle != null ? new GUIStyle(baseStyle) : new GUIStyle();
            guiStyle.richText = true;
            guiStyle.fontSize = 14;
        }

        /// <summary>
        /// Finds a scene canvas by hierarchy path.
        /// </summary>
        /// <param name="path">Hierarchy path in the form "Root/Child".</param>
        /// <returns>Matching canvas if found; otherwise null.</returns>
        private static UnityEngine.Canvas FindCanvasByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            UnityEngine.Canvas[] canvases = FindAllCanvases();
            foreach (UnityEngine.Canvas canvas in canvases)
            {
                if (GetHierarchyPath(canvas.transform) == path)
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
        private static UnityEngine.Canvas[] FindAllCanvases()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<UnityEngine.Canvas>(FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<UnityEngine.Canvas>();
#endif
        }

        /// <summary>
        /// Builds a hierarchy path for a transform in the form "Root/Child/SubChild".
        /// </summary>
        /// <param name="transform">Target transform.</param>
        /// <returns>Hierarchy path string.</returns>
        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}

