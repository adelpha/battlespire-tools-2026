using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Battlespire.Formats.Ui
{
    /// <summary>A file's bytes plus where they came from (loose file or BSA entry).</summary>
    public class LoadedFile
    {
        public string Name;
        public string Description;
        public byte[] Data;
    }

    /// <summary>
    /// Opening files for the viewers: either a loose file, or an entry picked from a
    /// BSA archive. The same dialog handles both, so any viewer can browse 3D.BSA,
    /// BSI.BSA and BS6.BSA directly.
    /// </summary>
    public static class FileSource
    {
        public static LoadedFile Load(string path)
        {
            LoadedFile f = new LoadedFile();
            f.Name = Path.GetFileName(path);
            f.Description = path;
            f.Data = File.ReadAllBytes(path);
            return f;
        }

        /// <summary>
        /// Shows an open dialog. When a .BSA is picked, a second dialog lists its entries
        /// whose names end with one of <paramref name="extensions"/> (or all entries).
        /// </summary>
        public static LoadedFile Prompt(IWin32Window owner, string title, string filter, string[] extensions)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = title;
                dlg.Filter = filter + "|BSA archives (*.bsa)|*.bsa|All files (*.*)|*.*";
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                if (string.Equals(Path.GetExtension(dlg.FileName), ".bsa", StringComparison.OrdinalIgnoreCase))
                    return PickFromArchive(owner, dlg.FileName, extensions);
                return Load(dlg.FileName);
            }
        }

        public static LoadedFile PickFromArchive(IWin32Window owner, string bsaPath, string[] extensions)
        {
            using (BsaArchive bsa = new BsaArchive(bsaPath))
            using (ArchivePicker picker = new ArchivePicker(bsa, extensions))
            {
                if (picker.ShowDialog(owner) != DialogResult.OK || picker.Selected == null) return null;
                LoadedFile f = new LoadedFile();
                f.Name = picker.Selected.Name;
                f.Description = bsaPath + " : " + picker.Selected.Name;
                f.Data = bsa.Read(picker.Selected);
                return f;
            }
        }

        /// <summary>Opens bytes in a sibling tool (BsiTool.exe etc. next to this exe) via a temp file.</summary>
        public static bool OpenInTool(string exeName, string fileName, byte[] data)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string exe = Path.Combine(dir, exeName);
            if (!File.Exists(exe)) return false;
            string tempDir = Path.Combine(Path.GetTempPath(), "BattlespireTools");
            Directory.CreateDirectory(tempDir);
            string temp = Path.Combine(tempDir, BsaArchive.SanitizeFileName(fileName));
            File.WriteAllBytes(temp, data);
            Process.Start(exe, "\"" + temp + "\"");
            return true;
        }

        public static void ShowError(IWin32Window owner, string what, Exception ex)
        {
            MessageBox.Show(owner, what + "\n\n" + ex.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Lists the entries of an archive with a filter box.</summary>
    public class ArchivePicker : Form
    {
        readonly BsaArchive bsa;
        readonly string[] extensions;
        readonly TextBox filter = new TextBox();
        readonly ListBox list = new ListBox();
        readonly CheckBox showAll = new CheckBox();

        public BsaEntry Selected { get; private set; }

        public ArchivePicker(BsaArchive bsa, string[] extensions)
        {
            this.bsa = bsa;
            this.extensions = extensions;
            Text = "Choose a file from " + Path.GetFileName(bsa.Path);
            Size = new Size(380, 520);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;

            Label l = new Label();
            l.Text = "Filter:";
            l.AutoSize = true;
            l.Location = new Point(8, 11);
            filter.Location = new Point(50, 8);
            filter.Width = 305;
            filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            filter.TextChanged += delegate { Fill(); };

            showAll.Text = "Show all entries";
            showAll.AutoSize = true;
            showAll.Location = new Point(8, 36);
            showAll.Visible = extensions != null && extensions.Length > 0;
            showAll.CheckedChanged += delegate { Fill(); };

            list.Location = new Point(8, 60);
            list.Size = new Size(347, 380);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.IntegralHeight = false;
            list.DoubleClick += delegate { Accept(); };

            Button ok = new Button();
            ok.Text = "Open";
            ok.Location = new Point(199, 448);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.Click += delegate { Accept(); };
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(280, 448);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { l, filter, showAll, list, ok, cancel });
            Fill();
        }

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            string f = filter.Text.Trim();
            foreach (BsaEntry e in bsa.Entries)
            {
                if (!showAll.Checked && extensions != null && extensions.Length > 0 && bsa.Kind != BsaKind.Numeric)
                {
                    bool match = false;
                    foreach (string ext in extensions)
                        if (e.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!match) continue;
                }
                if (f.Length > 0 && e.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Items.Add(e);
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        void Accept()
        {
            Selected = list.SelectedItem as BsaEntry;
            if (Selected == null) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>
    /// Shows a bitmap zoomed with square pixels on a checkerboard, scrollable.
    /// The mouse wheel zooms in and out in 10% steps around the cursor;
    /// Shift+wheel scrolls instead.
    /// </summary>
    public class PixelView : Panel
    {
        public const int MinZoom = 10, MaxZoom = 1600, ZoomStep = 10;

        Bitmap image;
        int zoom = 200;

        /// <summary>Raised when the zoom changes, including from the mouse wheel.</summary>
        public event EventHandler ZoomChanged;

        public PixelView()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = SystemColors.AppWorkspace;
        }

        public Bitmap Image
        {
            get { return image; }
            set { image = value; UpdateSize(); Invalidate(); }
        }

        /// <summary>Zoom in percent (100 = one screen pixel per image pixel).</summary>
        public int Zoom
        {
            get { return zoom; }
            set { SetZoom(value, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); }
        }

        /// <summary>Picks a starting zoom so small images are not tiny and big ones fit.</summary>
        public static int DefaultZoomFor(int width, int height)
        {
            return width <= 64 && height <= 64 ? 400 : width <= 200 ? 200 : 100;
        }

        int Scaled(int n) { return Math.Max(1, (int)Math.Round(n * zoom / 100.0)); }

        void SetZoom(int value, Point anchor)
        {
            value = Math.Max(MinZoom, Math.Min(MaxZoom, value));
            if (value == zoom) return;
            // Keep the image point under the anchor where it is.
            Point scroll = AutoScrollPosition;
            double ix = (anchor.X - 8 - scroll.X) * 100.0 / zoom;
            double iy = (anchor.Y - 8 - scroll.Y) * 100.0 / zoom;
            zoom = value;
            UpdateSize();
            int sx = (int)Math.Round(ix * zoom / 100.0) + 8 - anchor.X;
            int sy = (int)Math.Round(iy * zoom / 100.0) + 8 - anchor.Y;
            AutoScrollPosition = new Point(Math.Max(0, sx), Math.Max(0, sy));
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        void UpdateSize()
        {
            AutoScrollMinSize = image == null ? Size.Empty : new Size(Scaled(image.Width) + 16, Scaled(image.Height) + 16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (image == null) return;
            Rectangle r = new Rectangle(8 + AutoScrollPosition.X, 8 + AutoScrollPosition.Y, Scaled(image.Width), Scaled(image.Height));
            using (HatchBrush checker = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(200, 200, 200), Color.White))
                e.Graphics.FillRectangle(checker, r);
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(image, r);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // The wheel goes to the focused control on older Windows versions.
            Form f = FindForm();
            if (f != null && f.ContainsFocus && !(f.ActiveControl is TextBoxBase)) Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Shift) != 0 || image == null)
            {
                base.OnMouseWheel(e);
                return;
            }
            int notches = e.Delta / 120;
            if (notches == 0) notches = e.Delta > 0 ? 1 : -1;
            // Step from the nearest multiple of 10% so odd starting values line up.
            int z = (int)Math.Round(zoom / (double)ZoomStep) * ZoomStep + notches * ZoomStep;
            SetZoom(z, e.Location);
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }
    }

    /// <summary>Formats a byte array as a classic hex dump.</summary>
    public static class HexDump
    {
        public static string Format(byte[] data, int max)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int n = Math.Min(data.Length, max);
            for (int i = 0; i < n; i += 16)
            {
                sb.AppendFormat("{0:X8}  ", i);
                for (int k = 0; k < 16; k++)
                {
                    if (i + k < n) sb.AppendFormat("{0:X2} ", data[i + k]);
                    else sb.Append("   ");
                    if (k == 7) sb.Append(' ');
                }
                sb.Append(' ');
                for (int k = 0; k < 16 && i + k < n; k++)
                {
                    byte b = data[i + k];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine();
            }
            if (data.Length > max) sb.AppendFormat("... {0} more bytes", data.Length - max).AppendLine();
            return sb.ToString();
        }
    }
}
