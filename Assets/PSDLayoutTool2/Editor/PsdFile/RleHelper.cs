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
            DecodedRow(stream, imgData, startIdx, columns, -1);
        }

        /// <summary>
        /// Reads a row of data from an RLE using the row byte count from the PSD row table.
        /// </summary>
        /// <param name="stream">The stream containing the data</param>
        /// <param name="imgData">The output image data</param>
        /// <param name="startIdx">The starting index</param>
        /// <param name="columns">The number of columns</param>
        /// <param name="rowByteCount">The number of compressed bytes in this row</param>
        public static void DecodedRow(Stream stream, byte[] imgData, int startIdx, int columns, int rowByteCount)
        {
            if (imgData == null)
            {
                throw new InvalidDataException("RLE output buffer is null.");
            }

            if (startIdx < 0 || columns < 0 || startIdx > imgData.Length || columns > imgData.Length - startIdx)
            {
                throw new InvalidDataException("RLE row exceeds the output buffer.");
            }

            bool hasRowByteCount = rowByteCount >= 0;
            long rowEnd = -1L;
            if (hasRowByteCount)
            {
                rowEnd = stream.Position + rowByteCount;
                if (rowEnd > stream.Length)
                {
                    throw new EndOfStreamException("PSD RLE row byte count extends past the available data.");
                }
            }

            int written = 0;
            while (written < columns)
            {
                int marker = ReadRowByte(stream, hasRowByteCount, rowEnd, "row marker");

                if (marker == 128)
                {
                    continue;
                }

                if (marker < 128)
                {
                    int count = marker + 1;
                    EnsureCompressedBytesAvailable(stream, hasRowByteCount, rowEnd, count, "literal bytes");
                    if (!hasRowByteCount)
                    {
                        EnsureRowCapacity(written, count, columns);
                    }

                    for (int index = 0; index < count; ++index)
                    {
                        int value = stream.ReadByte();
                        if (value < 0)
                        {
                            throw new EndOfStreamException("Unexpected end of PSD RLE data while reading literal bytes.");
                        }

                        if (written < columns)
                        {
                            imgData[startIdx + written] = (byte)value;
                            ++written;
                        }
                    }

                    continue;
                }

                int repeatCount = 257 - marker;
                if (!hasRowByteCount)
                {
                    EnsureRowCapacity(written, repeatCount, columns);
                }

                int repeatValue = ReadRowByte(stream, hasRowByteCount, rowEnd, "repeated byte");
                int bytesToWrite = hasRowByteCount ? Min(repeatCount, columns - written) : repeatCount;
                for (int index = 0; index < bytesToWrite; ++index)
                {
                    imgData[startIdx + written] = (byte)repeatValue;
                    ++written;
                }
            }

            if (hasRowByteCount)
            {
                stream.Position = rowEnd;
            }
        }

        /// <summary>
        /// Reads the PSD RLE row byte counts table.
        /// </summary>
        /// <param name="reader">The reader containing the row byte counts.</param>
        /// <param name="rows">The number of rows in the channel.</param>
        /// <returns>The compressed byte count for each row.</returns>
        public static int[] ReadRowByteCounts(BinaryReverseReader reader, int rows)
        {
            int[] rowByteCounts = new int[rows];
            for (int i = 0; i < rows; i++)
            {
                rowByteCounts[i] = reader.ReadUInt16();
            }

            return rowByteCounts;
        }

        private static int ReadRowByte(Stream stream, bool hasRowByteCount, long rowEnd, string valueDescription)
        {
            if (hasRowByteCount && stream.Position >= rowEnd)
            {
                throw new InvalidDataException("PSD RLE row ended before reading the expected " + valueDescription + ".");
            }

            int value = stream.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException("Unexpected end of PSD RLE data while reading " + valueDescription + ".");
            }

            return value;
        }

        private static void EnsureCompressedBytesAvailable(Stream stream, bool hasRowByteCount, long rowEnd, int count, string valueDescription)
        {
            if (hasRowByteCount && stream.Position + count > rowEnd)
            {
                throw new InvalidDataException("PSD RLE row byte count ended while reading " + valueDescription + ".");
            }
        }

        private static void EnsureRowCapacity(int written, int count, int columns)
        {
            if (written + count > columns)
            {
                throw new InvalidDataException("PSD RLE row expands past the expected row length.");
            }
        }

        private static int Min(int value1, int value2)
        {
            return value1 < value2 ? value1 : value2;
        }
    }
}
