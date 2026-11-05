namespace PsdLayoutTool2.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using PhotoshopFile;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>Run in a disposable Unity project. No test-framework or game assembly required.</summary>
    public static class PsdTransparencyRegressionTests
    {
        private const int Width = 16;
        private const int Height = 16;

        public static void Run()
        {
            string id = Guid.NewGuid().ToString("N");
            string assets = "Assets/PSDTransparencyRegression_" + id;
            string fixtures = "Library/PSDTransparencyRegression_" + id;
            float previousPpu = PsdImporter.PixelsToUnits;
            try
            {
                Directory.CreateDirectory(assets);
                Directory.CreateDirectory(fixtures);
                CheckSolidEdges(fixtures, new Color32(255, 255, 255, 255), "white");
                CheckSolidEdges(fixtures, new Color32(219, 57, 137, 255), "colored");
                CheckAlphaAndShadows(fixtures);
                CheckNoAlphaChannel(fixtures);
                CheckMaskAndOpacity(fixtures);
                CheckFullyTransparent(fixtures);
                CheckSpriteReimport(fixtures, assets);
                Debug.Log("PSD_TRANSPARENCY_REGRESSION_PASS Unity " + Application.unityVersion);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
            finally
            {
                PsdImporter.PixelsToUnits = previousPpu;
                AssetDatabase.DeleteAsset(assets);
                // Both directories are unique fixtures created by this invocation.
                if (Directory.Exists(assets)) Directory.Delete(assets, true);
                if (Directory.Exists(fixtures)) Directory.Delete(fixtures, true);
            }
        }

        private static Color32[] Square(Color32 color)
        {
            var pixels = new Color32[Width * Height];
            for (int y = 4; y < 12; y++)
                for (int x = 4; x < 12; x++) pixels[y * Width + x] = color;
            return pixels;
        }

        private static void CheckSolidEdges(string folder, Color32 color, string name)
        {
            Color32[] input = Square(color);
            Texture2D texture = DecodeFixture(folder, name, input);
            try
            {
                CheckPixels(texture, input, true, 255, null, name);
                CheckBilinearEdge(texture, color, name);
                Debug.Log("PASS transparent black RGB does not darken the " + name + " bilinear edge.");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        private static void CheckBilinearEdge(Texture2D texture, Color32 source, string label)
        {
            texture.Apply(false, false);
            // Find a fractional coverage sample with Unity's actual bilinear sampler,
            // independently of whether the API maps UVs to texel centres or corners.
            Color edge = Color.clear;
            float best = 1;
            for (int i = 0; i <= 256; i++)
            {
                float u = (2.5f + i / 128f) / Width;
                Color candidate = texture.GetPixelBilinear(u, .5f);
                float distance = Mathf.Abs(candidate.a - .5f);
                if (distance < best) { best = distance; edge = candidate; }
            }
            Require(best < .025f, label + ": failed to sample a half-covered edge.");
            Color expectedColor = source;
            NearRgb(edge, expectedColor, label + ": transparent RGB contaminated interpolation.");
            foreach (float backdrop in new[] { 1f, .5f, .05f })
            {
                var background = new Color(backdrop, backdrop, backdrop, 1);
                Color actual = edge * edge.a + background * (1 - edge.a);
                Color expected = expectedColor * edge.a + background * (1 - edge.a);
                NearRgb(actual, expected, label + ": dark fringe on background " + backdrop);
            }
        }

        private static void CheckAlphaAndShadows(string folder)
        {
            var input = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                input[y * Width + 2] = new Color32(0, 0, 0, 1);
                input[y * Width + 3] = new Color32(0, 0, 0, 48);
                input[y * Width + 4] = new Color32(0, 0, 0, 128);
                input[y * Width + 8] = new Color32(230, 73, 21, 1);
                input[y * Width + 9] = new Color32(51, 147, 212, 96);
                input[y * Width + 10] = new Color32(192, 209, 27, 254);
                input[y * Width + 11] = new Color32(255, 255, 255, 255);
            }
            Texture2D texture = DecodeFixture(folder, "shadow-and-translucency", input);
            try { CheckPixels(texture, input, true, 255, null, "shadow-and-translucency"); }
            finally { Object.DestroyImmediate(texture); }
            Debug.Log("PASS every alpha byte and all visible RGB, including alpha=1 and black shadows, are unchanged.");
        }

        private static void CheckNoAlphaChannel(string folder)
        {
            var input = Enumerable.Range(0, Width * Height)
                .Select(i => new Color32((byte)(i % 251), 112, 214, 0)).ToArray();
            foreach (byte opacity in new byte[] { 255, 128 })
            {
                Texture2D texture = DecodeFixture(folder, "no-alpha-" + opacity, input, false, opacity);
                try { CheckPixels(texture, input, false, opacity, null, "no-alpha-" + opacity); }
                finally { Object.DestroyImmediate(texture); }
            }
            Debug.Log("PASS layers without an alpha channel default to 255 before layer opacity.");
        }

        private static void CheckMaskAndOpacity(string folder)
        {
            var input = Enumerable.Range(0, Width * Height)
                .Select(i => new Color32(215, (byte)(i % 251), 84, (byte)(i % 4 == 0 ? 0 : i % 4 == 1 ? 1 : i % 4 == 2 ? 128 : 255))).ToArray();
            var mask = Enumerable.Range(0, Width * Height).Select(i => (byte)(i % 3 == 0 ? 0 : i % 3 == 1 ? 128 : 255)).ToArray();
            Texture2D texture = DecodeFixture(folder, "mask-opacity", input, true, 128, mask);
            try { CheckPixels(texture, input, true, 128, mask, "mask-opacity"); }
            finally { Object.DestroyImmediate(texture); }
            Debug.Log("PASS layer alpha, mask alpha and opacity retain their original byte composition.");
        }

        private static void CheckFullyTransparent(string folder)
        {
            var input = Enumerable.Range(0, Width * Height).Select(i => new Color32((byte)i, 37, 205, 0)).ToArray();
            Texture2D texture = DecodeFixture(folder, "fully-transparent", input);
            try
            {
                CheckPixels(texture, input, true, 255, null, "fully-transparent");
                Color32[] actual = texture.GetPixels32();
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        Require(actual[(Height - 1 - y) * Width + x].Equals(input[y * Width + x]),
                            "Fully transparent image must remain unchanged when there are no color donors.");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        private static void CheckPixels(Texture2D texture, Color32[] input, bool hasAlpha, byte opacity, byte[] mask, string label)
        {
            Color32[] actual = texture.GetPixels32();
            Require(actual.Length == input.Length, label + ": dimensions changed.");
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int sourceIndex = y * Width + x;
                    Color32 source = input[sourceIndex];
                    Color32 output = actual[(Height - 1 - y) * Width + x];
                    int alpha = hasAlpha ? source.a : 255;
                    if (mask != null) alpha = alpha * mask[sourceIndex] / 255;
                    alpha = alpha * opacity / 255;
                    Require(output.a == alpha, label + ": alpha changed at pixel " + sourceIndex);
                    if (alpha > 0)
                        Require(output.r == source.r && output.g == source.g && output.b == source.b,
                            label + ": visible RGB changed at pixel " + sourceIndex);
                }
        }

        private static void CheckSpriteReimport(string fixtures, string assets)
        {
            string path = assets + "/edge.png";
            MethodInfo importSprite = typeof(PsdImporter).GetMethod("ImportSprite", BindingFlags.Static | BindingFlags.NonPublic);
            Require(importSprite != null, "Plugin sprite import entry point was not found.");
            string guid = null;
            PsdImporter.PixelsToUnits = 123;
            foreach (Color32 color in new[] { new Color32(255, 255, 255, 255), new Color32(219, 57, 137, 255) })
            {
                Texture2D decoded = DecodeFixture(fixtures, "reimport-" + color.r, Square(color));
                try { File.WriteAllBytes(path, decoded.EncodeToPNG()); }
                finally { Object.DestroyImmediate(decoded); }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                if (guid == null) guid = AssetDatabase.AssetPathToGUID(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Default;
                importer.alphaSource = TextureImporterAlphaSource.None;
                importer.alphaIsTransparency = false;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.isReadable = true;
                importer.SaveAndReimport();
                var sprite = (Sprite)importSprite.Invoke(null, new object[] { path, "TransparencyRegression" });
                importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Require(sprite, "Plugin did not generate a Sprite after reimport.");
                Require(AssetDatabase.AssetPathToGUID(path) == guid, "Reimport changed the existing asset GUID.");
                Require(importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Single,
                    "Reimport did not persist Sprite settings.");
                Require(importer.alphaSource == TextureImporterAlphaSource.FromInput && importer.alphaIsTransparency,
                    "Reimport retained stale alpha settings.");
                Require(importer.wrapMode == TextureWrapMode.Clamp && !importer.mipmapEnabled,
                    "Reimport retained wrapping or mipmaps that contaminate sprite edges.");
                Require(importer.textureCompression == TextureImporterCompression.Uncompressed,
                    "Reimport retained lossy texture compression.");
                Require(Mathf.Abs(importer.spritePixelsPerUnit - 123) < .01f, "Sprite PPU was not preserved.");
                Require(sprite.texture.width == Width && sprite.texture.height == Height, "Sprite dimensions changed.");
                CheckBilinearEdge(sprite.texture, color, "Unity imported PNG");
                Require(File.ReadAllText(path + ".meta").Contains(guid), "The existing GUID was not saved to the meta file.");
            }
            Debug.Log("PASS real Sprite reimport repairs stale settings, preserves GUID and exports clean white/colored edges.");
        }

        private static Texture2D DecodeFixture(string folder, string name, Color32[] pixels,
            bool alpha = true, byte opacity = 255, byte[] mask = null)
        {
            string path = folder + "/" + name + ".psd";
            WritePsd(path, pixels, alpha, opacity, mask);
            var psd = new PsdFile(path);
            Require(psd.Layers.Count == 1, "Fixture must decode through the real PSD layer reader.");
            Texture2D texture = ImageDecoder.DecodeImage(psd.Layers[0]);
            Require(texture, "Decoder returned no image.");
            return texture;
        }

        // Minimal valid 8-bit RGB PSD with one raw planar layer, optional alpha/mask,
        // and a merged RGB image. Inputs use Photoshop's top-left row order.
        private static void WritePsd(string path, Color32[] pixels, bool alpha, byte opacity, byte[] mask)
        {
            var ids = new List<short> { 0, 1, 2 };
            if (alpha) ids.Add(-1);
            if (mask != null) ids.Add(-2);
            byte[] extra = Bytes(writer =>
            {
                Big32(writer, mask == null ? 0 : 20);
                if (mask != null)
                {
                    Rect(writer); writer.Write((byte)255); writer.Write((byte)0); Big16(writer, 0);
                }
                Big32(writer, 0); // blending ranges
                writer.Write(new byte[] { 3, (byte)'f', (byte)'i', (byte)'x' });
            });
            byte[] layerInfo = Bytes(writer =>
            {
                Big16(writer, 1); Rect(writer); Big16(writer, ids.Count);
                foreach (short id in ids) { Big16(writer, id); Big32(writer, 2 + pixels.Length); }
                writer.Write(Encoding.ASCII.GetBytes("8BIMnorm"));
                writer.Write(opacity); writer.Write(new byte[3]);
                Big32(writer, extra.Length); writer.Write(extra);
                foreach (short id in ids)
                {
                    Big16(writer, 0); // uncompressed raw channel
                    for (int i = 0; i < pixels.Length; i++)
                        writer.Write(id == 0 ? pixels[i].r : id == 1 ? pixels[i].g : id == 2 ? pixels[i].b : id == -1 ? pixels[i].a : mask[i]);
                }
                if ((writer.BaseStream.Position & 1) != 0) writer.Write((byte)0);
            });
            byte[] layerAndMask = Bytes(writer => { Big32(writer, layerInfo.Length); writer.Write(layerInfo); Big32(writer, 0); });
            File.WriteAllBytes(path, Bytes(writer =>
            {
                writer.Write(Encoding.ASCII.GetBytes("8BPS")); Big16(writer, 1); writer.Write(new byte[6]);
                Big16(writer, 3); Big32(writer, Height); Big32(writer, Width); Big16(writer, 8); Big16(writer, 3);
                Big32(writer, 0); Big32(writer, 0); Big32(writer, layerAndMask.Length); writer.Write(layerAndMask);
                Big16(writer, 0);
                for (int channel = 0; channel < 3; channel++)
                    foreach (Color32 pixel in pixels) writer.Write(channel == 0 ? pixel.r : channel == 1 ? pixel.g : pixel.b);
            }));
        }

        private static byte[] Bytes(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) { write(writer); return stream.ToArray(); }
        }
        private static void Rect(BinaryWriter writer) { Big32(writer, 0); Big32(writer, 0); Big32(writer, Height); Big32(writer, Width); }
        private static void Big16(BinaryWriter writer, int value) { writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
        private static void Big32(BinaryWriter writer, int value)
        {
            writer.Write((byte)(value >> 24)); writer.Write((byte)(value >> 16)); writer.Write((byte)(value >> 8)); writer.Write((byte)value);
        }
        private static void NearRgb(Color actual, Color expected, string message)
        {
            Require(Mathf.Abs(actual.r - expected.r) <= 2f / 255 && Mathf.Abs(actual.g - expected.g) <= 2f / 255 &&
                Mathf.Abs(actual.b - expected.b) <= 2f / 255, message + " Expected " + expected + ", got " + actual);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
