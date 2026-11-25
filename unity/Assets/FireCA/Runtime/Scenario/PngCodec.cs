using System;
using System.IO;
using System.IO.Compression;

namespace FireCA
{
    /// <summary>
    /// A small PNG reader and writer for fuel.png. The reader returns the raw red channel bytes,
    /// with no color-space conversion. The writer produces an 8-bit grayscale image.
    /// </summary>
    public static class PngCodec
    {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        static uint Crc(uint crc, byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static uint ReadUInt32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

        static void WriteUInt32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        /// <summary>Reads the width and the height from the IHDR chunk.</summary>
        public static void ReadSize(byte[] png, out int width, out int height)
        {
            if (png == null || png.Length < 33) throw new FormatException("The file is too short to be a PNG.");
            for (int i = 0; i < 8; i++)
                if (png[i] != Signature[i]) throw new FormatException("The file has no PNG signature.");
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
                throw new FormatException("The PNG has no IHDR chunk.");
            width = (int)ReadUInt32(png, 16);
            height = (int)ReadUInt32(png, 20);
        }

        /// <summary>
        /// Decodes the red channel of an 8-bit PNG. Row 0 of the result is the top row of the image,
        /// or the bottom row when <paramref name="flipRows"/> is true.
        /// </summary>
        public static byte[] DecodeRed(byte[] png, bool flipRows, out int width, out int height)
        {
            ReadSize(png, out width, out height);
            int bitDepth = png[24], colorType = png[25], interlace = png[28];
            if (bitDepth != 8) throw new FormatException($"The PNG bit depth is {bitDepth}. The bit depth must be 8.");
            if (interlace != 0) throw new FormatException("The PNG is interlaced. The PNG must not be interlaced.");
            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break;   // gray
                case 2: channels = 3; break;   // RGB
                case 3: channels = 1; break;   // palette
                case 4: channels = 2; break;   // gray + alpha
                case 6: channels = 4; break;   // RGBA
                default: throw new FormatException($"The PNG color type {colorType} is not valid.");
            }

            byte[] paletteRed = null;
            using var idat = new MemoryStream(png.Length);
            int pos = 8;
            while (pos + 12 <= png.Length)
            {
                int length = (int)ReadUInt32(png, pos);
                int type = pos + 4, data = pos + 8;
                if (length < 0 || data + length + 4 > png.Length) throw new FormatException("A PNG chunk is truncated.");
                if (png[type] == 'I' && png[type + 1] == 'D' && png[type + 2] == 'A' && png[type + 3] == 'T')
                {
                    idat.Write(png, data, length);
                }
                else if (png[type] == 'P' && png[type + 1] == 'L' && png[type + 2] == 'T' && png[type + 3] == 'E')
                {
                    paletteRed = new byte[256];
                    for (int i = 0; i < length / 3 && i < 256; i++) paletteRed[i] = png[data + i * 3];
                }
                else if (png[type] == 'I' && png[type + 1] == 'E' && png[type + 2] == 'N' && png[type + 3] == 'D')
                {
                    break;
                }
                pos = data + length + 4;
            }
            if (colorType == 3 && paletteRed == null) throw new FormatException("The PNG has no palette.");
            if (idat.Length < 6) throw new FormatException("The PNG has no image data.");

            int stride = width * channels;
            var raw = new byte[(long)height * (stride + 1)];
            idat.Position = 2; // skip the zlib header
            using (var inflate = new DeflateStream(idat, CompressionMode.Decompress))
            {
                int total = 0;
                while (total < raw.Length)
                {
                    int n = inflate.Read(raw, total, raw.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total != raw.Length) throw new FormatException("The PNG image data is truncated.");
            }

            var red = new byte[width * height];
            int bpp = channels;
            for (int y = 0; y < height; y++)
            {
                int row = y * (stride + 1) + 1;
                int prev = row - (stride + 1);
                int filter = raw[row - 1];
                switch (filter)
                {
                    case 0: break;
                    case 1:
                        for (int i = bpp; i < stride; i++) raw[row + i] = (byte)(raw[row + i] + raw[row + i - bpp]);
                        break;
                    case 2:
                        if (y > 0) for (int i = 0; i < stride; i++) raw[row + i] = (byte)(raw[row + i] + raw[prev + i]);
                        break;
                    case 3:
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? raw[row + i - bpp] : 0;
                            int b = y > 0 ? raw[prev + i] : 0;
                            raw[row + i] = (byte)(raw[row + i] + ((a + b) >> 1));
                        }
                        break;
                    case 4:
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? raw[row + i - bpp] : 0;
                            int b = y > 0 ? raw[prev + i] : 0;
                            int c = (i >= bpp && y > 0) ? raw[prev + i - bpp] : 0;
                            int p = a + b - c;
                            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                            raw[row + i] = (byte)(raw[row + i] + pred);
                        }
                        break;
                    default: throw new FormatException($"The PNG filter type {filter} is not valid.");
                }

                int outRow = (flipRows ? height - 1 - y : y) * width;
                if (colorType == 3)
                    for (int x = 0; x < width; x++) red[outRow + x] = paletteRed[raw[row + x]];
                else if (channels == 1)
                    Buffer.BlockCopy(raw, row, red, outRow, width);
                else
                    for (int x = 0; x < width; x++) red[outRow + x] = raw[row + x * channels];
            }
            return red;
        }

        /// <summary>
        /// Encodes an 8-bit grayscale PNG. Row 0 of <paramref name="gray"/> is the top row of the image,
        /// or the bottom row when <paramref name="flipRows"/> is true.
        /// </summary>
        public static byte[] EncodeGray(byte[] gray, int width, int height, bool flipRows)
        {
            if (gray.Length != width * height) throw new ArgumentException("The array size does not match the image size.");
            using var compressed = new MemoryStream();
            compressed.WriteByte(0x78);
            compressed.WriteByte(0x9C);
            uint s1 = 1, s2 = 0;
            using (var deflate = new DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                var line = new byte[width + 1];
                for (int y = 0; y < height; y++)
                {
                    int src = (flipRows ? height - 1 - y : y) * width;
                    line[0] = 0;
                    Buffer.BlockCopy(gray, src, line, 1, width);
                    deflate.Write(line, 0, line.Length);
                    for (int i = 0; i < line.Length; i++)
                    {
                        s1 += line[i]; if (s1 >= 65521) s1 -= 65521;
                        s2 += s1; if (s2 >= 65521) s2 -= 65521;
                    }
                }
            }
            WriteUInt32(compressed, (s2 << 16) | s1);

            using var png = new MemoryStream();
            png.Write(Signature, 0, 8);
            var ihdr = new byte[13];
            ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
            ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 0;   // grayscale
            WriteChunk(png, "IHDR", ihdr, ihdr.Length);
            WriteChunk(png, "IDAT", compressed.GetBuffer(), (int)compressed.Length);
            WriteChunk(png, "IEND", Array.Empty<byte>(), 0);
            return png.ToArray();
        }

        static void WriteChunk(Stream s, string type, byte[] data, int length)
        {
            WriteUInt32(s, (uint)length);
            var tag = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(tag, 0, 4);
            s.Write(data, 0, length);
            uint crc = Crc(0xFFFFFFFFu, tag, 0, 4);
            crc = Crc(crc, data, 0, length) ^ 0xFFFFFFFFu;
            WriteUInt32(s, crc);
        }
    }
}
