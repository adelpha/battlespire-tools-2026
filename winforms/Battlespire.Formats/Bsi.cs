using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Battlespire.Formats
{
    public class BsiChunk
    {
        public string Tag;
        public int Offset;
        public int Length;
        public byte[] Data;
        public override string ToString() { return string.Format("{0} @0x{1:X} ({2} bytes)", Tag, Offset, Length); }
    }

    public enum BsiPalette
    {
        /// <summary>HICL: 128 colours stored as RGB555 shifted left one bit (R 15-11, G 10-6, B 5-1), indexed by pixel / 2. This is what the game draws with.</summary>
        Hicl,
        /// <summary>HTBL: 32 shade levels of the 128 colours, stored as RGB555 (level 31 is the brightest).</summary>
        Htbl,
        /// <summary>CMAP: 256 entry 6 bit VGA palette (shared by most images), indexed by the raw pixel.</summary>
        Cmap,
        /// <summary>Raw pixel values as greys.</summary>
        Greyscale
    }

    /// <summary>
    /// One image (BHDR + palette chunks + DATA) inside a BSI file.
    /// Field meanings come from LoadBsiImage (0x00015607) and the frame
    /// decoder at 0x00015f1b.
    /// </summary>
    public class BsiImage
    {
        public string Name;
        public byte[] Ifhd;

        // BHDR, 26 bytes, little endian
        public short XOffset;          // +0  default draw position
        public short YOffset;          // +2
        public ushort Width;           // +4
        public ushort Height;          // +6
        public ushort Flags;           // +8  bit 0: colour 0 is transparent (texture cache 0x73c6c); bit 1 set on all images
        public uint RuntimeData;       // +10 always 0 on disk; the loader stores its data pointer here
        public ushort Frames;          // +14
        public ushort FrameDelay;      // +16 timer ticks per frame; the game uses 0x50 when 0
        public ushort BlendMode;       // +18 0 opaque, 1 water, 2 translucent (ghosts, wraiths, ENERGY)
        public ushort ColourSlot;      // +20 copied into the texture cache entry; 0 (almost always) means the game assigns one
        public short Scale;            // +22 sprite size in world units per texel, x/256 (256 = 1.0); added to the placement's own scale (0x92021)
        public ushort Compression;     // +24 0 raw, 1 RLE, 2 skip/copy runs, 4 or 6 row table

        public ushort[] Hicl;          // 128 colours, RGB555 << 1 (bit 0 unused)
        public ushort[] Htbl;          // 32 x 128 RGB555 colours (shade levels), may be null
        public byte[] Cmap;            // 768 bytes of 6 bit RGB, may be null
        public byte[] Data;

        public int EffectiveFrameDelay { get { return FrameDelay == 0 ? 0x50 : FrameDelay; } }
        public int FrameCount { get { return Math.Max(1, (int)Frames); } }

        internal void ReadHeader(byte[] b)
        {
            if (b.Length < 26) throw new InvalidDataException("BHDR is shorter than 26 bytes.");
            XOffset = BitConverter.ToInt16(b, 0);
            YOffset = BitConverter.ToInt16(b, 2);
            Width = BitConverter.ToUInt16(b, 4);
            Height = BitConverter.ToUInt16(b, 6);
            Flags = BitConverter.ToUInt16(b, 8);
            RuntimeData = BitConverter.ToUInt32(b, 10);
            Frames = BitConverter.ToUInt16(b, 14);
            FrameDelay = BitConverter.ToUInt16(b, 16);
            BlendMode = BitConverter.ToUInt16(b, 18);
            ColourSlot = BitConverter.ToUInt16(b, 20);
            Scale = BitConverter.ToInt16(b, 22);
            Compression = BitConverter.ToUInt16(b, 24);
        }

        /// <summary>
        /// Decodes one frame into Width*Height pixel bytes.
        /// Pixel values are byte offsets into HICL, so they are always even.
        /// </summary>
        public byte[] DecodeFrame(int frame)
        {
            int w = Width, h = Height;
            int size = w * h;
            byte[] output = new byte[size];
            if (Data == null || size == 0) return output;
            if (frame < 0 || frame >= FrameCount) frame = 0;

            switch (Compression)
            {
                case 0:
                    {
                        int start = frame * size;
                        int n = Math.Max(0, Math.Min(size, Data.Length - start));
                        if (n > 0) Buffer.BlockCopy(Data, start, output, 0, n);
                        break;
                    }
                case 1:
                case 2:
                    {
                        // Whole-image run streams. The game only decodes the first frame of
                        // these; later frames are assumed to follow on in the stream.
                        int src = 0;
                        byte[] all = new byte[size * FrameCount];
                        int dst = 0;
                        int want = size * (frame + 1);
                        if (Compression == 1) DecodeRle(Data, ref src, all, ref dst, want);
                        else DecodeSkipRuns(Data, ref src, all, ref dst, want);
                        Buffer.BlockCopy(all, frame * size, output, 0, size);
                        break;
                    }
                case 4:
                case 6:
                    {
                        // Row table: Height*Frames 32 bit offsets from the start of DATA.
                        // Bit 31 set means the row is RLE coded, otherwise it is raw.
                        int dst = 0;
                        for (int y = 0; y < h; y++)
                        {
                            int tablePos = (frame * h + y) * 4;
                            if (tablePos + 4 > Data.Length) break;
                            uint v = BitConverter.ToUInt32(Data, tablePos);
                            int rowStart = (int)(v & 0x7FFFFFFF);
                            if ((v & 0x80000000) == 0)
                            {
                                int n = Math.Max(0, Math.Min(w, Data.Length - rowStart));
                                if (n > 0) Buffer.BlockCopy(Data, rowStart, output, dst, n);
                                dst += w;
                            }
                            else
                            {
                                int src = rowStart;
                                DecodeRle(Data, ref src, output, ref dst, dst + w);
                                dst = (y + 1) * w;
                            }
                        }
                        break;
                    }
                default:
                    throw new NotSupportedException("Unknown BSI compression " + Compression + ".");
            }
            return output;
        }

        // Control byte with bit 7 set: repeat the next byte (ctrl & 0x7F) times.
        // Otherwise copy the next ctrl bytes.
        static void DecodeRle(byte[] src, ref int s, byte[] dst, ref int d, int end)
        {
            while (d < end && s < src.Length)
            {
                int c = src[s++];
                if ((c & 0x80) != 0)
                {
                    c &= 0x7F;
                    if (s >= src.Length) return;
                    byte v = src[s++];
                    for (int i = 0; i < c && d < end; i++) dst[d++] = v;
                }
                else
                {
                    for (int i = 0; i < c && d < end && s < src.Length; i++) dst[d++] = src[s++];
                }
            }
        }

        // Pairs of (skip, count): skip transparent pixels, then copy count bytes.
        static void DecodeSkipRuns(byte[] src, ref int s, byte[] dst, ref int d, int end)
        {
            while (d < end && s + 1 < src.Length)
            {
                int skip = src[s++];
                int count = src[s++];
                d += skip;
                for (int i = 0; i < count && d < end && s < src.Length; i++) dst[d++] = src[s++];
            }
        }

        /// <summary>
        /// HICL colours are RGB555 shifted up one bit. Fitted against an in-game
        /// screenshot of RACEPICK.BSI; HTBL level 31 equals HICL >> 1.
        /// </summary>
        public static Color FromHicl(ushort c)
        {
            return FromRgb555((ushort)(c >> 1));
        }

        public static Color FromRgb555(ushort c)
        {
            int r = (c >> 10) & 0x1F, g = (c >> 5) & 0x1F, b = c & 0x1F;
            return Color.FromArgb((r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2));
        }

        public bool HasPalette(BsiPalette p)
        {
            switch (p)
            {
                case BsiPalette.Hicl: return Hicl != null;
                case BsiPalette.Htbl: return Htbl != null;
                case BsiPalette.Cmap: return Cmap != null;
                default: return true;
            }
        }

        /// <summary>Builds the 256 colour lookup for raw pixel values.</summary>
        public Color[] BuildPalette(BsiPalette mode, int shadeLevel)
        {
            Color[] pal = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                switch (mode)
                {
                    case BsiPalette.Hicl:
                        pal[i] = Hicl != null ? FromHicl(Hicl[(i >> 1) & 0x7F]) : Color.Magenta;
                        break;
                    case BsiPalette.Htbl:
                        {
                            int level = Math.Max(0, Math.Min(31, shadeLevel));
                            pal[i] = Htbl != null ? FromRgb555(Htbl[level * 128 + ((i >> 1) & 0x7F)]) : Color.Magenta;
                            break;
                        }
                    case BsiPalette.Cmap:
                        if (Cmap != null)
                        {
                            int r = Cmap[i * 3] & 0x3F, g = Cmap[i * 3 + 1] & 0x3F, b = Cmap[i * 3 + 2] & 0x3F;
                            pal[i] = Color.FromArgb((r << 2) | (r >> 4), (g << 2) | (g >> 4), (b << 2) | (b >> 4));
                        }
                        else pal[i] = Color.Magenta;
                        break;
                    default:
                        pal[i] = Color.FromArgb(i, i, i);
                        break;
                }
            }
            return pal;
        }

        /// <summary>Renders a frame. Pixel 0 is transparent in sprites when <paramref name="transparentZero"/> is set.</summary>
        public Bitmap RenderFrame(int frame, BsiPalette mode, int shadeLevel, bool transparentZero)
        {
            byte[] px = DecodeFrame(frame);
            return ToBitmap(px, Width, Height, BuildPalette(mode, shadeLevel), transparentZero);
        }

        /// <summary>Renders every frame stacked vertically, like the original bsitool output.</summary>
        public Bitmap RenderStrip(BsiPalette mode, int shadeLevel, bool transparentZero)
        {
            int n = FrameCount;
            byte[] all = new byte[Width * Height * n];
            for (int f = 0; f < n; f++)
            {
                byte[] px = DecodeFrame(f);
                Buffer.BlockCopy(px, 0, all, f * px.Length, px.Length);
            }
            return ToBitmap(all, Width, Height * n, BuildPalette(mode, shadeLevel), transparentZero);
        }

        public static Bitmap ToBitmap(byte[] px, int w, int h, Color[] pal, bool transparentZero)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
            if (w == 0 || h == 0) return bmp;
            int[] argb = new int[256];
            for (int i = 0; i < 256; i++) argb[i] = pal[i].ToArgb();
            if (transparentZero) argb[0] = 0;

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int[] row = new int[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) row[x] = argb[px[y * w + x]];
                Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w);
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
    }

    /// <summary>
    /// A BSI file: big endian chunk headers (4 byte tag, 4 byte length) with little
    /// endian contents. An optional leading BSIF header is skipped, as the game does.
    /// Some BSI files hold several images (IFHD ... DATA repeated).
    /// </summary>
    public class BsiFile
    {
        public List<BsiChunk> Chunks = new List<BsiChunk>();
        public List<BsiImage> Images = new List<BsiImage>();
        public bool HasBsifHeader;
        public string Warning;

        public static BsiFile Parse(byte[] data)
        {
            BsiFile f = new BsiFile();
            int pos = 0;
            if (data.Length >= 8 && TextUtil.FourCC(data, 0) == "BSIF")
            {
                f.HasBsifHeader = true;
                pos = 8;
            }

            BsiImage cur = null;
            string pendingName = null;
            byte[] pendingIfhd = null;

            while (pos + 8 <= data.Length)
            {
                string tag = TextUtil.FourCC(data, pos);
                int len = (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
                if (!IsTag(tag) || len < 0 || pos + 8 + (long)len > data.Length)
                {
                    f.Warning = string.Format("Not a BSI chunk at offset 0x{0:X} ('{1}').", pos, Printable(tag));
                    break;
                }
                BsiChunk c = new BsiChunk();
                c.Tag = tag;
                c.Offset = pos;
                c.Length = len;
                c.Data = new byte[len];
                Buffer.BlockCopy(data, pos + 8, c.Data, 0, len);
                f.Chunks.Add(c);
                pos += 8 + len;

                switch (tag)
                {
                    case "IFHD": pendingIfhd = c.Data; break;
                    case "NAME": pendingName = TextUtil.ReadCString(c.Data, 0, c.Data.Length); break;
                    case "BHDR":
                        cur = new BsiImage();
                        cur.Name = pendingName;
                        cur.Ifhd = pendingIfhd;
                        cur.ReadHeader(c.Data);
                        pendingName = null;
                        pendingIfhd = null;
                        break;
                    case "HICL":
                        if (cur == null) break;
                        cur.Hicl = new ushort[128];
                        for (int i = 0; i < 128 && i * 2 + 1 < c.Data.Length; i++)
                            cur.Hicl[i] = BitConverter.ToUInt16(c.Data, i * 2);
                        break;
                    case "HTBL":
                        if (cur == null) break;
                        cur.Htbl = new ushort[c.Data.Length / 2];
                        for (int i = 0; i < cur.Htbl.Length; i++)
                            cur.Htbl[i] = BitConverter.ToUInt16(c.Data, i * 2);
                        if (cur.Htbl.Length < 32 * 128) cur.Htbl = null;
                        break;
                    case "CMAP":
                        if (cur == null) break;
                        cur.Cmap = c.Data.Length >= 768 ? c.Data : null;
                        break;
                    case "DATA":
                        if (cur == null) break;
                        cur.Data = c.Data;
                        f.Images.Add(cur);
                        cur = null;
                        break;
                }
                if (tag == "END ") break;
            }

            if (f.Images.Count == 0 && f.Warning == null)
                f.Warning = "No images found.";
            return f;
        }

        static bool IsTag(string tag)
        {
            foreach (char ch in tag)
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == ' ')) return false;
            return true;
        }

        static string Printable(string s)
        {
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++) if (c[i] < 32 || c[i] > 126) c[i] = '.';
            return new string(c);
        }
    }
}
