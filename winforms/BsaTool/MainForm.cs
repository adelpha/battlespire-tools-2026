using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Text;
using System.Windows.Forms;
using Battlespire.Formats;
using Battlespire.Formats.Ui;

namespace BsaTool
{
    /// <summary>
    /// BSA archive browser: lists entries, previews images, models, text and sounds,
    /// and extracts single entries or the whole archive (decompressing LZSS entries).
    /// Replaces bsatool.py and the Linux-only bsafuse driver.
    /// </summary>
    public class MainForm : Form
    {
        BsaArchive archive;
        readonly ListView list = new ListView();
        readonly TextBox filter = new TextBox();
        readonly SplitContainer split = new SplitContainer();
        readonly PixelView imageView = new PixelView();
        readonly TextBox textView = new TextBox();
        readonly Panel soundPanel = new Panel();
        readonly Label soundLabel = new Label();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly Button openInViewer = new Button();
        List<BsaEntry> shown = new List<BsaEntry>();
        int sortColumn = -1;
        bool sortDescending;
        byte[] previewData;
        BsaEntry previewEntry;
        SoundPlayer player;

        public MainForm(string path)
        {
            Text = "Battlespire BSA Browser";
            Size = new Size(1000, 680);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) OpenArchive(f[0]); };

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open archive...", null, delegate { PromptOpen(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("&Extract selected...", null, delegate { ExtractSelected(); }, Keys.Control | Keys.E));
            file.DropDownItems.Add(new ToolStripMenuItem("Extract &all...", null, delegate { ExtractAll(); }));
            file.DropDownItems.Add(new ToolStripMenuItem("Save &listing...", null, delegate { SaveListing(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, delegate { Close(); }));
            menu.Items.Add(file);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Top;
            tools.Height = 32;
            tools.Padding = new Padding(4, 3, 4, 0);
            Label fl = new Label();
            fl.Text = "Filter:";
            fl.AutoSize = true;
            fl.Margin = new Padding(3, 6, 3, 0);
            tools.Controls.Add(fl);
            filter.Width = 200;
            filter.TextChanged += delegate { Fill(); };
            tools.Controls.Add(filter);
            Button extract = new Button();
            extract.Text = "Extract selected";
            extract.AutoSize = true;
            extract.Click += delegate { ExtractSelected(); };
            tools.Controls.Add(extract);
            openInViewer.Text = "Open in viewer";
            openInViewer.AutoSize = true;
            openInViewer.Enabled = false;
            openInViewer.Click += delegate { OpenInViewer(); };
            tools.Controls.Add(openInViewer);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.VirtualMode = true;
            list.Columns.Add("#", 50, HorizontalAlignment.Right);
            list.Columns.Add("Name", 150);
            list.Columns.Add("Size", 80, HorizontalAlignment.Right);
            list.Columns.Add("Compressed", 75);
            list.Columns.Add("Offset", 85, HorizontalAlignment.Right);
            list.RetrieveVirtualItem += RetrieveItem;
            list.SelectedIndexChanged += delegate { Preview(); };
            list.ColumnClick += delegate(object s, ColumnClickEventArgs e) { SortBy(e.Column); };
            list.DoubleClick += delegate { OpenInViewer(); };

            imageView.Dock = DockStyle.Fill;
            textView.Dock = DockStyle.Fill;
            textView.Multiline = true;
            textView.ReadOnly = true;
            textView.ScrollBars = ScrollBars.Both;
            textView.WordWrap = false;
            textView.Font = new Font(FontFamily.GenericMonospace, 9f);
            textView.BackColor = SystemColors.Window;

            soundPanel.Dock = DockStyle.Fill;
            soundLabel.Location = new Point(12, 12);
            soundLabel.AutoSize = true;
            Button play = new Button();
            play.Text = "Play";
            play.Location = new Point(12, 40);
            play.Click += delegate { PlaySound(); };
            Button stop = new Button();
            stop.Text = "Stop";
            stop.Location = new Point(92, 40);
            stop.Click += delegate { if (player != null) player.Stop(); };
            soundPanel.Controls.AddRange(new Control[] { soundLabel, play, stop });

            split.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(imageView);
            split.Panel2.Controls.Add(textView);
            split.Panel2.Controls.Add(soundPanel);
            ShowPreview(textView);

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            bar.Items.Add(status);

            Controls.Add(split);
            Controls.Add(tools);
            Controls.Add(menu);
            Controls.Add(bar);
            MainMenuStrip = menu;

            Load += delegate
            {
                split.SplitterDistance = 470;
                if (!string.IsNullOrEmpty(path)) OpenArchive(path);
            };
            FormClosed += delegate { if (archive != null) archive.Dispose(); if (player != null) player.Dispose(); };
        }

        void PromptOpen()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "BSA archives (*.bsa)|*.bsa|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenArchive(dlg.FileName);
            }
        }

        void OpenArchive(string path)
        {
            BsaArchive a;
            try
            {
                a = new BsaArchive(path);
            }
            catch (Exception ex)
            {
                FileSource.ShowError(this, "Could not open " + path + ".", ex);
                return;
            }
            if (archive != null) archive.Dispose();
            archive = a;
            Text = "Battlespire BSA Browser - " + Path.GetFileName(path);
            sortColumn = -1;
            Fill();
            int compressed = 0;
            foreach (BsaEntry e in archive.Entries) if (e.Compressed) compressed++;
            status.Text = string.Format("{0} entries ({1} compressed), {2} archive, {3:N0} bytes{4}",
                archive.Entries.Count, compressed, archive.Kind, archive.FileLength,
                archive.Warning == null ? "" : ". Warning: " + archive.Warning);
        }

        void Fill()
        {
            // Empty the virtual list first: clearing a selection makes the ListView
            // ask for items using the old count, which would index past a shorter list.
            list.SelectedIndices.Clear();
            list.VirtualListSize = 0;
            shown = new List<BsaEntry>();
            if (archive != null)
            {
                string f = filter.Text.Trim();
                foreach (BsaEntry e in archive.Entries)
                    if (f.Length == 0 || e.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                        shown.Add(e);
            }
            ApplySort();
            list.SelectedIndices.Clear();
            list.VirtualListSize = shown.Count;
            list.Invalidate();
        }

        void SortBy(int column)
        {
            if (sortColumn == column) sortDescending = !sortDescending;
            else { sortColumn = column; sortDescending = false; }
            ApplySort();
            list.SelectedIndices.Clear();
            list.Invalidate();
        }

        void ApplySort()
        {
            if (sortColumn < 0) return;
            Comparison<BsaEntry> cmp;
            switch (sortColumn)
            {
                case 1: cmp = delegate(BsaEntry a, BsaEntry b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); }; break;
                case 2: cmp = delegate(BsaEntry a, BsaEntry b) { return a.Size.CompareTo(b.Size); }; break;
                case 3: cmp = delegate(BsaEntry a, BsaEntry b) { return a.Compressed.CompareTo(b.Compressed); }; break;
                default: cmp = delegate(BsaEntry a, BsaEntry b) { return a.Index.CompareTo(b.Index); }; break;
            }
            shown.Sort(cmp);
            if (sortDescending) shown.Reverse();
        }

        void RetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= shown.Count) { e.Item = new ListViewItem(""); return; }
            BsaEntry b = shown[e.ItemIndex];
            ListViewItem item = new ListViewItem(b.Index.ToString());
            item.SubItems.Add(b.Name);
            item.SubItems.Add(b.Size.ToString("N0"));
            item.SubItems.Add(b.Compressed ? "LZSS" : "");
            item.SubItems.Add("0x" + b.Offset.ToString("X"));
            e.Item = item;
        }

        List<BsaEntry> SelectedEntries()
        {
            List<BsaEntry> r = new List<BsaEntry>();
            foreach (int i in list.SelectedIndices) r.Add(shown[i]);
            return r;
        }

        void ShowPreview(Control c)
        {
            imageView.Visible = c == imageView;
            textView.Visible = c == textView;
            soundPanel.Visible = c == soundPanel;
        }

        void Preview()
        {
            openInViewer.Enabled = false;
            previewData = null;
            previewEntry = null;
            if (list.SelectedIndices.Count != 1 || archive == null) return;
            BsaEntry e = shown[list.SelectedIndices[0]];
            byte[] data;
            try
            {
                data = archive.Read(e);
            }
            catch (Exception ex)
            {
                textView.Text = "Could not read entry: " + ex.Message;
                ShowPreview(textView);
                return;
            }
            previewData = data;
            previewEntry = e;
            FileKind kind = FileSniffer.Detect(e.Name, data);
            string head = string.Format("{0}: {1:N0} bytes stored, {2:N0} bytes {3}\r\n", e.Name, e.Size, data.Length,
                e.Compressed ? "after LZSS decompression" : "uncompressed");

            try
            {
                switch (kind)
                {
                    case FileKind.Bsi:
                        {
                            BsiFile bsi = BsiFile.Parse(data);
                            if (bsi.Images.Count > 0)
                            {
                                BsiImage img = bsi.Images[0];
                                imageView.Image = img.RenderFrame(0, BsiPalette.Hicl, 31, true);
                                imageView.Zoom = PixelView.DefaultZoomFor(img.Width, img.Height);
                                ShowPreview(imageView);
                                status.Text = string.Format("{0}: {1}x{2}, {3} frame(s), compression {4}, {5} image(s)",
                                    e.Name, img.Width, img.Height, img.FrameCount, img.Compression, bsi.Images.Count);
                                openInViewer.Enabled = true;
                                return;
                            }
                            textView.Text = head + (bsi.Warning ?? "") + "\r\n\r\n" + HexDump.Format(data, 4096);
                            break;
                        }
                    case FileKind.Model3D:
                        {
                            Model3D m = Model3D.Parse(data);
                            StringBuilder sb = new StringBuilder(head);
                            sb.AppendFormat("Model {0}: {1} points, {2} planes, {3} frame(s), radius {4}\r\n",
                                m.Version, m.PointCount, m.PlaneCount, m.Frames.Count, m.Radius);
                            foreach (string w in m.Warnings) sb.AppendLine("Warning: " + w);
                            sb.AppendLine().Append(HexDump.Format(data, 2048));
                            textView.Text = sb.ToString();
                            openInViewer.Enabled = true;
                            break;
                        }
                    case FileKind.Bs6:
                        {
                            Bs6File lvl = Bs6File.Parse(data);
                            textView.Text = head + string.Format("Level: {0} objects using {1} model names, {2} lights, {3} flats\r\n\r\n",
                                lvl.Objects.Count, lvl.ModelNames.Count, lvl.Lights.Count, lvl.Flats.Count) + HexDump.Format(data, 2048);
                            openInViewer.Enabled = true;
                            break;
                        }
                    case FileKind.Wave:
                        soundLabel.Text = head + "RIFF WAVE sound";
                        ShowPreview(soundPanel);
                        return;
                    case FileKind.Text:
                        textView.Text = TextUtil.Latin1.GetString(data).Replace("\r\n", "\n").Replace("\n", "\r\n");
                        break;
                    default:
                        textView.Text = head + kind + "\r\n\r\n" + HexDump.Format(data, 8192);
                        break;
                }
            }
            catch (Exception ex)
            {
                textView.Text = head + "Could not decode as " + kind + ": " + ex.Message + "\r\n\r\n" + HexDump.Format(data, 8192);
            }
            ShowPreview(textView);
            status.Text = head.Trim();
        }

        void PlaySound()
        {
            if (previewData == null) return;
            if (player != null) player.Dispose();
            player = new SoundPlayer(new MemoryStream(previewData));
            try { player.Play(); }
            catch (Exception ex) { FileSource.ShowError(this, "Could not play the sound.", ex); }
        }

        void OpenInViewer()
        {
            if (previewData == null || previewEntry == null) return;
            string exe;
            switch (FileSniffer.Detect(previewEntry.Name, previewData))
            {
                case FileKind.Bsi: exe = "BsiTool.exe"; break;
                case FileKind.Model3D: exe = "ModelTool.exe"; break;
                case FileKind.Bs6: exe = "Bs6Tool.exe"; break;
                default: return;
            }
            string name = archive.SuggestFileName(previewEntry, previewData);
            if (!FileSource.OpenInTool(exe, name, previewData))
                MessageBox.Show(this, exe + " was not found next to this program.", Text);
        }

        void ExtractSelected()
        {
            List<BsaEntry> sel = SelectedEntries();
            if (sel.Count == 0) return;
            if (sel.Count == 1)
            {
                byte[] data = archive.Read(sel[0]);
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.FileName = archive.SuggestFileName(sel[0], data);
                    if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllBytes(dlg.FileName, data);
                }
                return;
            }
            Extract(sel);
        }

        void ExtractAll()
        {
            if (archive != null) Extract(archive.Entries);
        }

        void Extract(IList<BsaEntry> entries)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Extract " + entries.Count + " entries to:";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                int done = 0, failed = 0;
                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (BsaEntry e in entries)
                    {
                        try
                        {
                            byte[] data = archive.Read(e);
                            string name = archive.SuggestFileName(e, data);
                            // Some archives contain the same name twice; keep both copies.
                            string unique = name;
                            for (int n = 2; used.Contains(unique); n++)
                                unique = Path.GetFileNameWithoutExtension(name) + "_" + n + Path.GetExtension(name);
                            used.Add(unique);
                            File.WriteAllBytes(Path.Combine(dlg.SelectedPath, unique), data);
                            done++;
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                        if (done % 100 == 0)
                        {
                            status.Text = "Extracted " + done + " of " + entries.Count + "...";
                            Application.DoEvents();
                        }
                    }
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
                status.Text = string.Format("Extracted {0} entries to {1}{2}", done, dlg.SelectedPath,
                    failed > 0 ? string.Format(" ({0} failed)", failed) : "");
            }
        }

        void SaveListing()
        {
            if (archive == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Tab separated (*.tsv)|*.tsv|Text (*.txt)|*.txt";
                dlg.FileName = Path.GetFileNameWithoutExtension(archive.Path) + ".tsv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder("Index\tOffset\tLength\tCompressed\tName\r\n");
                foreach (BsaEntry e in archive.Entries)
                    sb.AppendFormat("{0}\t0x{1:X}\t{2}\t{3}\t{4}\r\n", e.Index, e.Offset, e.Size, e.Compressed ? 1 : 0, e.Name);
                File.WriteAllText(dlg.FileName, sb.ToString());
            }
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
