namespace PhotoshopFile
{
    using System;
    using UnityEngine;

    /// <summary>
    /// Used to decode an image from a PSD layer.
    /// </summary>
    public static class ImageDecoder
    {
        /// <summary>
        /// Decodes a <see cref="Layer"/> into a <see cref="Texture2D"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to decode.</param>
        /// <returns>The <see cref="Texture2D"/> decoded from the layer.</returns>
        public static Texture2D DecodeImage(Layer layer)
        {
            if (layer.Rect.width == 0 || layer.Rect.height == 0)
            {
                return null;
            }

            Texture2D texture = new Texture2D((int)layer.Rect.width, (int)layer.Rect.height, TextureFormat.ARGB32, false);

            Color32[] colors = new Color32[(int)(layer.Rect.width * layer.Rect.height)];

            for (int y = 0; y < layer.Rect.height; ++y)
            {
                int layerRow = y * (int)layer.Rect.width;

                // we need to reverse the Y position for the Unity texture
                int textureRow = ((int)layer.Rect.height - 1 - y) * (int)layer.Rect.width;

                for (int x = 0; x < layer.Rect.width; ++x)
                {
                    int layerPosition = layerRow + x;
                    int texturePosition = textureRow + x;

                    colors[texturePosition] = GetColor(layer, layerPosition);

                    // set the alpha
                    if (layer.SortedChannels.ContainsKey(-2))
                    {
                        byte maskAlpha = GetColor(layer.MaskData, x, y);
                        int maskedAlpha = (colors[texturePosition].a * maskAlpha) / byte.MaxValue;
                        colors[texturePosition].a = (byte)maskedAlpha;
                    }

                    // apply layer opacity (0-255) from Photoshop.
                    int layeredAlpha = (colors[texturePosition].a * layer.Opacity) / byte.MaxValue;
                    colors[texturePosition].a = (byte)layeredAlpha;
                }
            }

            // PNG stores straight alpha. Bleed only the invisible RGB so linear
            // filtering cannot mix visible edge colors with transparent black.
            BleedTransparentPixels(colors, (int)layer.Rect.width, (int)layer.Rect.height);
            texture.SetPixels32(colors);
            return texture;
        }

