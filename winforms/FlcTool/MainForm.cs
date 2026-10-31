using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Battlespire.Formats;
using Battlespire.Formats.Ui;

namespace FlcTool
{
    /// <summary>
    /// FLI/FLC animation viewer: plays the animations in FLC.BSA (or loose .FLC files),
    /// steps through frames, and exports frames as PNG.
    /// </summary>
    public class MainForm : Form
    {
        // The open archive (if any) and its FLC entries.
        BsaArchive archive;
        readonly List<BsaEntry> entries = new List<BsaEntry>();

        FlcFile flc;
        FlcDecoder decoder;
        string currentName;
        bool updatingBar;

        readonly PixelView view = new PixelView();
        readonly ComboBox animBox = new ComboBox();
        readonly TrackBar frameBar = new TrackBar();
        readonly Label frameLabel = new Label();
        readonly CheckBox play = new CheckBox();
        readonly NumericUpDown zoomBox = new NumericUpDown();
        readonly TextBox info = new TextBox();
        readonly Timer timer = new Timer();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();

        public MainForm(string path)
        {
            Text = "Battlespire FLC Viewer";
            Size = new Size(1000, 720);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) OpenPath(f[0]); };

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open (FLC or BSA)...", null, delegate { PromptOpen(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("&Save frame as PNG...", null, delegate { ExportFrame(); }, Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripMenuItem("Save all frames as separate &PNGs...", null, delegate { ExportFrames(); }, Keys.Control | Keys.E));
            file.DropDownItems.Add(new ToolStripMenuItem("Export every animation in the &archive...", null, delegate { ExportArchive(); }));
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

            animBox.DropDownStyle = ComboBoxStyle.DropDownList;
            animBox.Width = 230;
            animBox.MaxDropDownItems = 30;
            animBox.SelectedIndexChanged += delegate { OpenEntry(animBox.SelectedIndex); };

            frameBar.Width = 230;
            frameBar.Minimum = 0;
            frameBar.TickStyle = TickStyle.None;
            frameBar.ValueChanged += delegate { if (!updatingBar) ShowFrame(frameBar.Value); };

            play.Text = "Play";
            play.AutoSize = true;
            play.CheckedChanged += delegate { UpdateTimer(); };
            timer.Tick += delegate { NextFrame(); };

            zoomBox.Minimum = PixelView.MinZoom;
            zoomBox.Maximum = PixelView.MaxZoom;
            zoomBox.Increment = PixelView.ZoomStep;
            zoomBox.Value = 100;
            zoomBox.Width = 70;
            zoomBox.ValueChanged += delegate { view.Zoom = (int)zoomBox.Value; };
            view.ZoomChanged += delegate { if (zoomBox.Value != view.Zoom) zoomBox.Value = view.Zoom; };

            info.Multiline = true;
            info.ReadOnly = true;
            info.ScrollBars = ScrollBars.Vertical;
            info.Width = 230;
            info.Height = 200;
            info.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            info.BackColor = SystemColors.Window;

            Button saveFrame = new Button();
            saveFrame.Text = "Save frame as PNG...";
            saveFrame.Width = 230;
            saveFrame.Click += delegate { ExportFrame(); };
            Button saveAll = new Button();
            saveAll.Text = "Save all frames as PNGs...";
            saveAll.Width = 230;
            saveAll.Click += delegate { ExportFrames(); };

            controls.Controls.Add(Caption("Animation"));
            controls.Controls.Add(animBox);
            controls.Controls.Add(frameLabel);
            controls.Controls.Add(frameBar);
            controls.Controls.Add(play);
            controls.Controls.Add(Caption("Zoom % (mouse wheel)"));
            controls.Controls.Add(zoomBox);
            controls.Controls.Add(Caption("Details"));
            controls.Controls.Add(info);
            controls.Controls.Add(saveFrame);
            controls.Controls.Add(saveAll);

            view.Dock = DockStyle.Fill;
            view.Zoom = 100;

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            bar.Items.Add(status);

            Controls.Add(view);
            Controls.Add(controls);
            Controls.Add(menu);
            Controls.Add(bar);
            MainMenuStrip = menu;

