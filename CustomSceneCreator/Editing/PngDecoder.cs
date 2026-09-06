using System;
using System.IO;
using System.IO.Compression;

namespace CustomSceneCreator.Editing {
    /// <summary>Minimal 8-bit, non-interlaced PNG reader, adapted from HSR's picture pipeline.</summary>
    internal static class PngDecoder {
        public static byte[]? ReadRgba(string path, out int width, out int height, out string error) {
            width = 0; height = 0; error = "";
            try {
                if (!File.Exists(path)) { error = "No such file: " + path; return null; }
                byte[] file = File.ReadAllBytes(path);
                if (file.Length < 8 || file[0] != 0x89 || file[1] != 0x50 || file[2] != 0x4E || file[3] != 0x47) {
                    error = "Not a PNG: " + path; return null;
                }
                int bitDepth = 0, colorType = 0, interlace = 0;
                var idat = new MemoryStream(); int pos = 8;
                while (pos + 8 <= file.Length) {
                    int length = ReadInt(file, pos);
                    string type = "" + (char)file[pos + 4] + (char)file[pos + 5] + (char)file[pos + 6] + (char)file[pos + 7];
                    int dataAt = pos + 8;
                    if (length < 0 || dataAt + length > file.Length) { error = "Truncated PNG chunk '" + type + "'."; return null; }
                    if (type == "IHDR") { width = ReadInt(file, dataAt); height = ReadInt(file, dataAt + 4); bitDepth = file[dataAt + 8]; colorType = file[dataAt + 9]; interlace = file[dataAt + 12]; }
                    else if (type == "IDAT") idat.Write(file, dataAt, length);
                    else if (type == "IEND") break;
                    pos = dataAt + length + 4;
                }
                if (width <= 0 || height <= 0) { error = "PNG has no valid IHDR."; return null; }
                if (bitDepth != 8) { error = "Only 8-bit PNGs are supported."; return null; }
                if (interlace != 0) { error = "Interlaced PNGs are not supported."; return null; }
                int channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 6 ? 4 : 0;
                if (channels == 0) { error = "PNG must be greyscale, RGB, or RGBA."; return null; }
                byte[]? raw = Inflate(idat.ToArray(), height * (1 + width * channels));
                if (raw == null) { error = "Could not inflate PNG data."; return null; }
                return Unfilter(raw, width, height, channels, out error);
            } catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
        }
        private static int ReadInt(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        private static byte[]? Inflate(byte[] zlib, int expected) {
            if (zlib.Length < 3) return null;
            using var input = new MemoryStream(zlib, 2, zlib.Length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected > 0 ? expected : 0);
            var buffer = new byte[81920]; int read;
            while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
            return output.ToArray();
        }
        private static byte[]? Unfilter(byte[] raw, int width, int height, int channels, out string error) {
            error = ""; int stride = width * channels;
            if (raw.Length < height * (stride + 1)) { error = "PNG data is shorter than its header claims."; return null; }
            var lines = new byte[height * stride]; var prior = new byte[stride]; var line = new byte[stride];
            for (int y = 0; y < height; y++) {
                int at = y * (stride + 1), filter = raw[at]; Buffer.BlockCopy(raw, at + 1, line, 0, stride);
                for (int i = 0; i < stride; i++) {
                    int a = i >= channels ? line[i - channels] : 0, b = prior[i], c = i >= channels ? prior[i - channels] : 0, v = line[i];
                    if (filter == 1) v += a; else if (filter == 2) v += b; else if (filter == 3) v += (a + b) / 2;
                    else if (filter == 4) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
                    else if (filter != 0) { error = "Unknown PNG row filter " + filter + "."; return null; }
                    line[i] = (byte)v;
                }
                Buffer.BlockCopy(line, 0, lines, y * stride, stride); Buffer.BlockCopy(line, 0, prior, 0, stride);
            }
            var rgba = new byte[width * height * 4];
            for (int src = 0, dst = 0; dst < rgba.Length; src += channels, dst += 4) {
                if (channels == 1) rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = lines[src];
                else { rgba[dst] = lines[src]; rgba[dst + 1] = lines[src + 1]; rgba[dst + 2] = lines[src + 2]; }
                rgba[dst + 3] = channels == 4 ? lines[src + 3] : (byte)255;
            }
            return rgba;
        }
    }
}
