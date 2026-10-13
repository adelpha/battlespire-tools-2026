using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Battlespire.Formats;
using Battlespire.Formats.Ui;

namespace RscTool
{
    /// <summary>
    /// TEXT.RSC viewer (replaces misc/rsctool.py, which could not run and paired each
    /// text with the next record's id). Lists every text with the id the game uses,
    /// shows all random variants, and exports a TSV.
    /// </summary>
    public class MainForm : Form
    {
        RscFile rsc;
        string path;
        readonly ListView list = new ListView();
        readonly TextBox search = new TextBox();
        readonly TextBox text = new TextBox();
        readonly CheckBox showCodes = new CheckBox();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        List<RscEntry> shown = new List<RscEntry>();

        public MainForm(string path)
        {
            Text = "Battlespire Text Viewer";
            Size = new Size(1000, 680);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) Open(f[0]); };

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open TEXT.RSC...", null, delegate { PromptOpen(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("&Export TSV...", null, delegate { ExportTsv(); }, Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, delegate { Close(); }));
            menu.Items.Add(file);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Top;
            tools.Height = 32;
            tools.Padding = new Padding(4, 3, 4, 0);
            Label sl = new Label();
            sl.Text = "Search (text or id):";
            sl.AutoSize = true;
            sl.Margin = new Padding(3, 6, 3, 0);
            tools.Controls.Add(sl);
            search.Width = 250;
            search.TextChanged += delegate { Fill(); };
            tools.Controls.Add(search);
            showCodes.Text = "Show control codes";
            showCodes.AutoSize = true;
            showCodes.Margin = new Padding(12, 5, 3, 0);
            showCodes.CheckedChanged += delegate { Fill(); ShowSelected(); };
            tools.Controls.Add(showCodes);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.VirtualMode = true;
            list.Columns.Add("Id", 55, HorizontalAlignment.Right);
            list.Columns.Add("Hex", 55);
            list.Columns.Add("Variants", 60, HorizontalAlignment.Right);
            list.Columns.Add("Text", 600);
            list.RetrieveVirtualItem += delegate(object s, RetrieveVirtualItemEventArgs e)
            {
                if (e.ItemIndex < 0 || e.ItemIndex >= shown.Count) { e.Item = new ListViewItem(""); return; }
                RscEntry r = shown[e.ItemIndex];
                ListViewItem item = new ListViewItem(r.Id.ToString());
                item.SubItems.Add("0x" + r.Id.ToString("X"));
                item.SubItems.Add(r.Variants.Count.ToString());
                string t = RscFile.Decode(r.Variants[0], showCodes.Checked).Replace("\r\n", " / ");
                item.SubItems.Add(t.Length > 200 ? t.Substring(0, 200) + "..." : t);
                e.Item = item;
            };
            list.SelectedIndexChanged += delegate { ShowSelected(); };

            text.Dock = DockStyle.Fill;
            text.Multiline = true;
            text.ReadOnly = true;
            text.ScrollBars = ScrollBars.Vertical;
            text.Font = new Font("Segoe UI", 10f);
            text.BackColor = SystemColors.Window;

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(text);

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
                split.SplitterDistance = 380;
                if (!string.IsNullOrEmpty(path)) Open(path);
            };
        }

        void PromptOpen()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Text resources (*.rsc)|*.rsc|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) Open(dlg.FileName);
            }
        }

        void Open(string file)
        {
            try
            {
                rsc = RscFile.Parse(File.ReadAllBytes(file));
            }
            catch (Exception ex)
            {
                FileSource.ShowError(this, "Could not open " + file + ".", ex);
                return;
            }
            path = file;
            Text = "Battlespire Text Viewer - " + Path.GetFileName(file);
            Fill();
            int variants = 0;
            foreach (RscEntry e in rsc.Entries) variants += e.Variants.Count;
            status.Text = string.Format("{0}: {1} texts, {2} variants", file, rsc.Entries.Count, variants);
        }

        void Fill()
        {
            // Empty the virtual list first: clearing a selection makes the ListView
            // ask for items using the old count, which would index past a shorter list.
            list.SelectedIndices.Clear();
            list.VirtualListSize = 0;
            shown = new List<RscEntry>();
            if (rsc != null)
            {
                string q = search.Text.Trim();
                foreach (RscEntry e in rsc.Entries)
                {
                    if (q.Length > 0 && e.Id.ToString() != q)
                    {
                        bool hit = false;
                        foreach (byte[] v in e.Variants)
                            if (RscFile.Decode(v, false).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                        if (!hit) continue;
                    }
                    shown.Add(e);
                }
            }
            list.SelectedIndices.Clear();
            list.VirtualListSize = shown.Count;
            list.Invalidate();
        }

        void ShowSelected()
        {
            if (list.SelectedIndices.Count != 1) { text.Text = ""; return; }
            RscEntry e = shown[list.SelectedIndices[0]];
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Text {0} (0x{0:X}), {1} bytes at offset 0x{2:X}", e.Id, e.Length, e.Offset);
            if (e.Variants.Count > 1) sb.AppendFormat(", {0} variants (the game picks one at random)", e.Variants.Count);
            sb.Append("\r\n\r\n");
            for (int i = 0; i < e.Variants.Count; i++)
            {
                if (e.Variants.Count > 1) sb.AppendFormat("--- Variant {0} ---\r\n", i + 1);
                sb.Append(RscFile.Decode(e.Variants[i], showCodes.Checked)).Append("\r\n\r\n");
            }
            text.Text = sb.ToString();
        }

        void ExportTsv()
        {
            if (rsc == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Tab separated (*.tsv)|*.tsv";
                dlg.FileName = Path.GetFileNameWithoutExtension(path) + ".tsv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, rsc.ToTsv(showCodes.Checked), Encoding.UTF8);
                status.Text = "Saved " + dlg.FileName;
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
