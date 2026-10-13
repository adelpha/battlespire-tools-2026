using System;
using System.Text;

namespace Battlespire.Formats
{
    public static class TextUtil
    {
        /// <summary>Byte-for-byte text encoding used for names and game text.</summary>
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static string ReadCString(byte[] buf, int offset, int max)
        {
            int n = 0;
            while (n < max && offset + n < buf.Length && buf[offset + n] != 0) n++;
            return Latin1.GetString(buf, offset, n);
        }

        public static string FourCC(byte[] buf, int offset)
        {
            if (offset + 4 > buf.Length) return "";
            return Latin1.GetString(buf, offset, 4);
        }
    }

    public enum FileKind
    {
        Unknown,
        Bsi,
        Model3D,
        Bs6,
        Wave,
        Flic,
        Text,
        Executable
    }

    /// <summary>Guesses what kind of data a file holds from its contents and name.</summary>
    public static class FileSniffer
    {
        public static FileKind Detect(string name, byte[] data)
        {
            string ext = name == null ? "" : System.IO.Path.GetExtension(name).ToUpperInvariant();
            if (data.Length >= 8)
            {
                string magic = TextUtil.FourCC(data, 0);
                if (magic == "BSIF" || magic == "IFHD" || magic == "BHDR") return FileKind.Bsi;
                if (magic.StartsWith("v2.") || magic.StartsWith("v3.")) return FileKind.Model3D;
                if (magic == "GNRL") return FileKind.Bs6;
                if (magic == "RIFF" && TextUtil.FourCC(data, 8) == "WAVE") return FileKind.Wave;
                if (data[0] == (byte)'M' && data[1] == (byte)'Z') return FileKind.Executable;
                ushort flic = BitConverter.ToUInt16(data, 4);
                if (flic == 0xAF11 || flic == 0xAF12) return FileKind.Flic;
            }
            if (ext == ".TXT" || ext == ".INI" || ext == ".CFG" || ext == ".BAT" || LooksLikeText(data))
                return FileKind.Text;
            return FileKind.Unknown;
        }

        public static bool LooksLikeText(byte[] data)
        {
            int n = Math.Min(data.Length, 512);
            if (n == 0) return false;
            int bad = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = data[i];
                if (b < 9 || (b > 13 && b < 32)) bad++;
            }
            return bad * 20 < n;
        }

        public static string GuessExtension(byte[] data)
        {
            switch (Detect(null, data))
            {
                case FileKind.Bsi: return ".BSI";
                case FileKind.Model3D: return ".3D";
                case FileKind.Bs6: return ".BS6";
                case FileKind.Wave: return ".WAV";
                case FileKind.Flic: return ".FLC";
                case FileKind.Text: return ".TXT";
                case FileKind.Executable: return ".EXE";
                default: return ".BIN";
            }
        }
    }
}