            UpdateFrameLabel();
            Load += delegate { if (!string.IsNullOrEmpty(path)) OpenPath(path); };
            FormClosed += delegate { CloseArchive(); };
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
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Open animation";
                dlg.Filter = "Animations and archives (*.flc;*.fli;*.bsa)|*.flc;*.fli;*.bsa|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenPath(dlg.FileName);
            }
        }

        void OpenPath(string path)
        {
            try
            {
                if (FileSource.IsArchive(path)) OpenArchive(path);
                else OpenLoose(path);
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open " + path + ".", ex); }
        }

        void CloseArchive()
        {
            if (archive != null) archive.Dispose();
            archive = null;
            entries.Clear();
        }

        /// <summary>Lists every FLC in an archive in the Animation box and shows the first.</summary>
        void OpenArchive(string path)
        {
            BsaArchive a = new BsaArchive(path);
            List<BsaEntry> found = new List<BsaEntry>();
            foreach (BsaEntry e in a.Entries)
                if (e.Name.EndsWith(".FLC", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".FLI", StringComparison.OrdinalIgnoreCase))
                    found.Add(e);
            if (found.Count == 0)
            {
                a.Dispose();
                throw new InvalidDataException(Path.GetFileName(path) + " holds no FLC animations.");
            }
            found.Sort(delegate(BsaEntry x, BsaEntry y) { return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
            CloseArchive();
            archive = a;
            entries.AddRange(found);
            animBox.BeginUpdate();
            animBox.Items.Clear();
            foreach (BsaEntry e in entries) animBox.Items.Add(e.Name);
            animBox.EndUpdate();
            animBox.Enabled = true;
            Text = "Battlespire FLC Viewer - " + Path.GetFileName(path);
            animBox.SelectedIndex = 0; // opens it
        }

        void OpenLoose(string path)
        {
            FlcFile f = FlcFile.Parse(File.ReadAllBytes(path));
            CloseArchive();
            animBox.Items.Clear();
            animBox.Items.Add(Path.GetFileName(path));
            animBox.Enabled = false;
            ShowAnimation(f, Path.GetFileName(path), path);
        }

        void OpenEntry(int index)
        {
            if (archive == null || index < 0 || index >= entries.Count) return;
            BsaEntry e = entries[index];
            try
            {
                ShowAnimation(FlcFile.Parse(archive.Read(e)), e.Name, archive.Path + " : " + e.Name);
            }
            catch (Exception ex)
            {
                flc = null;
                decoder = null;
                view.Image = null;
                info.Text = "Could not decode " + e.Name + ":\r\n" + ex.Message;
                status.Text = e.Name + ": " + ex.Message;
                UpdateFrameLabel();
            }
        }

        void ShowAnimation(FlcFile f, string name, string description)
        {
            flc = f;
            currentName = name;
            decoder = f.CreateDecoder();
            updatingBar = true;
            frameBar.Maximum = Math.Max(0, f.FrameCount - 1);
            frameBar.Value = 0;
            frameBar.Enabled = f.FrameCount > 1;
            updatingBar = false;
            play.Enabled = f.FrameCount > 1;
            info.Text = Describe(f, name);
            status.Text = description;
            ShowFrame(0);
            UpdateTimer();
        }

        static string Describe(FlcFile f, string name)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Name        {0}\r\n", name);
            sb.AppendFormat("Format      {0}\r\n", f.IsFlc ? "FLC" : "FLI");
            sb.AppendFormat("Size        {0} x {1}\r\n", f.Width, f.Height);
            sb.AppendFormat("Frames      {0}{1}\r\n", f.FrameCount, f.HasRingFrame ? " + ring frame" : "");
            sb.AppendFormat("Frame delay {0} ms ({1:0.#} fps)\r\n", f.FrameDelay, f.FrameDelay > 0 ? 1000.0 / f.FrameDelay : 0);
            sb.AppendFormat("Length      {0:0.0} s\r\n", f.FrameCount * f.FrameDelay / 1000.0);
            sb.AppendFormat("File size   {0:N0} bytes\r\n", f.Data.Length);
            foreach (string w in f.Warnings) sb.AppendLine("Warning: " + w);
            return sb.ToString();
        }

        void ShowFrame(int frame)
        {
            if (decoder == null || flc.FrameCount == 0) return;
            try
            {
                decoder.Seek(frame);
            }
            catch (Exception ex)
            {
                status.Text = "Frame " + frame + ": " + ex.Message;
                return;
            }
            Present();
        }

        void NextFrame()
        {
            if (decoder == null || flc.FrameCount < 2) return;
            decoder.Next();
            Present();
        }

        void Present()
        {
            Bitmap old = view.Image;
            view.Image = decoder.ToBitmap();
            if (old != null) old.Dispose();
            updatingBar = true;
            frameBar.Value = Math.Max(0, Math.Min(frameBar.Maximum, decoder.CurrentFrame));
            updatingBar = false;
            UpdateFrameLabel();
        }

        void UpdateFrameLabel()
        {
            frameLabel.Text = flc == null || decoder == null ? "Frame" : string.Format("Frame {0} of {1}", decoder.CurrentFrame + 1, flc.FrameCount);
            frameLabel.AutoSize = true;
            frameLabel.Margin = new Padding(3, 8, 3, 0);
        }

        void UpdateTimer()
        {
            timer.Stop();
            if (play.Checked && flc != null && flc.FrameCount > 1)
            {
                timer.Interval = Math.Max(10, flc.FrameDelay > 0 ? flc.FrameDelay : 70);
                timer.Start();
            }
        }

        string BaseName()
        {
            return Path.GetFileNameWithoutExtension(BsaArchive.SanitizeFileName(currentName ?? "animation"));
        }

        void ExportFrame()
        {
            if (decoder == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG image (*.png)|*.png";
                dlg.FileName = string.Format("{0}_{1:000}.png", BaseName(), decoder.CurrentFrame);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                using (Bitmap b = decoder.ToBitmap()) b.Save(dlg.FileName, ImageFormat.Png);
                status.Text = "Saved " + dlg.FileName;
            }
        }

        void ExportFrames()
        {
            if (flc == null) return;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Save every frame of " + currentName + " as a PNG in:";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    int n = SaveAllFrames(flc, dlg.SelectedPath, BaseName());
                    status.Text = string.Format("Saved {0} frames of {1} to {2}", n, currentName, dlg.SelectedPath);
                }
                catch (Exception ex) { FileSource.ShowError(this, "Export failed.", ex); }
                finally { Cursor = Cursors.Default; }
            }
        }

        /// <summary>Writes name_000.png, name_001.png ... for every frame. Uses its own decoder.</summary>
        static int SaveAllFrames(FlcFile f, string folder, string baseName)
        {
            FlcDecoder d = f.CreateDecoder();
            for (int i = 0; i < f.FrameCount; i++)
            {
                d.Seek(i);
                using (Bitmap b = d.ToBitmap())
                    b.Save(Path.Combine(folder, string.Format("{0}_{1:000}.png", baseName, i)), ImageFormat.Png);
            }
            return f.FrameCount;
        }

        void ExportArchive()
        {
            string bsaPath = archive != null ? archive.Path : null;
            if (bsaPath == null)
            {
                using (OpenFileDialog open = new OpenFileDialog())
                {
                    open.Filter = "BSA archives (*.bsa)|*.bsa|All files (*.*)|*.*";
                    open.Title = "Choose an archive of animations (e.g. FLC.BSA)";
                    if (open.ShowDialog(this) != DialogResult.OK) return;
                    bsaPath = open.FileName;
                }
            }
            string outDir;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Save the frames of every animation in " + Path.GetFileName(bsaPath) + " (one folder per animation) to:";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                outDir = dlg.SelectedPath;
            }
            bool wasPlaying = play.Checked;
            play.Checked = false;
            int anims = 0, frames = 0, failed = 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (BsaArchive bsa = new BsaArchive(bsaPath))
                {
                    int done = 0;
                    foreach (BsaEntry e in bsa.Entries)
                    {
                        done++;
                        if (!e.Name.EndsWith(".FLC", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".FLI", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            FlcFile f = FlcFile.Parse(bsa.Read(e));
                            string name = Path.GetFileNameWithoutExtension(BsaArchive.SanitizeFileName(e.Name));
                            string dir = Path.Combine(outDir, name);
                            Directory.CreateDirectory(dir);
                            frames += SaveAllFrames(f, dir, name);
                            anims++;
                        }
                        catch (Exception) { failed++; }
                        status.Text = string.Format("Exported {0} of {1} entries ({2} frames)...", done, bsa.Entries.Count, frames);
                        Application.DoEvents();
                    }
                }
            }
            catch (Exception ex) { FileSource.ShowError(this, "Export failed.", ex); }
            finally { Cursor = Cursors.Default; }
            status.Text = string.Format("Exported {0} frames from {1} animations to {2}{3}.", frames, anims, outDir,
                failed > 0 ? "; " + failed + " could not be decoded" : "");
            play.Checked = wasPlaying;
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
