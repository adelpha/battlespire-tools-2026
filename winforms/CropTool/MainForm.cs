using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CropTool
{
    /// <summary>
    /// Batch cropper: draw a crop box once on a sample image, then crop every image
    /// found under a folder (and its subfolders) to that box.
    /// </summary>
    public class MainForm : Form
    {
                readonly TextBox folderBox = new TextBox();
        readonly CheckBox recurse = new CheckBox();
        readonly ListBox fileList = new ListBox();
        readonly CropCanvas canvas = new CropCanvas();
        readonly NumericUpDown xBox = new NumericUpDown(), yBox = new NumericUpDown(), wBox = new NumericUpDown(), hBox = new NumericUpDown();
        readonly RadioButton toFolder = new RadioButton(), overwrite = new RadioButton();
        readonly TextBox outBox = new TextBox();
        readonly Button runButton = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly List<string> files = new List<string>();
        string root;
        bool syncing;

        public MainForm(string path)
        {
            Text = "Battlespire PNG Batch Crop";
            Size = new Size(1100, 760);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f.Length > 0) OpenFolder(Directory.Exists(f[0]) ? f[0] : Path.GetDirectoryName(f[0]));
            };

            // Top: folder picker.
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 32;
            top.Padding = new Padding(6, 5, 6, 3);
            Label fl = new Label();
            fl.Text = "Folder:";
            fl.AutoSize = false;
            fl.Width = 50;
            fl.Dock = DockStyle.Left;
            fl.TextAlign = ContentAlignment.MiddleLeft;
            folderBox.Dock = DockStyle.Fill;
            folderBox.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { OpenFolder(folderBox.Text); e.SuppressKeyPress = true; } };
            Button browse = new Button();
            browse.Text = "Browse...";
            browse.Dock = DockStyle.Right;
            browse.Width = 80;
            browse.Click += delegate { BrowseFolder(); };
            recurse.Text = "Include subfolders";
            recurse.Checked = true;
            recurse.AutoSize = false;
            recurse.Width = 140;
            recurse.Dock = DockStyle.Right;
            recurse.Padding = new Padding(10, 0, 0, 0);
            recurse.CheckedChanged += delegate { if (root != null) OpenFolder(root); };
            top.Controls.Add(fl);
            top.Controls.Add(recurse);
            top.Controls.Add(browse);
            top.Controls.Add(folderBox);
            folderBox.BringToFront();

            // Left: images found.
            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = 250;
            left.Padding = new Padding(6, 0, 0, 6);
            fileList.Dock = DockStyle.Fill;
            fileList.IntegralHeight = false;
            fileList.HorizontalScrollbar = true;
            fileList.SelectedIndexChanged += delegate { ShowSample(fileList.SelectedIndex); };
            Label ll = new Label();
            ll.Text = "PNG images (pick one to draw the box on)";
            ll.Dock = DockStyle.Top;
            ll.Height = 18;
            left.Controls.Add(fileList);
            left.Controls.Add(ll);

            // Right: crop box and run settings.
            FlowLayoutPanel right = new FlowLayoutPanel();
            right.Dock = DockStyle.Right;
            right.Width = 250;
            right.FlowDirection = FlowDirection.TopDown;
            right.WrapContents = false;
            right.Padding = new Padding(6, 0, 6, 6);

            right.Controls.Add(Caption("Crop box (pixels)"));
            right.Controls.Add(Hint("Drag on the image to draw a box. Drag inside it to move it, or drag its edges and corners to resize it."));
            right.Controls.Add(Row("X", xBox));
            right.Controls.Add(Row("Y", yBox));
            right.Controls.Add(Row("Width", wBox));
            right.Controls.Add(Row("Height", hBox));
            foreach (NumericUpDown n in new[] { xBox, yBox, wBox, hBox })
            {
                n.Maximum = 100000;
                n.Width = 80;
                n.ValueChanged += delegate { BoxesChanged(); };
            }
            Button whole = new Button();
            whole.Text = "Select whole image";
            whole.Width = 230;
            whole.Click += delegate { if (canvas.Image != null) canvas.Crop = new Rectangle(Point.Empty, canvas.Image.Size); };
            right.Controls.Add(whole);

            right.Controls.Add(Caption("Save cropped images"));
            toFolder.Text = "To a copy of the folder tree in:";
            toFolder.AutoSize = true;
            toFolder.Checked = true;
            toFolder.CheckedChanged += delegate { outBox.Enabled = toFolder.Checked; };
            outBox.Width = 230;
            Button outBrowse = new Button();
            outBrowse.Text = "Choose output folder...";
            outBrowse.Width = 230;
            outBrowse.Click += delegate { BrowseOutput(); };
            overwrite.Text = "Over the original files";
            overwrite.AutoSize = true;
            right.Controls.Add(toFolder);
            right.Controls.Add(outBox);
            right.Controls.Add(outBrowse);
            right.Controls.Add(overwrite);

            runButton.Text = "Crop all images";
            runButton.Width = 230;
            runButton.Height = 32;
            runButton.Margin = new Padding(3, 14, 3, 3);
            runButton.Enabled = false;
            runButton.Click += delegate { RunCrop(); };
            right.Controls.Add(runButton);
            progress.Width = 230;
            right.Controls.Add(progress);

            canvas.Dock = DockStyle.Fill;
            canvas.CropChanged += delegate { CropChangedOnCanvas(); };

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "Choose the folder that holds the exported frames.";
            bar.Items.Add(status);

            Controls.Add(canvas);
            Controls.Add(left);
            Controls.Add(right);
            Controls.Add(top);
            Controls.Add(bar);

            Load += delegate { if (!string.IsNullOrEmpty(path)) OpenFolder(path); };
        }

        static Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
            l.Margin = new Padding(3, 10, 3, 2);
            return l;
        }

        static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.MaximumSize = new Size(230, 0);
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            return l;
        }

        static Control Row(string label, Control c)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.Margin = new Padding(0);
            Label l = new Label();
            l.Text = label;
            l.Width = 60;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(3, 5, 3, 3);
            p.Controls.Add(l);
            p.Controls.Add(c);
            return p;
        }

        void BrowseFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose the folder that holds the PNGs (for example the folder you exported FLC frames to).";
                if (root != null) dlg.SelectedPath = root;
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenFolder(dlg.SelectedPath);
            }
        }

        void BrowseOutput()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Save the cropped images (same subfolders and names) to:";
                if (dlg.ShowDialog(this) == DialogResult.OK) { outBox.Text = dlg.SelectedPath; toFolder.Checked = true; }
            }
        }

        static bool IsImage(string path)
        {
            return string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);
        }

        void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                MessageBox.Show(this, "Folder not found: " + path, Text);
                return;
            }
            root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            folderBox.Text = root;
            outBox.Text = root + "_cropped";
            files.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(root, "*", recurse.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    if (IsImage(f)) files.Add(f);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not list " + root + ":\r\n" + ex.Message, Text);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            fileList.BeginUpdate();
            fileList.Items.Clear();
            foreach (string f in files) fileList.Items.Add(Relative(f));
            fileList.EndUpdate();
            runButton.Enabled = files.Count > 0;
            status.Text = string.Format("{0} PNG(s) found under {1}", files.Count, root);
            if (files.Count > 0) fileList.SelectedIndex = 0;
            else canvas.Image = null;
        }

        string Relative(string file)
        {
            return file.Length > root.Length ? file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : file;
        }

        /// <summary>Loads an image without keeping the file open, so it can be overwritten.</summary>
        static Bitmap LoadImage(string path)
        {
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        void ShowSample(int index)
        {
            if (index < 0 || index >= files.Count) return;
            try
            {
                Bitmap b = LoadImage(files[index]);
                Image old = canvas.Image;
                canvas.Image = b;
                if (old != null) old.Dispose();
                status.Text = string.Format("{0}: {1} x {2}", Relative(files[index]), b.Width, b.Height);
            }
            catch (Exception ex)
            {
                status.Text = Relative(files[index]) + ": " + ex.Message;
            }
        }

        void CropChangedOnCanvas()
        {
            Rectangle r = canvas.Crop;
            syncing = true;
            xBox.Value = r.X;
            yBox.Value = r.Y;
            wBox.Value = r.Width;
            hBox.Value = r.Height;
            syncing = false;
        }

        void BoxesChanged()
        {
            if (syncing) return;
            canvas.Crop = new Rectangle((int)xBox.Value, (int)yBox.Value, (int)wBox.Value, (int)hBox.Value);
        }

        void RunCrop()
        {
            Rectangle crop = canvas.Crop;
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                MessageBox.Show(this, "Draw a crop box on the image first.", Text);
                return;
            }
            string outRoot = null;
            if (toFolder.Checked)
            {
                outRoot = outBox.Text.Trim();
                if (outRoot.Length == 0) { MessageBox.Show(this, "Choose an output folder.", Text); return; }
                outRoot = Path.GetFullPath(outRoot);
                if (string.Equals(outRoot.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "The output folder is the same as the source folder. Choose another folder, or pick \"Over the original files\".", Text);
                    return;
                }
            }
            else if (MessageBox.Show(this, string.Format("Crop {0} image(s) to {1} x {2} at ({3}, {4}) and overwrite the originals?\r\n\r\nThis cannot be undone.",
                         files.Count, crop.Width, crop.Height, crop.X, crop.Y), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            // Never crop the cropped copies again if the output folder is inside the source.
            List<string> todo = new List<string>();
            foreach (string f in files)
                if (outRoot == null || !f.StartsWith(outRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) todo.Add(f);

            int done = 0, skipped = 0, failed = 0;
            string firstError = null;
            progress.Maximum = Math.Max(1, todo.Count);
            progress.Value = 0;
            Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                for (int i = 0; i < todo.Count; i++)
                {
                    string src = todo[i];
                    try
                    {
                        using (Bitmap b = LoadImage(src))
                        {
                            Rectangle r = Rectangle.Intersect(crop, new Rectangle(Point.Empty, b.Size));
                            if (r.Width <= 0 || r.Height <= 0) { skipped++; continue; }
                            string dst = outRoot == null ? src : Path.Combine(outRoot, Relative(src));
                            string dir = Path.GetDirectoryName(dst);
                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            using (Bitmap c = b.Clone(r, b.PixelFormat))
                                c.Save(dst, ImageFormat.Png);
                            done++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        if (firstError == null) firstError = Relative(src) + ": " + ex.Message;
                    }
                    finally
                    {
                        progress.Value = i + 1;
                        if (i % 10 == 0)
                        {
                            status.Text = string.Format("Cropping {0} of {1}...", i + 1, todo.Count);
                            Application.DoEvents();
                        }
                    }
                }
            }
            finally
            {
                Enabled = true;
                Cursor = Cursors.Default;
            }
            string msg = string.Format("Cropped {0} image(s) to {1} x {2}{3}.", done, crop.Width, crop.Height,
                outRoot != null ? " in " + outRoot : "");
            if (skipped > 0) msg += string.Format(" {0} skipped (smaller than the box's position).", skipped);
            if (failed > 0) msg += string.Format(" {0} failed; first: {1}", failed, firstError);
            status.Text = msg;
            if (failed > 0) MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (outRoot == null && fileList.SelectedIndex >= 0) ShowSample(fileList.SelectedIndex);
        }
    }

    /// <summary>
    /// Shows an image scaled to fit and lets the user draw, move and resize a crop box on it.
    /// </summary>
    public class CropCanvas : Control
    {
        const int Grip = 6;
        const int L = 1, T = 2, R = 4, B = 8;

        Image image;
        Rectangle crop;
        int dragEdges;      // which edges a drag moves; L|T|R|B moves the whole box
        bool drawing;       // drawing a new box from dragStart
        Point dragStart;    // image coordinates
        Rectangle dragOrig;

        public event EventHandler CropChanged;

        public CropCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(64, 64, 64);
        }

        public Image Image
        {
            get { return image; }
            set { image = value; Invalidate(); }
        }

        /// <summary>The crop box in image pixels.</summary>
        public Rectangle Crop
        {
            get { return crop; }
            set
            {
                Rectangle r = value;
                if (r.Width < 0) { r.X += r.Width; r.Width = -r.Width; }
                if (r.Height < 0) { r.Y += r.Height; r.Height = -r.Height; }
                if (r == crop) return;
                crop = r;
                Invalidate();
                if (CropChanged != null) CropChanged(this, EventArgs.Empty);
            }
        }

        float Scale(out PointF origin)
        {
            origin = PointF.Empty;
            if (image == null) return 1;
            float s = Math.Min((ClientSize.Width - 20f) / image.Width, (ClientSize.Height - 20f) / image.Height);
            if (s >= 1) s = (float)Math.Floor(s); // whole-number zoom keeps pixels square
            if (s <= 0) s = 0.05f;
            origin = new PointF((ClientSize.Width - image.Width * s) / 2, (ClientSize.Height - image.Height * s) / 2);
            return s;
        }

        RectangleF ToScreen(Rectangle r)
        {
            PointF o;
            float s = Scale(out o);
            return new RectangleF(o.X + r.X * s, o.Y + r.Y * s, r.Width * s, r.Height * s);
        }

        Point ToImage(Point p)
        {
            PointF o;
            float s = Scale(out o);
            int x = (int)Math.Round((p.X - o.X) / s);
            int y = (int)Math.Round((p.Y - o.Y) / s);
            return new Point(Math.Max(0, Math.Min(image.Width, x)), Math.Max(0, Math.Min(image.Height, y)));
        }

        int HitTest(Point p)
        {
            if (crop.Width <= 0 || crop.Height <= 0) return 0;
            RectangleF r = ToScreen(crop);
            bool nearL = Math.Abs(p.X - r.Left) <= Grip, nearR = Math.Abs(p.X - r.Right) <= Grip;
            bool nearT = Math.Abs(p.Y - r.Top) <= Grip, nearB = Math.Abs(p.Y - r.Bottom) <= Grip;
            bool inX = p.X >= r.Left - Grip && p.X <= r.Right + Grip;
            bool inY = p.Y >= r.Top - Grip && p.Y <= r.Bottom + Grip;
            int e = 0;
            if (inY && nearL) e |= L;
            if (inY && nearR) e |= R;
            if (inX && nearT) e |= T;
            if (inX && nearB) e |= B;
            if (e != 0) return e;
            if (r.Contains(p)) return L | T | R | B;
            return 0;
        }

        static Cursor CursorFor(int e)
        {
            switch (e)
            {
                case L | T | R | B: return Cursors.SizeAll;
                case L: case R: return Cursors.SizeWE;
                case T: case B: return Cursors.SizeNS;
                case L | T: case R | B: return Cursors.SizeNWSE;
                case R | T: case L | B: return Cursors.SizeNESW;
                default: return Cursors.Cross;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (image == null || e.Button != MouseButtons.Left) return;
            Focus();
            dragStart = ToImage(e.Location);
            dragOrig = crop;
            dragEdges = HitTest(e.Location);
            // A box covering the whole image can't be moved, so a drag inside it draws a new one.
            if (dragEdges == (L | T | R | B) && crop == new Rectangle(Point.Empty, image.Size)) dragEdges = 0;
            drawing = dragEdges == 0;
            if (drawing) Crop = new Rectangle(dragStart, Size.Empty);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (image == null) return;
            if (!Capture || (e.Button & MouseButtons.Left) == 0)
            {
                Cursor = CursorFor(HitTest(e.Location));
                return;
            }
            Point p = ToImage(e.Location);
            if (drawing)
            {
                Crop = Rectangle.FromLTRB(Math.Min(dragStart.X, p.X), Math.Min(dragStart.Y, p.Y), Math.Max(dragStart.X, p.X), Math.Max(dragStart.Y, p.Y));
                return;
            }
            int dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
            if (dragEdges == (L | T | R | B))
            {
                int x = Math.Max(0, Math.Min(image.Width - dragOrig.Width, dragOrig.X + dx));
                int y = Math.Max(0, Math.Min(image.Height - dragOrig.Height, dragOrig.Y + dy));
                Crop = new Rectangle(x, y, dragOrig.Width, dragOrig.Height);
                return;
            }
            int l = dragOrig.Left, t = dragOrig.Top, r = dragOrig.Right, b = dragOrig.Bottom;
            if ((dragEdges & L) != 0) l = Math.Min(r - 1, l + dx);
            if ((dragEdges & R) != 0) r = Math.Max(l + 1, r + dx);
            if ((dragEdges & T) != 0) t = Math.Min(b - 1, t + dy);
            if ((dragEdges & B) != 0) b = Math.Max(t + 1, b + dy);
            Crop = Rectangle.FromLTRB(Math.Max(0, l), Math.Max(0, t), Math.Min(image.Width, r), Math.Min(image.Height, b));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            drawing = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (image == null)
            {
                TextRenderer.DrawText(e.Graphics, "Choose a folder of PNGs, then drag a box over the area to keep.", Font,
                    ClientRectangle, Color.Silver, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            RectangleF all = ToScreen(new Rectangle(Point.Empty, image.Size));
            g.DrawImage(image, all);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            if (crop.Width <= 0 || crop.Height <= 0) return;

            RectangleF r = ToScreen(crop);
            using (Region outside = new Region(all))
            using (Brush shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                outside.Exclude(r);
                g.FillRegion(shade, outside);
            }
            using (Pen dark = new Pen(Color.Black, 3))
                g.DrawRectangle(dark, r.X, r.Y, r.Width, r.Height);
            using (Pen light = new Pen(Color.Yellow, 1))
                g.DrawRectangle(light, r.X, r.Y, r.Width, r.Height);
            float[] xs = { r.Left, r.Left + r.Width / 2, r.Right };
            float[] ys = { r.Top, r.Top + r.Height / 2, r.Bottom };
            foreach (float x in xs)
                foreach (float y in ys)
                {
                    if (x == xs[1] && y == ys[1]) continue;
                    g.FillRectangle(Brushes.Yellow, x - 3, y - 3, 7, 7);
                    g.DrawRectangle(Pens.Black, x - 3, y - 3, 7, 7);
                }
            string label = string.Format("{0} x {1} at ({2}, {3})", crop.Width, crop.Height, crop.X, crop.Y);
            SizeF ls = g.MeasureString(label, Font);
            float ly = r.Top - ls.Height - 4 >= 0 ? r.Top - ls.Height - 4 : r.Bottom + 4;
            g.FillRectangle(Brushes.Black, r.Left, ly, ls.Width + 4, ls.Height);
            g.DrawString(label, Font, Brushes.Yellow, r.Left + 2, ly);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }
}