        /// <summary>
        /// Extends the nearest visible RGB into fully transparent texels without
        /// changing coverage, semi-transparent artwork, or Photoshop shadows.
        /// </summary>
        internal static void BleedTransparentPixels(Color32[] colors, int width, int height)
        {
            if (colors == null) throw new ArgumentNullException(nameof(colors));
            if (width <= 0 || height <= 0 || (long)width * height != colors.Length)
                throw new ArgumentException("Pixel dimensions do not match the color buffer.");

            bool hasVisible = false, hasTransparent = false;
            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i].a == 0) hasTransparent = true;
                else hasVisible = true;
                if (hasVisible && hasTransparent) break;
            }
            // No seed exists for an empty layer; an opaque layer needs no padding.
            if (!hasVisible || !hasTransparent) return;

            // Two raster passes compute nearest sources in Manhattan distance.
            // O(width * height), with one int per pixel rather than a queue plus
            // distance/visited buffers or a whole-image scan for each new ring.
            var nearest = new int[colors.Length];
            for (int y = 0, index = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++, index++)
                {
                    if (colors[index].a != 0) { nearest[index] = index; continue; }
                    int source = -1, distance = int.MaxValue;
                    if (x > 0) SelectCloserSource(nearest[index - 1], x, y, width, ref source, ref distance);
                    if (y > 0) SelectCloserSource(nearest[index - width], x, y, width, ref source, ref distance);
                    nearest[index] = source;
                }
            }
            for (int y = height - 1, index = colors.Length - 1; y >= 0; y--)
            {
                for (int x = width - 1; x >= 0; x--, index--)
                {
                    if (colors[index].a != 0) continue;
                    int source = -1, distance = int.MaxValue;
                    SelectCloserSource(nearest[index], x, y, width, ref source, ref distance);
                    if (x + 1 < width) SelectCloserSource(nearest[index + 1], x, y, width, ref source, ref distance);
                    if (y + 1 < height) SelectCloserSource(nearest[index + width], x, y, width, ref source, ref distance);
                    nearest[index] = source;
                    // Every selected source is an original nonzero-alpha pixel.
                    // Transparent writes therefore cannot become new color seeds.
                    if (source >= 0)
                    {
                        var color = colors[source];
                        color.a = 0;
                        colors[index] = color;
                    }
                }
            }
        }

        private static void SelectCloserSource(int candidate, int x, int y, int width, ref int source, ref int distance)
        {
            if (candidate < 0) return;
            int candidateDistance = Math.Abs(x - candidate % width) + Math.Abs(y - candidate / width);
            if (candidateDistance < distance || (candidateDistance == distance && candidate < source))
            {
                source = candidate;
                distance = candidateDistance;
            }
        }

        /// <summary>
        /// Gets the color at the given position in the given <see cref="Layer"/>.
        /// </summary>
        /// <param name="layer">The <see cref="Layer"/> to sample.</param>
        /// <param name="position">The position to sample.</param>
        /// <returns>The sampled color.</returns>
        private static Color32 GetColor(Layer layer, int position)
        {
            Color32 baseColor = new Color32(1, 1, 1, byte.MaxValue);
            switch (layer.PsdFile.ColorMode)
            {
                case ColorModes.Grayscale:
                case ColorModes.Duotone:
                    baseColor = new Color32(layer.SortedChannels[0].ImageData[position], layer.SortedChannels[0].ImageData[position], layer.SortedChannels[0].ImageData[position], byte.MaxValue);
                    break;
                case ColorModes.Indexed:
                    int index = layer.SortedChannels[0].ImageData[position];
                    baseColor = new Color32(layer.PsdFile.ColorModeData[index], layer.PsdFile.ColorModeData[index + 256], layer.PsdFile.ColorModeData[index + 512], byte.MaxValue);
                    break;
                case ColorModes.RGB:
                    baseColor = new Color32(layer.SortedChannels[0].ImageData[position], layer.SortedChannels[1].ImageData[position], layer.SortedChannels[2].ImageData[position], byte.MaxValue);
                    break;
                case ColorModes.CMYK:
                    baseColor = CMYKToRGB(layer.SortedChannels[0].ImageData[position], layer.SortedChannels[1].ImageData[position], layer.SortedChannels[2].ImageData[position], layer.SortedChannels[3].ImageData[position]);
                    break;
                case ColorModes.Multichannel:
                    baseColor = CMYKToRGB(layer.SortedChannels[0].ImageData[position], layer.SortedChannels[1].ImageData[position], layer.SortedChannels[2].ImageData[position], 0);
                    break;
                case ColorModes.Lab:
                    baseColor = LabToRGB(layer.SortedChannels[0].ImageData[position], layer.SortedChannels[1].ImageData[position], layer.SortedChannels[2].ImageData[position]);
                    break;
            }

            // set the alpha
            if (layer.SortedChannels.ContainsKey(-1))
            {
                baseColor.a = layer.SortedChannels[-1].ImageData[position];
            }

            return baseColor;
        }

        /// <summary>
        /// Gets the color at the given pixel position in the given mask.
        /// </summary>
        /// <param name="mask">The mask to sample.</param>
        /// <param name="x">The x position.</param>
        /// <param name="y">The y position.</param>
        /// <returns>The mask color.</returns>
        private static byte GetColor(Mask mask, int x, int y)
        {
            byte num = byte.MaxValue;
            if (mask.PositionIsRelative)
            {
                x -= (int)mask.Rect.x;
                y -= (int)mask.Rect.y;
            }
            else
            {
                x = x + (int)mask.Layer.Rect.x - (int)mask.Rect.x;
                y = y + (int)mask.Layer.Rect.y - (int)mask.Rect.y;
            }

            if (y >= 0 && (y < mask.Rect.height && x >= 0) && x < mask.Rect.width)
            {
                int index = (y * (int)mask.Rect.width) + x;
                num = index >= mask.ImageData.Length ? byte.MaxValue : mask.ImageData[index];
            }

            return num;
        }

        /// <summary>
        /// Converts Lab color to RGB color.
        /// </summary>
        /// <param name="lb">The lb channel.</param>
        /// <param name="ab">The ab channel.</param>
        /// <param name="bb">The bb channel.</param>
        /// <returns>The RGB color.</returns>
        private static Color32 LabToRGB(byte lb, byte ab, byte bb)
        {
            double num1 = lb;
            double num2 = ab;
            double num3 = bb;
            double num4 = 2.56;
            double num5 = 1.0;
            double num6 = 1.0;
            int num7 = (int)(num1 / num4);
            int num8 = (int)((num2 / num5) - 128.0);
            int num9 = (int)((num3 / num6) - 128.0);
            double x1 = (num7 + 16.0) / 116.0;
            double x2 = (num8 / 500.0) + x1;
            double x3 = x1 - (num9 / 200.0);
            double num10 = Math.Pow(x1, 3.0) <= 0.008856 ? (x1 - 0.0) / 7.787 : Math.Pow(x1, 3.0);
            double num11 = Math.Pow(x2, 3.0) <= 0.008856 ? (x2 - 0.0) / 7.787 : Math.Pow(x2, 3.0);
            double num12 = Math.Pow(x3, 3.0) <= 0.008856 ? (x3 - 0.0) / 7.787 : Math.Pow(x3, 3.0);
            return XYZToRGB(95.047 * num11, 100.0 * num10, 108.883 * num12);
        }

        /// <summary>
        /// Converts XYZ color to RGB color.
        /// </summary>
        /// <param name="x">The x channel.</param>
        /// <param name="y">The y channel.</param>
        /// <param name="z">The z channel.</param>
        /// <returns>The RGB color.</returns>
        private static Color32 XYZToRGB(double x, double y, double z)
        {
            double num1 = x / 100.0;
            double num2 = y / 100.0;
            double num3 = z / 100.0;

            double x1 = (num1 * 3.2406) + (num2 * -1.5372) + (num3 * -0.4986);
            double x2 = (num1 * -0.9689) + (num2 * 1.8758) + (num3 * 0.0415);
            double x3 = (num1 * 0.0557) + (num2 * -0.204) + (num3 * 1.057);

            double num4 = x1 <= 0.0031308 ? 12.92 * x1 : (1.055 * Math.Pow(x1, 5.0 / 12.0)) - 0.055;
            double num5 = x2 <= 0.0031308 ? 12.92 * x2 : (1.055 * Math.Pow(x2, 5.0 / 12.0)) - 0.055;
            double num6 = x3 <= 0.0031308 ? 12.92 * x3 : (1.055 * Math.Pow(x3, 5.0 / 12.0)) - 0.055;

            byte red = (byte)(num4 * 256.0);
            byte green = (byte)(num5 * 256.0);
            byte blue = (byte)(num6 * 256.0);

            if (red > byte.MaxValue)
            {
                red = byte.MaxValue;
            }

            if (green > byte.MaxValue)
            {
                green = byte.MaxValue;
            }

            if (blue > byte.MaxValue)
            {
                blue = byte.MaxValue;
            }

            return new Color32(red, green, blue, byte.MaxValue);
        }

        /// <summary>
        /// Converts CMYK color to RGB color.
        /// </summary>
        /// <param name="c">The c channel.</param>
        /// <param name="m">The m channel.</param>
        /// <param name="y">The y channel.</param>
        /// <param name="k">The k channel.</param>
        /// <returns>The RGB color.</returns>
        private static Color32 CMYKToRGB(byte c, byte m, byte y, byte k)
        {
            double num1 = Math.Pow(2.0, 8.0);
            double num6 = 1.0 - (c / num1);
            double num7 = 1.0 - (m / num1);
            double num8 = 1.0 - (y / num1);
            double num9 = 1.0 - (k / num1);

            byte red = (byte)((1.0 - ((num6 * (1.0 - num9)) + num9)) * byte.MaxValue);
            byte green = (byte)((1.0 - ((num7 * (1.0 - num9)) + num9)) * byte.MaxValue);
            byte blue = (byte)((1.0 - ((num8 * (1.0 - num9)) + num9)) * byte.MaxValue);

            if (red > byte.MaxValue)
            {
                red = byte.MaxValue;
            }

            if (green > byte.MaxValue)
            {
                green = byte.MaxValue;
            }

            if (blue > byte.MaxValue)
            {
                blue = byte.MaxValue;
            }

            return new Color32(red, green, blue, byte.MaxValue);
        }
    }
}
