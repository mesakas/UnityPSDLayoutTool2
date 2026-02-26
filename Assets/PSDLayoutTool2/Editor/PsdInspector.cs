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
        /// The native Unity editor used to render the <see cref="TextureImporter"/>'s Inspector.
        /// </summary>
        private Editor nativeEditor;

        /// <summary>
        /// The style used to draw the section header text.
        /// </summary>
        private GUIStyle guiStyle;

        /// <summary>
        /// Display names for output directory mode.
        /// </summary>
        private static readonly GUIContent[] OutputModeOptions =
        {
            new GUIContent("与 PSD 同目录", "在 PSD 所在目录创建输出文件夹。"),
            new GUIContent("Assets 根目录", "在项目 Assets 根目录创建输出文件夹。")
        };

        /// <summary>
        /// Display names for prefab output mode.
        /// </summary>
        private static readonly GUIContent[] PrefabModeOptions =
        {
            new GUIContent("输出文件夹同级（默认）", "Prefab 与输出文件夹平级，便于按资源和预制体分开管理。"),
            new GUIContent("输出文件夹内部", "Prefab 放在输出文件夹内部，便于打包分发单个目录。")
        };

        /// <summary>
        /// Called by Unity when any Texture file is first clicked on and the Inspector is populated.
        /// </summary>
        public void OnEnable()
        {
            // use reflection to get the default Inspector
            Type type = Type.GetType("UnityEditor.TextureImporterInspector, UnityEditor");
            nativeEditor = CreateEditor(target, type);

            // set up the GUI style for the section headers
            guiStyle = new GUIStyle(EditorStyles.label);
            guiStyle.richText = true;
            guiStyle.fontSize = 14;

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
        }

        /// <summary>
        /// Draws the Inspector GUI for the TextureImporter.
        /// Normal Texture files should appear as they normally do, however PSD files will have additional items.
        /// </summary>
        public override void OnInspectorGUI()
        {
            if (nativeEditor != null)
            {
                // check if it is a PSD file selected
                string assetPath = ((TextureImporter)target).assetPath;

                if (assetPath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase))
                {
                    GUILayout.Label("<b>PSD 布局工具 2</b>", guiStyle, GUILayout.Height(23));

                    GUIContent maximumDepthLabel = new GUIContent(
                        "最大深度（Z）",
                        "用于图层布局的最远 Z 值。导入时会从该值逐层递减到 0。\n数值越大，层与层之间的深度间隔越明显。");
                    PsdImporter.MaximumDepth = EditorGUILayout.FloatField(maximumDepthLabel, PsdImporter.MaximumDepth);

                    GUIContent pixelsToUnitsLabel = new GUIContent(
                        "像素到单位（PPU）",
                        "每多少像素对应 1 个 Unity 世界单位。\n通常建议与项目中 Sprite 的 Pixels Per Unit 保持一致（常见为 100）。");
                    PsdImporter.PixelsToUnits = EditorGUILayout.FloatField(pixelsToUnitsLabel, PsdImporter.PixelsToUnits);

                    GUIContent useUnityUILabel = new GUIContent(
                        "使用 Unity UI",
                        "开启后生成 Canvas/Image/Text/Button 等 UI 对象。\n关闭后生成 SpriteRenderer/TextMesh 等普通场景对象。");
                    PsdImporter.UseUnityUI = EditorGUILayout.Toggle(useUnityUILabel, PsdImporter.UseUnityUI);

                    EditorGUI.BeginChangeCheck();
                    GUIContent outputModeLabel = new GUIContent(
                        "资源输出位置",
                        "控制导出的 PNG、动画片段（.anim）和控制器（.controller）保存到哪里。");
                    int outputModeIndex = EditorGUILayout.Popup(outputModeLabel, ToOutputModeIndex(PsdImporter.OutputMode), OutputModeOptions);
                    PsdImporter.OutputMode = ToOutputMode(outputModeIndex);

                    GUIContent outputFolderNameLabel = new GUIContent(
                        "输出文件夹名",
                        "生成文件夹名称。留空时自动使用 PSD 文件名。\n可用于按模块命名，例如 UI_MainMenu、角色立绘等。");
                    PsdImporter.OutputFolderName = EditorGUILayout.TextField(outputFolderNameLabel, PsdImporter.OutputFolderName);

                    GUIContent prefabModeLabel = new GUIContent(
                        "Prefab 输出位置",
                        "控制 Generate Prefab 生成的预制体保存位置。");
                    int prefabModeIndex = EditorGUILayout.Popup(prefabModeLabel, ToPrefabModeIndex(PsdImporter.PrefabMode), PrefabModeOptions);
                    PsdImporter.PrefabMode = ToPrefabMode(prefabModeIndex);
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorPrefs.SetInt(OutputModePrefKey, (int)PsdImporter.OutputMode);
                        EditorPrefs.SetString(OutputFolderNamePrefKey, PsdImporter.OutputFolderName ?? string.Empty);
                        EditorPrefs.SetInt(PrefabModePrefKey, (int)PsdImporter.PrefabMode);
                    }

                    EditorGUILayout.HelpBox("提示：标签匹配不区分大小写。|Button 仅在启用 Unity UI 时生效，|Animation 仅在非 UI 模式生效。", MessageType.Info);

                    // draw our custom buttons for PSD files
                    if (GUILayout.Button("导出图层为纹理"))
                    {
                        PsdImporter.ExportLayersAsTextures(assetPath);
                    }

                    if (GUILayout.Button("在当前场景中布局"))
                    {
                        PsdImporter.LayoutInCurrentScene(assetPath);
                    }

                    if (GUILayout.Button("生成预制体"))
                    {
                        PsdImporter.GeneratePrefab(assetPath);
                    }

                    GUILayout.Space(3);

                    GUILayout.Box(string.Empty, GUILayout.Height(1), GUILayout.MaxWidth(Screen.width - 30));

                    GUILayout.Space(3);

                    GUILayout.Label("<b>Unity 纹理导入设置</b>", guiStyle, GUILayout.Height(23));

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
    }
}

