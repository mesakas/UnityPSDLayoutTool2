namespace PsdLayoutTool2.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using UnityEditor;
    using UnityEditor.AssetImporters;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Run in a disposable Unity project with the plugin installed. No test-framework package required.
    /// </summary>
    public static class PsdInspectorRegressionTests
    {
        public static void Run()
        {
            string folder = "Assets/PSDInspectorRegression_" + Guid.NewGuid().ToString("N");
            Object[] previousSelection = Selection.objects;
            try
            {
                Directory.CreateDirectory(folder);
                CreateFixtures(folder);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                string png = folder + "/image.png";
                string jpg = folder + "/image.jpg";
                string psd = folder + "/image.psd";
                string uppercasePsd = folder + "/uppercase.PSD";

                CheckApplyAndRevert(png);
                CheckApplyAndRevert(jpg);
                CheckApplyAndRevert(psd);
                CheckApplyAndRevert(uppercasePsd);
                CheckApplyAndRevert(png, jpg);
                CheckApplyAndRevert(psd, png);
                CheckApplyAndRevert(psd, uppercasePsd);
                CheckUnrelatedAsset(folder);
                Debug.Log("PSD_INSPECTOR_REGRESSION_PASS Unity " + Application.unityVersion);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
            finally
            {
                Selection.objects = previousSelection;
                ActiveEditorTracker.sharedTracker.ForceRebuild();
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static void CheckApplyAndRevert(params string[] paths)
        {
            AssetImporterEditor editor = SelectImporters(paths);
            Require(editor.targets.Length == paths.Length, "Multi-selection lost importer targets.");
            PropertyInfo needsApplyRevert = typeof(AssetImporterEditor).GetProperty(
                "needsApplyRevert", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(needsApplyRevert != null && (bool)needsApplyRevert.GetValue(editor),
                "Apply/Revert would be hidden for " + string.Join(", ", paths));

            // Exercise the same serialized setting and save/discard methods as the native Inspector.
            string[] originalMeta = paths.Select(path => File.ReadAllText(path + ".meta")).ToArray();
            SetTextureType(editor, TextureImporterType.Sprite);
            Require(editor.HasModified(), "Texture Type change was not tracked.");
            Require(paths.Select((path, index) => File.ReadAllText(path + ".meta") == originalMeta[index]).All(x => x),
                "Unapplied changes were written to disk.");
            editor.DiscardChanges();
            Require(!editor.HasModified(), "Revert did not discard the pending change.");
            Require(editor.targets.Cast<TextureImporter>().All(x => x.textureType == TextureImporterType.Default),
                "Revert did not restore the original texture type.");

            SetTextureType(editor, TextureImporterType.Sprite);
            editor.SaveChanges();
            foreach (string path in paths)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Require(((TextureImporter)AssetImporter.GetAtPath(path)).textureType == TextureImporterType.Sprite,
                    "Applied Sprite setting did not survive reimport: " + path);
                Require(AssetDatabase.LoadAssetAtPath<Sprite>(path) != null, "Sprite was not generated: " + path);
            }

            editor = SelectImporters(paths);
            SetTextureType(editor, TextureImporterType.Default);
            editor.SaveChanges();
            foreach (string path in paths)
            {
                Require(((TextureImporter)AssetImporter.GetAtPath(path)).textureType == TextureImporterType.Default,
                    "Changing back to Default failed: " + path);
            }
            Debug.Log("PASS native Inspector, Apply/Revert and reimport: " + string.Join(", ", paths));
        }

        private static AssetImporterEditor SelectImporters(string[] paths)
        {
            Selection.objects = paths.Select(AssetDatabase.LoadMainAssetAtPath).ToArray();
            ActiveEditorTracker.sharedTracker.ForceRebuild();
            Editor editor = ActiveEditorTracker.sharedTracker.activeEditors.FirstOrDefault(x => x.target is TextureImporter);
            Require(editor != null, "Unity did not create a texture importer Inspector.");
            Require(editor.GetType().FullName == "UnityEditor.TextureImporterInspector",
                "Plugin replaced the native texture Inspector: " + editor.GetType().FullName);
            return (AssetImporterEditor)editor;
        }

        private static void SetTextureType(AssetImporterEditor editor, TextureImporterType type)
        {
            editor.serializedObject.Update();
            editor.serializedObject.FindProperty("m_TextureType").intValue = (int)type;
            if (type == TextureImporterType.Sprite)
            {
                // Match the native Texture Type dropdown, which initializes Sprite Mode to Single.
                editor.serializedObject.FindProperty("m_SpriteMode").intValue = (int)SpriteImportMode.Single;
            }
            editor.serializedObject.ApplyModifiedProperties();
        }

        [MenuItem("Tools/PSD Layout Tool 2/Prepare Inspector Test Assets")]
        public static void PrepareInteractiveFixtures()
        {
            string folder = "Assets/PSDInspectorManual_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder);
            CreateFixtures(folder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(folder + "/image.psd");
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log("Inspector test assets: " + folder);
        }

        private static void CheckUnrelatedAsset(string folder)
        {
            string path = folder + "/unrelated.txt";
            File.WriteAllText(path, "Unrelated asset");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(path);
            ActiveEditorTracker.sharedTracker.ForceRebuild();
            Require(ActiveEditorTracker.sharedTracker.activeEditors.All(x => x.GetType().Namespace != "PsdLayoutTool2"),
                "Plugin took over an unrelated Inspector.");
        }

        private static void CreateFixtures(string folder)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.red, 16).ToArray());
                texture.Apply();
                File.WriteAllBytes(folder + "/image.png", texture.EncodeToPNG());
                File.WriteAllBytes(folder + "/image.jpg", texture.EncodeToJPG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            // A real 4x4 RGB PSD: big-endian header, empty optional sections, raw planar pixels.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 56, 66, 80, 83, 0, 1 });
                writer.Write(new byte[6]);
                writer.Write(new byte[] { 0, 3, 0, 0, 0, 4, 0, 0, 0, 4, 0, 8, 0, 3 });
                writer.Write(new byte[14]);
                writer.Write(Enumerable.Repeat((byte)255, 16).ToArray());
                writer.Write(new byte[32]);
                File.WriteAllBytes(folder + "/image.psd", stream.ToArray());
                File.WriteAllBytes(folder + "/uppercase.PSD", stream.ToArray());
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
