using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Battlespire.Formats;
using Battlespire.Formats.Ui;

namespace BsiTool
{
    /// <summary>
    /// BSI image viewer: every image and frame in a file, the game's HICL palette
    /// (or HTBL shade levels / CMAP), animation, chunk listing, and PNG export.
    /// </summary>
    public class MainForm : Form
    {
        BsiFile bsi;
        LoadedFile source;
        readonly PixelView view = new PixelView();
        readonly ComboBox imageBox = new ComboBox();
        readonly ComboBox paletteBox = new ComboBox();
        readonly TrackBar frameBar = new TrackBar();
        readonly TrackBar shadeBar = new TrackBar();
        readonly NumericUpDown zoomBox = new NumericUpDown();
        readonly CheckBox transparent = new CheckBox();
        readonly CheckBox animate = new CheckBox();
        readonly Label frameLabel = new Label();
        readonly Label shadeLabel = new Label();
        readonly TextBox info = new TextBox();
        readonly Timer timer = new Timer();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();

        BsiImage Current
        {
            get { return bsi != null && imageBox.SelectedIndex >= 0 && imageBox.SelectedIndex < bsi.Images.Count ? bsi.Images[imageBox.SelectedIndex] : null; }
        }

        public MainForm(string path)
        {
            Text = "Battlespire BSI Viewer";
            Size = new Size(980, 700);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) OpenPath(f[0]); };

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open (BSI or BSA)...", null, delegate { PromptOpen(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("&Save frame as PNG...", null, delegate { ExportFrame(); }, Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripMenuItem("Save all frames as &strip PNG...", null, delegate { ExportStrip(); }));
            file.DropDownItems.Add(new ToolStripMenuItem("Save all frames as separate &PNGs...", null, delegate { ExportFrames(); }));
            file.DropDownItems.Add(new ToolStripMenuItem("Export a whole &BSA to PNG...", null, delegate { ExportArchive(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, delegate { Close(); }));
            menu.Items.Add(file);

            FlowLayoutPanel controls = new FlowLayoutPanel();
            controls.Dock = DockStyle.Left;
            controls.Width = 260;
            controls.FlowDirection = FlowDirection.TopDown;
            controls.WrapContents = false;
            controls.AutoScroll = true;
            controls.Padding = new Padding(6);

            imageBox.DropDownStyle = ComboBoxStyle.DropDownList;
            imageBox.Width = 230;
            imageBox.SelectedIndexChanged += delegate { ImageChanged(); };

            paletteBox.DropDownStyle = ComboBoxStyle.DropDownList;
            paletteBox.Width = 230;
            paletteBox.Items.AddRange(new object[] { "HICL (game colours)", "HTBL shade level", "CMAP (VGA palette)", "Greyscale indices" });
            paletteBox.SelectedIndex = 0;
            paletteBox.SelectedIndexChanged += delegate { shadeBar.Enabled = paletteBox.SelectedIndex == 1; Render(); };

            frameBar.Width = 230;
            frameBar.Minimum = 0;
            frameBar.TickStyle = TickStyle.None;
            frameBar.ValueChanged += delegate { Render(); };

            shadeBar.Width = 230;
            shadeBar.Minimum = 0;
            shadeBar.Maximum = 31;
            shadeBar.Value = 31;
            shadeBar.TickStyle = TickStyle.None;
            shadeBar.Enabled = false;
            shadeBar.ValueChanged += delegate { Render(); };

            zoomBox.Minimum = PixelView.MinZoom;
            zoomBox.Maximum = PixelView.MaxZoom;
            zoomBox.Increment = PixelView.ZoomStep;
            zoomBox.Value = 200;
            zoomBox.Width = 70;
            zoomBox.ValueChanged += delegate { view.Zoom = (int)zoomBox.Value; };
            view.ZoomChanged += delegate { if (zoomBox.Value != view.Zoom) zoomBox.Value = view.Zoom; };

            transparent.Text = "Colour 0 is transparent";
            transparent.AutoSize = true;
            transparent.Checked = true;
            transparent.CheckedChanged += delegate { Render(); };

            animate.Text = "Animate";
            animate.AutoSize = true;
            animate.CheckedChanged += delegate { UpdateTimer(); };

            timer.Tick += delegate
            {
                BsiImage img = Current;
                if (img == null || img.FrameCount < 2) return;
                frameBar.Value = (frameBar.Value + 1) % img.FrameCount;
            };

            info.Multiline = true;
            info.ReadOnly = true;
            info.ScrollBars = ScrollBars.Vertical;
            info.Width = 230;
            info.Height = 260;
            info.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            info.BackColor = SystemColors.Window;

            controls.Controls.Add(Caption("Image"));
            controls.Controls.Add(imageBox);
            controls.Controls.Add(Caption("Palette"));
            controls.Controls.Add(paletteBox);
            controls.Controls.Add(shadeLabel);
            controls.Controls.Add(shadeBar);
            controls.Controls.Add(frameLabel);
            controls.Controls.Add(frameBar);
            controls.Controls.Add(animate);
            controls.Controls.Add(transparent);
            controls.Controls.Add(Caption("Zoom % (mouse wheel)"));
            controls.Controls.Add(zoomBox);
            controls.Controls.Add(Caption("Details"));
            controls.Controls.Add(info);

            view.Dock = DockStyle.Fill;
            view.Zoom = 200;

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            bar.Items.Add(status);

            Controls.Add(view);
            Controls.Add(controls);
            Controls.Add(menu);
            Controls.Add(bar);
            MainMenuStrip = menu;

            Load += delegate { if (!string.IsNullOrEmpty(path)) OpenPath(path); };
        }

        static Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 8, 3, 0);
            return l;
        }

        void PromptOpen()
        {
            try
            {
                LoadedFile f = FileSource.Prompt(this, "Open image", "BSI images (*.bsi)|*.bsi", new string[] { ".BSI" });
                if (f != null) Open(f);
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open the file.", ex); }
        }

        void OpenPath(string path)
        {
            try
            {
                if (string.Equals(Path.GetExtension(path), ".bsa", StringComparison.OrdinalIgnoreCase))
                {
                    LoadedFile f = FileSource.PickFromArchive(this, path, new string[] { ".BSI" });
                    if (f != null) Open(f);
                }
                else Open(FileSource.Load(path));
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open " + path + ".", ex); }
        }

        void Open(LoadedFile f)
        {
            BsiFile parsed = BsiFile.Parse(f.Data);
            if (parsed.Images.Count == 0)
                throw new InvalidDataException(parsed.Warning ?? "No images in this file.");
            bsi = parsed;
            source = f;
            Text = "Battlespire BSI Viewer - " + f.Name;
            imageBox.Items.Clear();
            for (int i = 0; i < bsi.Images.Count; i++)
            {
                BsiImage img = bsi.Images[i];
                imageBox.Items.Add(string.Format("{0}: {1}x{2}{3}{4}", i, img.Width, img.Height,
                    img.FrameCount > 1 ? " x" + img.FrameCount : "", string.IsNullOrEmpty(img.Name) ? "" : " " + img.Name));
            }
            imageBox.SelectedIndex = 0;
            status.Text = f.Description + (bsi.Warning != null ? "  (" + bsi.Warning + ")" : "");
        }

        void ImageChanged()
        {
            BsiImage img = Current;
            if (img == null) return;
            frameBar.Maximum = Math.Max(0, img.FrameCount - 1);
            frameBar.Value = 0;
            frameBar.Enabled = img.FrameCount > 1;
            animate.Enabled = img.FrameCount > 1;
            zoomBox.Value = PixelView.DefaultZoomFor(img.Width, img.Height);
            info.Text = Describe(img);
            transparent.Checked = (img.Flags & 1) != 0;
            UpdateTimer();
            Render();
        }

        void UpdateTimer()
        {
            BsiImage img = Current;
            timer.Enabled = false;
            if (img == null || !animate.Checked || img.FrameCount < 2) return;
            // FrameDelay is in game timer ticks; treat a tick as roughly a millisecond
            // (the default of 0x50 then gives ~12 frames per second).
            timer.Interval = Math.Max(15, img.EffectiveFrameDelay);
            timer.Enabled = true;
        }

        BsiPalette PaletteMode
        {
            get
            {
                switch (paletteBox.SelectedIndex)
                {
                    case 1: return BsiPalette.Htbl;
                    case 2: return BsiPalette.Cmap;
                    case 3: return BsiPalette.Greyscale;
                    default: return BsiPalette.Hicl;
                }
            }
        }

        void Render()
        {
            BsiImage img = Current;
            frameLabel.Text = img == null ? "Frame" : string.Format("Frame {0} of {1}", frameBar.Value + 1, img.FrameCount);
            shadeLabel.Text = "Shade level " + shadeBar.Value;
            if (img == null) return;
            if (!img.HasPalette(PaletteMode))
            {
                status.Text = "This image has no " + PaletteMode + " chunk.";
            }
            try
            {
                Bitmap old = view.Image;
                view.Image = img.RenderFrame(frameBar.Value, PaletteMode, shadeBar.Value, transparent.Checked);
                if (old != null) old.Dispose();
            }
            catch (Exception ex)
            {
                status.Text = "Could not decode: " + ex.Message;
            }
        }

        string Describe(BsiImage img)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Size        {0} x {1}\r\n", img.Width, img.Height);
            sb.AppendFormat("Frames      {0}\r\n", img.Frames);
            sb.AppendFormat("Frame delay {0}{1}\r\n", img.FrameDelay, img.FrameDelay == 0 ? " (game uses 80)" : "");
            sb.AppendFormat("Compression {0} ({1})\r\n", img.Compression, CompressionName(img.Compression));
            sb.AppendFormat("Position    {0}, {1}\r\n", img.XOffset, img.YOffset);
            sb.AppendFormat("Flags       {0} ({1})\r\n", img.Flags, (img.Flags & 1) != 0 ? "colour 0 transparent" : "opaque");
            sb.AppendFormat("Blend mode  {0} ({1})\r\n", img.BlendMode, img.BlendMode == 1 ? "water" : img.BlendMode == 2 ? "translucent" : img.BlendMode == 0 ? "normal" : "unknown");
            sb.AppendFormat("Scale       {0} ({1:0.###} units per texel)\r\n", img.Scale, img.Scale / 256.0);
            sb.AppendFormat("Colour slot {0}{1}\r\n", img.ColourSlot, img.ColourSlot == 0 ? " (assigned by the game)" : "");
            if (img.RuntimeData != 0) sb.AppendFormat("+10         {0} (normally 0)\r\n", img.RuntimeData);
            sb.AppendFormat("Palettes    {0}{1}{2}\r\n", img.Hicl != null ? "HICL " : "", img.Htbl != null ? "HTBL " : "", img.Cmap != null ? "CMAP" : "");
            if (!string.IsNullOrEmpty(img.Name)) sb.AppendFormat("Name        {0}\r\n", img.Name);
            sb.AppendLine();
            sb.AppendLine("Chunks:");
            foreach (BsiChunk c in bsi.Chunks) sb.AppendLine("  " + c);
            return sb.ToString();
        }

        static string CompressionName(int c)
        {
            switch (c)
            {
                case 0: return "raw";
                case 1: return "RLE";
                case 2: return "skip/copy runs";
                case 4:
                case 6: return "row table, RLE rows";
                default: return "unknown";
            }
        }

        string BaseName()
        {
            string n = source == null ? "image" : Path.GetFileNameWithoutExtension(source.Name);
            if (bsi != null && bsi.Images.Count > 1) n += "_" + imageBox.SelectedIndex;
            return n;
        }

        void SavePng(Bitmap bmp, string suggested)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG image (*.png)|*.png";
                dlg.FileName = suggested;
                if (dlg.ShowDialog(this) == DialogResult.OK) bmp.Save(dlg.FileName, ImageFormat.Png);
            }
        }

        void ExportFrame()
        {
            BsiImage img = Current;
            if (img == null) return;
            using (Bitmap b = img.RenderFrame(frameBar.Value, PaletteMode, shadeBar.Value, transparent.Checked))
                SavePng(b, BaseName() + (img.FrameCount > 1 ? "_f" + frameBar.Value : "") + ".png");
        }

        void ExportStrip()
        {
            BsiImage img = Current;
            if (img == null) return;
            using (Bitmap b = img.RenderStrip(PaletteMode, shadeBar.Value, transparent.Checked))
                SavePng(b, BaseName() + ".png");
        }

        void ExportFrames()
        {
            BsiImage img = Current;
            if (img == null) return;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                for (int f = 0; f < img.FrameCount; f++)
                    using (Bitmap b = img.RenderFrame(f, PaletteMode, shadeBar.Value, transparent.Checked))
                        b.Save(Path.Combine(dlg.SelectedPath, BaseName() + "_f" + f + ".png"), ImageFormat.Png);
                status.Text = "Saved " + img.FrameCount + " frames to " + dlg.SelectedPath;
            }
        }

        void ExportArchive()
        {
            string bsaPath;
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Filter = "BSA archives (*.bsa)|*.bsa";
                open.Title = "Choose an archive of BSI images (e.g. BSI.BSA)";
                if (open.ShowDialog(this) != DialogResult.OK) return;
                bsaPath = open.FileName;
            }
            string outDir;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Save PNGs (one strip per image) to:";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                outDir = dlg.SelectedPath;
            }
            int ok = 0, skipped = 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (BsaArchive bsa = new BsaArchive(bsaPath))
                {
                    foreach (BsaEntry e in bsa.Entries)
                    {
                        try
                        {
                            byte[] data = bsa.Read(e);
                            if (FileSniffer.Detect(e.Name, data) != FileKind.Bsi) { skipped++; continue; }
                            BsiFile f = BsiFile.Parse(data);
                            for (int i = 0; i < f.Images.Count; i++)
                            {
                                string name = Path.GetFileNameWithoutExtension(BsaArchive.SanitizeFileName(e.Name)) +
                                    (f.Images.Count > 1 ? "_" + i : "") + ".png";
                                using (Bitmap b = f.Images[i].RenderStrip(PaletteMode, shadeBar.Value, transparent.Checked))
                                    b.Save(Path.Combine(outDir, name), ImageFormat.Png);
                            }
                            ok++;
                        }
                        catch (Exception) { skipped++; }
                        if ((ok + skipped) % 50 == 0)
                        {
                            status.Text = string.Format("Exported {0} of {1}...", ok + skipped, bsa.Entries.Count);
                            Application.DoEvents();
                        }
                    }
                }
            }
            catch (Exception ex) { FileSource.ShowError(this, "Export failed.", ex); }
            finally { Cursor = Cursors.Default; }
            status.Text = string.Format("Exported {0} images to {1}; skipped {2} entries that are not images.", ok, outDir, skipped);
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
