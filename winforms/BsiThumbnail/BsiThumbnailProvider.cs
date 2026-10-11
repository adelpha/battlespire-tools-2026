using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Battlespire.Formats;

namespace BsiThumbnail
{
    /// <summary>How the shell should treat the alpha channel of a thumbnail.</summary>
    public enum WTS_ALPHATYPE
    {
        WTSAT_UNKNOWN = 0,
        WTSAT_RGB = 1,
        WTSAT_ARGB = 2
    }

    [ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IThumbnailProvider
    {
        void GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    /// <summary>
    /// Explorer thumbnail handler for Battlespire .BSI images. Explorer runs it in a
    /// separate surrogate process (dllhost.exe), hands it the file as a stream, and
    /// asks for a bitmap no bigger than cx pixels square. The thumbnail is the first
    /// frame of the first image in the game's own colours (HICL).
    /// </summary>
    [ComVisible(true)]
    [Guid(BsiThumbnailProvider.ClsidString)]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("Battlespire.BsiThumbnail")]
    public class BsiThumbnailProvider : IThumbnailProvider, IInitializeWithStream
    {
        public const string ClsidString = "6B1F4E52-3C8A-4D27-9E61-B5A0D2C47F18";

        // Largest file read from the stream. The biggest BSI in BSI.BSA is well under 1 MB.
        const int MaxFileSize = 16 * 1024 * 1024;

        byte[] data;

        public void Initialize(IStream pstream, uint grfMode)
        {
            if (data != null) throw new COMException("Already initialised", unchecked((int)0x8007139F)); // HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED)
            try
            {
                data = ReadAll(pstream);
            }
            finally
            {
                // Let go of the file as soon as it has been read.
                if (Marshal.IsComObject(pstream)) Marshal.ReleaseComObject(pstream);
            }
        }

        public void GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha)
        {
            phbmp = IntPtr.Zero;
            pdwAlpha = WTS_ALPHATYPE.WTSAT_ARGB;
            if (data == null) throw new COMException("Not initialised", unchecked((int)0x8000FFFF)); // E_UNEXPECTED

            BsiFile bsi = BsiFile.Parse(data);
            if (bsi.Images.Count == 0) throw new COMException("No image in file", unchecked((int)0x80004005)); // E_FAIL
            BsiImage img = bsi.Images[0];
            if (img.Width == 0 || img.Height == 0) throw new COMException("Empty image", unchecked((int)0x80004005));

            BsiPalette pal = img.HasPalette(BsiPalette.Hicl) ? BsiPalette.Hicl
                : img.HasPalette(BsiPalette.Cmap) ? BsiPalette.Cmap : BsiPalette.Greyscale;
            using (Bitmap frame = img.RenderFrame(0, pal, 31, (img.Flags & 1) != 0))
            using (Bitmap thumb = Fit(frame, (int)Math.Max(1, cx)))
                phbmp = ToDibSection(thumb);
        }

        /// <summary>
        /// Small images are enlarged by a whole number so the pixels stay sharp;
        /// big ones are shrunk smoothly to fit.
        /// </summary>
        static Bitmap Fit(Bitmap src, int size)
        {
            int w, h;
            bool enlarge = src.Width <= size && src.Height <= size;
            if (enlarge)
            {
                int k = Math.Max(1, size / Math.Max(src.Width, src.Height));
                w = src.Width * k;
                h = src.Height * k;
            }
            else
            {
                double s = Math.Min(size / (double)src.Width, size / (double)src.Height);
                w = Math.Max(1, (int)Math.Round(src.Width * s));
                h = Math.Max(1, (int)Math.Round(src.Height * s));
            }
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = enlarge ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    // Stops the edge pixels fading out when scaling.
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        static byte[] ReadAll(IStream stream)
        {
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            stream.Stat(out stat, 1); // STATFLAG_NONAME
            if (stat.cbSize <= 0 || stat.cbSize > MaxFileSize) throw new COMException("Unexpected file size", unchecked((int)0x80004005));

            byte[] buf = new byte[stat.cbSize];
            IntPtr pRead = Marshal.AllocCoTaskMem(4);
            try
            {
                int total = 0;
                while (total < buf.Length)
                {
                    byte[] chunk = new byte[Math.Min(65536, buf.Length - total)];
                    Marshal.WriteInt32(pRead, 0);
                    stream.Read(chunk, chunk.Length, pRead);
                    int n = Marshal.ReadInt32(pRead);
                    if (n <= 0) break;
                    Buffer.BlockCopy(chunk, 0, buf, total, n);
                    total += n;
                }
                if (total < buf.Length) Array.Resize(ref buf, total);
                return buf;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pRead);
            }
        }

        /// <summary>
        /// Copies a 32 bit ARGB bitmap into a top-down DIB section, which keeps the alpha
        /// channel (Bitmap.GetHbitmap does not).
        /// </summary>
        static IntPtr ToDibSection(Bitmap bmp)
        {
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = bmp.Width;
            bi.biHeight = -bmp.Height; // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            IntPtr bits;
            IntPtr hbmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero) throw new COMException("CreateDIBSection failed", unchecked((int)0x8007000E)); // E_OUTOFMEMORY

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bmp.Width * 4;
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, rowBytes);
                    Marshal.Copy(row, 0, new IntPtr(bits.ToInt64() + (long)y * rowBytes), rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return hbmp;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    }
}
