namespace PhotoshopFile
{
    using System.IO;

    /// <summary>
    /// Represents a set of helper methods for RLE.
    /// </summary>
    internal static class RleHelper
    {
        /// <summary>
        /// Reads a row of data from an RLE.
        /// </summary>
        /// <param name="stream">The stream containing the data</param>
        /// <param name="imgData">The output image data</param>
        /// <param name="startIdx">The starting index</param>
        /// <param name="columns">The number of columns</param>
        public static void DecodedRow(Stream stream, byte[] imgData, int startIdx, int columns)
        {
            if (imgData == null)
            {
                throw new InvalidDataException("RLE output buffer is null.");
            }

            if (startIdx < 0 || columns < 0 || startIdx + columns > imgData.Length)
            {
                throw new InvalidDataException("RLE row exceeds the output buffer.");
            }

            int written = 0;
            while (written < columns)
            {
                int marker = stream.ReadByte();
                if (marker < 0)
                {
                    throw new EndOfStreamException("Unexpected end of PSD RLE data while reading a row marker.");
                }

                if (marker == 128)
                {
                    continue;
                }

                if (marker < 128)
                {
                    int count = marker + 1;
                    EnsureRowCapacity(written, count, columns);
                    for (int index = 0; index < count; ++index)
                    {
                        int value = stream.ReadByte();
                        if (value < 0)
                        {
                            throw new EndOfStreamException("Unexpected end of PSD RLE data while reading literal bytes.");
                        }

                        imgData[startIdx + written] = (byte)value;
                        ++written;
                    }

                    continue;
                }

                int repeatCount = 257 - marker;
                EnsureRowCapacity(written, repeatCount, columns);
                int repeatValue = stream.ReadByte();
                if (repeatValue < 0)
                {
                    throw new EndOfStreamException("Unexpected end of PSD RLE data while reading repeated byte.");
                }

                for (int index = 0; index < repeatCount; ++index)
                {
                    imgData[startIdx + written] = (byte)repeatValue;
                    ++written;
                }
            }
        }

        private static void EnsureRowCapacity(int written, int count, int columns)
        {
            if (written + count > columns)
            {
                throw new InvalidDataException("PSD RLE row expands past the expected row length.");
            }
        }
    }
}
