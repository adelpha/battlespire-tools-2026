using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CropTool
{
    /// <summary>One folder that directly holds PNGs.</summary>
    class ImageFolder
    {
        public string Dir;
        public string Rel;          // relative to the input folder; "" for the input folder itself
        public List<string> Files = new List<string>();
        public bool FitKnown;       // Fit has been worked out
        public Rectangle Fit;       // union of the visible pixels of every PNG; empty if all transparent
        public int Transparent;     // PNGs with no visible pixels
        public int Unreadable;

        public override string ToString()
        {
            return string.Format("{0}  ({1})", Rel.Length == 0 ? "(input folder itself)" : Rel, Files.Count);
        }
    }

    /// <summary>
    /// Batch cropper. Every folder of PNGs under the input folder is written to the same
    /// relative folder under the output folder. A ticked folder is cropped to the smallest
    /// box that holds the visible pixels of all its own PNGs; an unticked folder is
    /// cropped to the box drawn by hand.
    /// </summary>
    public class MainForm : Form
    {
        readonly TextBox folderBox = new TextBox();
        readonly CheckBox recurse = new CheckBox();
        readonly TextBox outBox = new TextBox();
        readonly CheckedListBox folderList = new CheckedListBox();
        readonly ListBox fileList = new ListBox();
        readonly CropCanvas canvas = new CropCanvas();
        readonly NumericUpDown xBox = new NumericUpDown(), yBox = new NumericUpDown(), wBox = new NumericUpDown(), hBox = new NumericUpDown();
        readonly CheckBox overwrite = new CheckBox();
        readonly NumericUpDown minAlphaBox = new NumericUpDown();
        readonly Button runButton = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly TextBox log = new TextBox();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly List<ImageFolder> folders = new List<ImageFolder>();
        string root;
        bool syncing;

        public MainForm(string path)
        {
            Text = "Battlespire PNG Batch Crop";
            Size = new Size(1180, 800);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f.Length > 0) OpenFolder(Directory.Exists(f[0]) ? f[0] : Path.GetDirectoryName(f[0]));
            };

            // Top: input and output folders.
            Panel inRow = PathRow("Input:", folderBox, delegate { BrowseFolder(); });
            folderBox.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { OpenFolder(folderBox.Text); e.SuppressKeyPress = true; } };
            recurse.Text = "Include subfolders";
            recurse.Checked = true;
            recurse.Width = 140;
            recurse.Dock = DockStyle.Right;
            recurse.Padding = new Padding(10, 0, 0, 0);
            recurse.CheckedChanged += delegate { if (root != null) OpenFolder(root); };
            inRow.Controls.Add(recurse);
            folderBox.BringToFront();
            Panel outRow = PathRow("Output:", outBox, delegate { BrowseOutput(); });
            overwrite.Text = "Overwrite the originals instead";
            overwrite.Width = 210;
            overwrite.Dock = DockStyle.Right;
            overwrite.Padding = new Padding(10, 0, 0, 0);
            overwrite.CheckedChanged += delegate { outBox.Enabled = !overwrite.Checked; };
            outRow.Controls.Add(overwrite);
            outBox.BringToFront();

            // Left: folders (ticked = fit automatically) and the PNGs of the selected one.
            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = 290;
            left.Padding = new Padding(6, 0, 0, 6);
            folderList.Dock = DockStyle.Top;
            folderList.Height = 300;
            folderList.IntegralHeight = false;
            folderList.CheckOnClick = true;
            folderList.HorizontalScrollbar = true;
            folderList.SelectedIndexChanged += delegate { ShowFolder(folderList.SelectedIndex); };
            folderList.ItemCheck += delegate { BeginInvoke(new MethodInvoker(delegate { ShowFolder(folderList.SelectedIndex); })); };
            FlowLayoutPanel tickRow = new FlowLayoutPanel();
            tickRow.Dock = DockStyle.Top;
            tickRow.Height = 30;
            Button tickAll = new Button();
            tickAll.Text = "Tick all";
            tickAll.Click += delegate { SetAllTicks(true); };
            Button untickAll = new Button();
            untickAll.Text = "Untick all";
            untickAll.Click += delegate { SetAllTicks(false); };
            tickRow.Controls.Add(tickAll);
            tickRow.Controls.Add(untickAll);
            fileList.Dock = DockStyle.Fill;
            fileList.IntegralHeight = false;
            fileList.HorizontalScrollbar = true;
            fileList.SelectedIndexChanged += delegate { ShowSample(); };
            left.Controls.Add(fileList);
            left.Controls.Add(TopLabel("PNGs in the selected folder"));
            left.Controls.Add(tickRow);
            left.Controls.Add(folderList);
            left.Controls.Add(TopLabel("Folders (ticked = fit to its own visible pixels)"));

            // Right: hand-drawn box, run and log.
            FlowLayoutPanel right = new FlowLayoutPanel();
            right.Dock = DockStyle.Right;
            right.Width = 260;
            right.FlowDirection = FlowDirection.TopDown;
            right.WrapContents = false;
            right.Padding = new Padding(6, 0, 6, 6);

            right.Controls.Add(Caption("How folders are cropped"));
            right.Controls.Add(Hint("Ticked folders: each is cropped to the smallest box that holds every visible pixel of its own PNGs (shown in cyan)."));
            right.Controls.Add(Hint("Unticked folders: cropped to the yellow box you draw. Drag on the image to draw it; drag inside or on its edges to move or resize it."));
            right.Controls.Add(Row("Minimum opacity", minAlphaBox, 120));
            right.Controls.Add(Hint("Pixels with alpha below this (1 to 255) are ignored when fitting. 1 counts every pixel that isn't fully transparent; 255 counts only fully opaque ones."));
            minAlphaBox.Minimum = 1;
            minAlphaBox.Maximum = 255;
            minAlphaBox.Value = 1;
            minAlphaBox.Width = 80;
            minAlphaBox.ValueChanged += delegate
            {
                foreach (ImageFolder f in folders) f.FitKnown = false;
                ShowFolder(folderList.SelectedIndex);
            };
            right.Controls.Add(Caption("Yellow box (pixels)"));
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
            whole.Width = 240;
            whole.Click += delegate { if (canvas.Image != null) canvas.Crop = new Rectangle(Point.Empty, canvas.Image.Size); };
            right.Controls.Add(whole);

            runButton.Text = "Crop every folder";
            runButton.Width = 240;
            runButton.Height = 32;
            runButton.Margin = new Padding(3, 14, 3, 3);
            runButton.Enabled = false;
            runButton.Click += delegate { RunCrop(); };
            right.Controls.Add(runButton);
            progress.Width = 240;
            right.Controls.Add(progress);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Both;
            log.WordWrap = false;
            log.Width = 240;
            log.Height = 220;
            log.BackColor = SystemColors.Window;
            right.Controls.Add(log);

            canvas.Dock = DockStyle.Fill;
            canvas.CropChanged += delegate { CropChangedOnCanvas(); };

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "Choose the input folder that holds the folders of frames.";
            bar.Items.Add(status);

            Controls.Add(canvas);
            Controls.Add(left);
            Controls.Add(right);
            Controls.Add(outRow);
            Controls.Add(inRow);
            Controls.Add(bar);

            Load += delegate { if (!string.IsNullOrEmpty(path)) OpenFolder(path); };
        }

        static Panel PathRow(string label, TextBox box, EventHandler browse)
        {
            Panel row = new Panel();
            row.Dock = DockStyle.Top;
            row.Height = 30;
            row.Padding = new Padding(6, 4, 6, 2);
            Label l = new Label();
            l.Text = label;
            l.Width = 55;
            l.Dock = DockStyle.Left;
            l.TextAlign = ContentAlignment.MiddleLeft;
            box.Dock = DockStyle.Fill;
            Button b = new Button();
            b.Text = "Browse...";
            b.Dock = DockStyle.Right;
            b.Width = 80;
            b.Click += browse;
            row.Controls.Add(l);
            row.Controls.Add(b);
            row.Controls.Add(box);
            return row;
        }

        static Label TopLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Top;
            l.Height = 20;
            l.TextAlign = ContentAlignment.BottomLeft;
            return l;
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
            l.MaximumSize = new Size(240, 0);
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 2, 3, 4);
            return l;
        }

        static Control Row(string label, Control c)
        {
            return Row(label, c, 60);
        }

        static Control Row(string label, Control c, int labelWidth)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.Margin = new Padding(0);
            Label l = new Label();
            l.Text = label;
            l.Width = labelWidth;
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
                dlg.Description = "Choose the input folder (for example the folder you exported FLC frames to).";
                if (root != null) dlg.SelectedPath = root;
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenFolder(dlg.SelectedPath);
            }
        }

        void BrowseOutput()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Write the cropped folders (same names and layout as the input) to:";
                if (outBox.Text.Length > 0 && Directory.Exists(outBox.Text)) dlg.SelectedPath = outBox.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) { outBox.Text = dlg.SelectedPath; overwrite.Checked = false; }
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
            string newRoot = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (outBox.Text.Length == 0 || root == null || outBox.Text == root + "_cropped") outBox.Text = newRoot + "_cropped";
            root = newRoot;
            folderBox.Text = root;
            folders.Clear();
            Dictionary<string, ImageFolder> byDir = new Dictionary<string, ImageFolder>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string f in Directory.GetFiles(root, "*", recurse.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    if (!IsImage(f)) continue;
                    string dir = Path.GetDirectoryName(f);
                    ImageFolder g;
                    if (!byDir.TryGetValue(dir, out g))
                    {
                        g = new ImageFolder();
                        g.Dir = dir;
                        g.Rel = Relative(dir);
                        byDir.Add(dir, g);
                        folders.Add(g);
                    }
                    g.Files.Add(f);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not list " + root + ":\r\n" + ex.Message, Text);
            }
            folders.Sort(delegate(ImageFolder a, ImageFolder b) { return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase); });
            int total = 0;
            folderList.BeginUpdate();
            folderList.Items.Clear();
            foreach (ImageFolder g in folders)
            {
                g.Files.Sort(StringComparer.OrdinalIgnoreCase);
                total += g.Files.Count;
                folderList.Items.Add(g, true);
            }
            folderList.EndUpdate();
            runButton.Enabled = folders.Count > 0;
            status.Text = string.Format("{0} PNG(s) in {1} folder(s) under {2}", total, folders.Count, root);
            if (folders.Count > 0) folderList.SelectedIndex = 0;
            else { fileList.Items.Clear(); canvas.Image = null; canvas.AutoBox = Rectangle.Empty; }
        }

        string Relative(string path)
        {
            return path.Length > root.Length ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
        }

        void SetAllTicks(bool on)
        {
            for (int i = 0; i < folderList.Items.Count; i++) folderList.SetItemChecked(i, on);
        }

        /// <summary>Loads an image without keeping the file open, so it can be overwritten.</summary>
        static Bitmap LoadImage(string path)
        {
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        ImageFolder SelectedFolder
        {
            get { int i = folderList.SelectedIndex; return i >= 0 && i < folders.Count ? folders[i] : null; }
        }

        void ShowFolder(int index)
        {
            if (index < 0 || index >= folders.Count) return;
            ImageFolder g = folders[index];
            if (fileList.Tag != g)
            {
                fileList.Tag = g;
                fileList.BeginUpdate();
                fileList.Items.Clear();
                foreach (string f in g.Files) fileList.Items.Add(Path.GetFileName(f));
                fileList.EndUpdate();
                if (g.Files.Count > 0) fileList.SelectedIndex = 0;
            }
            if (folderList.GetItemChecked(index))
            {
                ComputeFit(g);
                canvas.AutoBox = g.Fit;
                status.Text = DescribeFit(g);
            }
            else
            {
                canvas.AutoBox = Rectangle.Empty;
                status.Text = (g.Rel.Length == 0 ? "Input folder" : g.Rel) + ": unticked, so it will be cropped to the yellow box.";
            }
        }

        static string DescribeFit(ImageFolder g)
        {
            string name = g.Rel.Length == 0 ? "Input folder" : g.Rel;
            if (g.Fit.IsEmpty) return name + ": no pixel in any PNG reaches the minimum opacity, so it will be skipped.";
            string s = string.Format("{0}: fitted box {1} x {2} at ({3}, {4}) from {5} PNG(s).", name, g.Fit.Width, g.Fit.Height, g.Fit.X, g.Fit.Y, g.Files.Count);
            if (g.Transparent > 0) s += string.Format(" {0} with no pixel at the minimum opacity.", g.Transparent);
            if (g.Unreadable > 0) s += string.Format(" {0} unreadable.", g.Unreadable);
            return s;
        }

        void ShowSample()
        {
            ImageFolder g = fileList.Tag as ImageFolder;
            int i = fileList.SelectedIndex;
            if (g == null || i < 0 || i >= g.Files.Count) return;
            try
            {
                Bitmap b = LoadImage(g.Files[i]);
                Image old = canvas.Image;
                canvas.Image = b;
                if (old != null) old.Dispose();
            }
            catch (Exception ex)
            {
                status.Text = Path.GetFileName(g.Files[i]) + ": " + ex.Message;
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

        /// <summary>Bounds of the non-transparent pixels in one image, or an empty rectangle if it has none.</summary>
        static Rectangle VisibleBounds(Bitmap b, int minAlpha)
        {
            int w = b.Width, h = b.Height;
            BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] row = new int[w];
            int minX = w, minY = h, maxX = -1, maxY = -1;
            try
            {
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, w);
                    int first = -1, last = -1;
                    for (int x = 0; x < w; x++)
                        if ((int)((uint)row[x] >> 24) >= minAlpha) { if (first < 0) first = x; last = x; }
                    if (first < 0) continue;
                    if (first < minX) minX = first;
                    if (last > maxX) maxX = last;
                    if (y < minY) minY = y;
                    maxY = y;
                }
            }
            finally { b.UnlockBits(bd); }
            return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        /// <summary>Works out (once) the box that holds the visible pixels of every PNG in a folder.</summary>
        void ComputeFit(ImageFolder g)
        {
            if (g.FitKnown) return;
            Rectangle union = Rectangle.Empty;
            int minAlpha = (int)minAlphaBox.Value;
            g.Transparent = g.Unreadable = 0;
            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                for (int i = 0; i < g.Files.Count; i++)
                {
                    try
                    {
                        using (Bitmap b = LoadImage(g.Files[i]))
                        {
                            Rectangle r = VisibleBounds(b, minAlpha);
                            if (r.IsEmpty) g.Transparent++;
                            else union = union.IsEmpty ? r : Rectangle.Union(union, r);
                        }
                    }
                    catch (Exception) { g.Unreadable++; }
                }
            }
            finally { Cursor = old; }
            g.Fit = union;
            g.FitKnown = true;
        }

        void RunCrop()
        {
            Rectangle manual = canvas.Crop;
            List<ImageFolder> todo = new List<ImageFolder>();
            bool needManual = false;
            for (int i = 0; i < folders.Count; i++)
            {
                todo.Add(folders[i]);
                if (!folderList.GetItemChecked(i)) needManual = true;
            }
            if (needManual && (manual.Width <= 0 || manual.Height <= 0))
            {
                MessageBox.Show(this, "Some folders are unticked, so they use the yellow box, but no box is drawn. Draw one, or tick every folder.", Text);
                return;
            }
            string outRoot = null;
            if (!overwrite.Checked)
            {
                outRoot = outBox.Text.Trim();
                if (outRoot.Length == 0) { MessageBox.Show(this, "Choose an output folder.", Text); return; }
                outRoot = Path.GetFullPath(outRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(outRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "The output folder is the input folder. Choose another one, or tick \"Overwrite the originals instead\".", Text);
                    return;
                }
                // Leave out anything that is already inside the output folder.
                todo.RemoveAll(delegate(ImageFolder g)
                {
                    return string.Equals(g.Dir, outRoot, StringComparison.OrdinalIgnoreCase)
                        || g.Dir.StartsWith(outRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                });
            }
            else if (MessageBox.Show(this, "Crop every PNG in " + todo.Count + " folder(s) and overwrite the originals?\r\n\r\nThis cannot be undone.",
                         Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            int totalFiles = 0;
            foreach (ImageFolder g in todo) totalFiles += g.Files.Count;
            progress.Maximum = Math.Max(1, totalFiles * 2);
            progress.Value = 0;
            StringBuilder report = new StringBuilder();
            int done = 0, failed = 0, skippedFolders = 0, step = 0;
            Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (ImageFolder g in todo)
                {
                    string name = g.Rel.Length == 0 ? "(input folder)" : g.Rel;
                    bool auto = folderList.GetItemChecked(folders.IndexOf(g));
                    status.Text = (auto ? "Fitting and cropping " : "Cropping ") + name + "...";
                    Application.DoEvents();
                    if (auto) ComputeFit(g);
                    step += g.Files.Count;
                    progress.Value = Math.Min(progress.Maximum, step);
                    Rectangle box = auto ? g.Fit : manual;
                    if (box.Width <= 0 || box.Height <= 0)
                    {
                        report.AppendFormat("{0}: skipped, no pixel reaches the minimum opacity\r\n", name);
                        skippedFolders++;
                        step += g.Files.Count;
                        continue;
                    }
                    int ok = 0, bad = 0;
                    string outDir = outRoot == null ? g.Dir : (g.Rel.Length == 0 ? outRoot : Path.Combine(outRoot, g.Rel));
                    foreach (string src in g.Files)
                    {
                        try
                        {
                            using (Bitmap b = LoadImage(src))
                            {
                                Rectangle r = Rectangle.Intersect(box, new Rectangle(Point.Empty, b.Size));
                                if (r.Width <= 0 || r.Height <= 0) throw new InvalidOperationException("the box lies outside this image");
                                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                                using (Bitmap c = b.Clone(r, b.PixelFormat))
                                    c.Save(Path.Combine(outDir, Path.GetFileName(src)), ImageFormat.Png);
                                ok++;
                            }
                        }
                        catch (Exception ex)
                        {
                            bad++;
                            report.AppendFormat("  {0}: {1}\r\n", Path.GetFileName(src), ex.Message);
                        }
                        progress.Value = Math.Min(progress.Maximum, ++step);
                        if (step % 10 == 0) Application.DoEvents();
                    }
                    done += ok;
                    failed += bad;
                    report.AppendFormat("{0}: {1} PNG(s) cropped to {2} x {3} at ({4}, {5}){6}{7}\r\n", name, ok, box.Width, box.Height, box.X, box.Y,
                        auto ? ", fitted" : ", yellow box", bad > 0 ? ", " + bad + " failed" : "");
                    // The originals have changed size, so a fitted box must be worked out again.
                    if (outRoot == null) g.FitKnown = false;
                }
            }
            finally
            {
                Enabled = true;
                Cursor = Cursors.Default;
            }
            log.Text = report.ToString();
            status.Text = string.Format("Cropped {0} PNG(s) in {1} folder(s){2}{3}{4}.", done, todo.Count - skippedFolders,
                outRoot != null ? " into " + outRoot : "",
                skippedFolders > 0 ? "; " + skippedFolders + " folder(s) skipped" : "",
                failed > 0 ? "; " + failed + " failed (see the list on the right)" : "");
            if (outRoot == null) ShowSample();
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

        Rectangle autoBox;

        /// <summary>A box drawn for reference only (the fitted box of a ticked folder).</summary>
        public Rectangle AutoBox
        {
            get { return autoBox; }
            set { if (value != autoBox) { autoBox = value; Invalidate(); } }
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
            // Checkerboard behind the image so transparent areas show up.
            using (HatchBrush checks = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(204, 204, 204), Color.White))
                g.FillRectangle(checks, all);
            g.DrawImage(image, all);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            if (autoBox.Width > 0 && autoBox.Height > 0)
            {
                RectangleF a = ToScreen(autoBox);
                using (Pen cyan = new Pen(Color.Cyan, 2))
                {
                    cyan.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(cyan, a.X, a.Y, a.Width, a.Height);
                }
                string al = string.Format("fitted {0} x {1} at ({2}, {3})", autoBox.Width, autoBox.Height, autoBox.X, autoBox.Y);
                SizeF als = g.MeasureString(al, Font);
                float aly = a.Bottom + 4 + als.Height <= ClientSize.Height ? a.Bottom + 4 : a.Top - als.Height - 4;
                g.FillRectangle(Brushes.Black, a.Left, aly, als.Width + 4, als.Height);
                g.DrawString(al, Font, Brushes.Cyan, a.Left + 2, aly);
            }
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
