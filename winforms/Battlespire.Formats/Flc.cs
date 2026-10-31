using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Battlespire.Formats
{
    /// <summary>
    /// Autodesk FLI/FLC animation, as stored in FLC.BSA (the talking heads in
    /// conversations). The game's player (0x5c2b4) handles chunk types 4, 7, 11, 12,
    /// 13, 15 and 16; this decoder handles the same set. Frames after the first only
    /// hold the pixels that changed, so a frame can only be shown by playing every
    /// frame before it.
    /// </summary>
    public class FlcFile
    {
        public const ushort MagicFli = 0xAF11;
        public const ushort MagicFlc = 0xAF12;
        const ushort FrameChunk = 0xF1FA;

        public ushort Magic;
        public int Width;
        public int Height;
        public int Depth;
        /// <summary>Number of frames in the animation, not counting the ring frame.</summary>
        public int FrameCount;
        /// <summary>Delay between frames in milliseconds.</summary>
        public int FrameDelay;
        /// <summary>True when the file has a ring frame that turns the last frame back into the first.</summary>
        public bool HasRingFrame;
        /// <summary>Offsets of every frame chunk, including the ring frame.</summary>
        public List<int> FrameOffsets = new List<int>();
        public List<string> Warnings = new List<string>();
        public byte[] Data;

        public bool IsFlc { get { return Magic == MagicFlc; } }

        public static FlcFile Parse(byte[] data)
        {
            if (data.Length < 128) throw new InvalidDataException("File is too small to be a FLI/FLC animation.");
            FlcFile f = new FlcFile();
            f.Data = data;
            f.Magic = BitConverter.ToUInt16(data, 4);
            if (f.Magic != MagicFli && f.Magic != MagicFlc)
                throw new InvalidDataException(string.Format("Not a FLI/FLC animation (magic 0x{0:X4}).", f.Magic));
            f.FrameCount = BitConverter.ToUInt16(data, 6);
            f.Width = BitConverter.ToUInt16(data, 8);
            f.Height = BitConverter.ToUInt16(data, 10);
            f.Depth = BitConverter.ToUInt16(data, 12);
            if (f.Magic == MagicFli)
            {
                // FLI: 320x200 and speed in 1/70 second ticks.
                if (f.Width == 0) f.Width = 320;
                if (f.Height == 0) f.Height = 200;
                f.FrameDelay = BitConverter.ToUInt16(data, 16) * 1000 / 70;
            }
            else
            {
                f.FrameDelay = BitConverter.ToInt32(data, 16);
            }
            if (f.Depth != 8 && f.Depth != 0) f.Warnings.Add(f.Depth + " bits per pixel; only 8 bit animations are supported.");

            int pos = 128;
            if (f.Magic == MagicFlc)
            {
                int oframe1 = BitConverter.ToInt32(data, 80);
                if (oframe1 >= 128 && oframe1 < data.Length) pos = oframe1;
            }
            int end = Math.Min(data.Length, Math.Max(128, BitConverter.ToInt32(data, 0)));
            if (end < data.Length && end <= pos) end = data.Length;
            while (pos + 6 <= end)
            {
                int size = BitConverter.ToInt32(data, pos);
                ushort type = BitConverter.ToUInt16(data, pos + 4);
                if (size < 6 || pos + size > data.Length)
                {
                    f.Warnings.Add(string.Format("Chunk at 0x{0:X} has a bad size ({1}); stopped there.", pos, size));
                    break;
                }
                if (type == FrameChunk) f.FrameOffsets.Add(pos);
                pos += size;
            }
            if (f.FrameCount == 0) f.FrameCount = f.FrameOffsets.Count;
            f.HasRingFrame = f.FrameOffsets.Count > f.FrameCount;
            if (f.FrameOffsets.Count < f.FrameCount)
            {
                f.Warnings.Add(string.Format("Header says {0} frames but the file holds {1}.", f.FrameCount, f.FrameOffsets.Count));
                f.FrameCount = f.FrameOffsets.Count;
            }
            return f;
        }

        public FlcDecoder CreateDecoder()
        {
            return new FlcDecoder(this);
        }
    }

    /// <summary>
    /// Plays an animation frame by frame into an 8 bit screen and a 256 colour palette.
    /// </summary>
    public class FlcDecoder
    {
        readonly FlcFile file;
        public readonly byte[] Pixels;
        public readonly Color[] Palette = new Color[256];
        /// <summary>The frame now in <see cref="Pixels"/>, or -1 before the first.</summary>
        public int CurrentFrame { get; private set; }

        public FlcDecoder(FlcFile file)
        {
            this.file = file;
            Pixels = new byte[file.Width * file.Height];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(Pixels, 0, Pixels.Length);
            for (int i = 0; i < 256; i++) Palette[i] = Color.FromArgb(i, i, i);
            CurrentFrame = -1;
        }

        /// <summary>Plays forward (rewinding if needed) until <paramref name="frame"/> is showing.</summary>
        public void Seek(int frame)
        {
            if (frame < 0 || frame >= file.FrameCount) throw new ArgumentOutOfRangeException("frame");
            if (frame < CurrentFrame) Reset();
            while (CurrentFrame < frame) Step();
        }

        /// <summary>Shows the next frame, going back to frame 0 after the last.</summary>
        public void Next()
        {
            if (CurrentFrame + 1 >= file.FrameCount)
            {
                // The ring frame turns the last frame back into the first without a full redraw.
                if (file.HasRingFrame)
                {
                    ApplyFrame(file.FrameOffsets[file.FrameCount]);
                    CurrentFrame = 0;
                }
                else
                {
                    Reset();
                    Step();
                }
            }
            else Step();
        }

        void Step()
        {
            ApplyFrame(file.FrameOffsets[CurrentFrame + 1]);
            CurrentFrame++;
        }

        void ApplyFrame(int pos)
        {
            byte[] d = file.Data;
            int frameSize = BitConverter.ToInt32(d, pos);
            int chunks = BitConverter.ToUInt16(d, pos + 6);
            int p = pos + 16;
            int end = pos + frameSize;
            for (int c = 0; c < chunks && p + 6 <= end; c++)
            {
                int size = BitConverter.ToInt32(d, p);
                int type = BitConverter.ToUInt16(d, p + 4);
                if (size < 6 || p + size > end) break;
                int body = p + 6;
                switch (type)
                {
                    case 4: ReadPalette(d, body, false); break;
                    case 11: ReadPalette(d, body, true); break;
                    case 7: DeltaFlc(d, body, p + size); break;
                    case 12: DeltaFli(d, body, p + size); break;
                    case 13: Array.Clear(Pixels, 0, Pixels.Length); break;
                    case 15: ByteRun(d, body, p + size); break;
                    case 16: Buffer.BlockCopy(d, body, Pixels, 0, Math.Min(Pixels.Length, size - 6)); break;
                    // 18 is a postage stamp (thumbnail); the game ignores it too.
                }
                p += size;
            }
        }

        void ReadPalette(byte[] d, int p, bool sixBit)
        {
            int packets = BitConverter.ToUInt16(d, p);
            p += 2;
            int index = 0;
            for (int k = 0; k < packets; k++)
            {
                index += d[p];
                int count = d[p + 1];
                if (count == 0) count = 256;
                p += 2;
                for (int i = 0; i < count && index < 256; i++, index++, p += 3)
                {
                    int r = d[p], g = d[p + 1], b = d[p + 2];
                    if (sixBit) { r = (r << 2) | (r >> 4); g = (g << 2) | (g >> 4); b = (b << 2) | (b >> 4); }
                    Palette[index] = Color.FromArgb(r & 255, g & 255, b & 255);
                }
            }
        }

        void ByteRun(byte[] d, int p, int end)
        {
            int w = file.Width;
            for (int y = 0; y < file.Height && p < end; y++)
            {
                p++; // packet count, unused (old players relied on it; widths decide here)
                int x = 0, row = y * w;
                while (x < w && p < end)
                {
                    sbyte n = (sbyte)d[p++];
                    if (n >= 0)
                    {
                        byte v = d[p++];
                        for (int i = 0; i < n && x < w; i++) Pixels[row + x++] = v;
                    }
                    else
                    {
                        for (int i = 0; i < -n && x < w; i++) Pixels[row + x++] = d[p++];
                    }
                }
            }
        }

        void DeltaFlc(byte[] d, int p, int end)
        {
            int w = file.Width, h = file.Height;
            int lines = BitConverter.ToUInt16(d, p);
            p += 2;
            int y = 0;
            for (int l = 0; l < lines && p + 2 <= end && y < h; l++)
            {
                int packets;
                while (true)
                {
                    ushort op = BitConverter.ToUInt16(d, p);
                    p += 2;
                    if ((op & 0xC000) == 0xC000) y += -(short)op;           // skip lines
                    else if ((op & 0xC000) != 0) { if (y < h) Pixels[y * w + w - 1] = (byte)op; } // last pixel (the game treats 0x4000 the same)
                    else { packets = op; break; }
                    if (p + 2 > end) return;
                }
                if (y >= h) return;
                int x = 0, row = y * w;
                for (int k = 0; k < packets && p + 2 <= end; k++)
                {
                    x += d[p];
                    sbyte n = (sbyte)d[p + 1];
                    p += 2;
                    if (n >= 0)
                    {
                        for (int i = 0; i < n; i++, p += 2)
                        {
                            if (x < w) Pixels[row + x] = d[p];
                            if (x + 1 < w) Pixels[row + x + 1] = d[p + 1];
                            x += 2;
                        }
                    }
                    else
                    {
                        byte a = d[p], b = d[p + 1];
                        p += 2;
                        for (int i = 0; i < -n; i++)
                        {
                            if (x < w) Pixels[row + x] = a;
                            if (x + 1 < w) Pixels[row + x + 1] = b;
                            x += 2;
                        }
                    }
                }
                y++;
            }
        }

        void DeltaFli(byte[] d, int p, int end)
        {
            int w = file.Width;
            int y = BitConverter.ToUInt16(d, p);
            int lines = BitConverter.ToUInt16(d, p + 2);
            p += 4;
            for (int l = 0; l < lines && p < end && y < file.Height; l++, y++)
            {
                int packets = d[p++];
                int x = 0, row = y * w;
                for (int k = 0; k < packets && p + 1 < end; k++)
                {
                    x += d[p];
                    sbyte n = (sbyte)d[p + 1];
                    p += 2;
                    if (n >= 0)
                    {
                        for (int i = 0; i < n; i++) { if (x < w) Pixels[row + x] = d[p]; x++; p++; }
                    }
                    else
                    {
                        byte v = d[p++];
                        for (int i = 0; i < -n; i++) { if (x < w) Pixels[row + x] = v; x++; }
                    }
                }
            }
        }

        /// <summary>The current frame as a 32 bit bitmap.</summary>
        public Bitmap ToBitmap()
        {
            int w = file.Width, h = file.Height;
            Bitmap bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
            if (w == 0 || h == 0) return bmp;
            int[] argb = new int[256];
            for (int i = 0; i < 256; i++) argb[i] = Palette[i].ToArgb();
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int[] row = new int[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) row[x] = argb[Pixels[y * w + x]];
                Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w);
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
    }
}
