using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Battlespire.Formats;
using Battlespire.Formats.Ui;

namespace Bs6Tool
{
    /// <summary>Top-down plot of a level's objects, flats and lights. Drag to pan, wheel to zoom.</summary>
    public class MapView : Control
    {
        Bs6File level;
        float scale = 0.05f;
        PointF pan;
        Point lastMouse;
        bool dragging, moved;

        public bool ShowObjects = true, ShowFlats = true, ShowLights = true;
        public object Selected;
        public event EventHandler SelectionChanged;

        public MapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 30);
        }

        public void SetLevel(Bs6File l)
        {
            level = l;
            Selected = null;
            Fit();
        }

        public void Fit()
        {
            if (level == null) return;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (LevelObject o in level.Objects) { minX = Math.Min(minX, o.X); maxX = Math.Max(maxX, o.X); minZ = Math.Min(minZ, o.Z); maxZ = Math.Max(maxZ, o.Z); }
            foreach (LevelFlat f in level.Flats) { minX = Math.Min(minX, f.X); maxX = Math.Max(maxX, f.X); minZ = Math.Min(minZ, f.Z); maxZ = Math.Max(maxZ, f.Z); }
            if (minX > maxX) { minX = -1000; maxX = 1000; minZ = -1000; maxZ = 1000; }
            float w = Math.Max(1, maxX - minX), h = Math.Max(1, maxZ - minZ);
            scale = Math.Min((Width - 40) / w, (Height - 40) / h);
            if (scale <= 0 || float.IsInfinity(scale)) scale = 0.05f;
            pan = new PointF(-(minX + maxX) / 2f, -(minZ + maxZ) / 2f);
            Invalidate();
        }

        PointF ToScreen(int x, int z)
        {
            // Z increases away from the viewer; draw it upwards.
            return new PointF(Width / 2f + (x + pan.X) * scale, Height / 2f - (z + pan.Y) * scale);
        }

        protected override void OnMouseDown(MouseEventArgs e) { dragging = true; moved = false; lastMouse = e.Location; Focus(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            if (Math.Abs(e.X - lastMouse.X) + Math.Abs(e.Y - lastMouse.Y) > 2) moved = true;
            pan.X += (e.X - lastMouse.X) / scale;
            pan.Y -= (e.Y - lastMouse.Y) / scale;
            lastMouse = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            if (!moved) Pick(e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Zoom 10% per wheel notch, keeping the map point under the cursor fixed.
            float f = e.Delta > 0 ? 1.1f : 1 / 1.1f;
            float cx = e.X - Width / 2f, cy = e.Y - Height / 2f;
            pan.X += cx / (scale * f) - cx / scale;
            pan.Y -= cy / (scale * f) - cy / scale;
            scale *= f;
            Invalidate();
        }

        void Pick(Point p)
        {
            if (level == null) return;
            object best = null;
            float bestD = 100;
            Action<object, int, int> test = delegate(object o, int x, int z)
            {
                PointF s = ToScreen(x, z);
                float d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
                if (d < bestD) { bestD = d; best = o; }
            };
            if (ShowObjects) foreach (LevelObject o in level.Objects) test(o, o.X, o.Z);
            if (ShowFlats) foreach (LevelFlat f in level.Flats) test(f, f.X, f.Z);
            if (ShowLights) foreach (LevelLight l in level.Lights) test(l, l.X, l.Z);
            Selected = best;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (level == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Pen axis = new Pen(Color.FromArgb(60, 255, 255, 255)))
            {
                PointF o = ToScreen(0, 0);
                g.DrawLine(axis, 0, o.Y, Width, o.Y);
                g.DrawLine(axis, o.X, 0, o.X, Height);
            }

            if (ShowLights)
                using (Pen lp = new Pen(Color.FromArgb(70, 255, 220, 80)))
                using (Brush lb = new SolidBrush(Color.FromArgb(255, 220, 80)))
                    foreach (LevelLight l in level.Lights)
                    {
                        PointF s = ToScreen(l.X, l.Z);
                        float r = l.Radius * scale;
                        if (r > 2 && r < 4000) g.DrawEllipse(lp, s.X - r, s.Y - r, r * 2, r * 2);
                        g.FillEllipse(lb, s.X - 2.5f, s.Y - 2.5f, 5, 5);
                    }

            if (ShowFlats)
                using (Brush fb = new SolidBrush(Color.FromArgb(110, 220, 120)))
                    foreach (LevelFlat f in level.Flats)
                    {
                        PointF s = ToScreen(f.X, f.Z);
                        g.FillRectangle(fb, s.X - 2, s.Y - 2, 4, 4);
                    }

            if (ShowObjects)
                using (Pen op = new Pen(Color.FromArgb(110, 170, 255)))
                    foreach (LevelObject o in level.Objects)
                    {
                        PointF s = ToScreen(o.X, o.Z);
                        g.DrawRectangle(op, s.X - 3, s.Y - 3, 6, 6);
                    }

            if (Selected != null)
            {
                int x = 0, z = 0;
                LevelObject so = Selected as LevelObject;
                LevelFlat sf = Selected as LevelFlat;
                LevelLight sl = Selected as LevelLight;
                if (so != null) { x = so.X; z = so.Z; }
                if (sf != null) { x = sf.X; z = sf.Z; }
                if (sl != null) { x = sl.X; z = sl.Z; }
                PointF s = ToScreen(x, z);
                using (Pen hp = new Pen(Color.White, 2)) g.DrawEllipse(hp, s.X - 8, s.Y - 8, 16, 16);
            }

            using (Brush tb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                g.DrawString("blue = objects   green = flats   yellow = lights   (X across, Z up; drag to pan, wheel to zoom, click to select)",
                    Font, tb, 6, Height - 20);
        }
    }

    /// <summary>
    /// BS6 level viewer (replaces bs6tool.py): chunk tree with decoded values, the
    /// level's objects with their model names resolved through LFIL, a top-down map,
    /// and XML export.
    /// </summary>
    public class MainForm : Form
    {
        Bs6File level;
        LoadedFile source;
        readonly TreeView tree = new TreeView();
        readonly TextBox details = new TextBox();
        readonly ListView objects = new ListView();
        readonly MapView map = new MapView();
        readonly TabControl tabs = new TabControl();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly Dictionary<Bs6Chunk, TreeNode> nodes = new Dictionary<Bs6Chunk, TreeNode>();

        public MainForm(string path)
        {
            Text = "Battlespire Level Viewer";
            Size = new Size(1100, 720);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) OpenPath(f[0]); };

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open (BS6 or BSA)...", null, delegate { PromptOpen(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("Export &XML...", null, delegate { ExportXml(); }, Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripMenuItem("Export object &list...", null, delegate { ExportObjects(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, delegate { Close(); }));
            ToolStripMenuItem viewMenu = new ToolStripMenuItem("&Map");
            ToolStripMenuItem so = new ToolStripMenuItem("Show &objects") { Checked = true, CheckOnClick = true };
            so.CheckedChanged += delegate { map.ShowObjects = so.Checked; map.Invalidate(); };
            ToolStripMenuItem sf = new ToolStripMenuItem("Show &flats") { Checked = true, CheckOnClick = true };
            sf.CheckedChanged += delegate { map.ShowFlats = sf.Checked; map.Invalidate(); };
            ToolStripMenuItem sl = new ToolStripMenuItem("Show &lights") { Checked = true, CheckOnClick = true };
            sl.CheckedChanged += delegate { map.ShowLights = sl.Checked; map.Invalidate(); };
            viewMenu.DropDownItems.AddRange(new ToolStripItem[] { so, sf, sl, new ToolStripMenuItem("&Fit to window", null, delegate { map.Fit(); }) });
            menu.Items.Add(file);
            menu.Items.Add(viewMenu);

            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.AfterSelect += delegate { ShowChunk(tree.SelectedNode == null ? null : tree.SelectedNode.Tag as Bs6Chunk); };

            details.Dock = DockStyle.Fill;
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Both;
            details.WordWrap = false;
            details.Font = new Font(FontFamily.GenericMonospace, 9f);
            details.BackColor = SystemColors.Window;

            SplitContainer chunkSplit = new SplitContainer();
            chunkSplit.Dock = DockStyle.Fill;
            chunkSplit.Panel1.Controls.Add(tree);
            chunkSplit.Panel2.Controls.Add(details);

            objects.Dock = DockStyle.Fill;
            objects.View = View.Details;
            objects.FullRowSelect = true;
            objects.HideSelection = false;
            objects.Columns.Add("Kind", 55);
            objects.Columns.Add("Id", 55, HorizontalAlignment.Right);
            objects.Columns.Add("Model / image", 140);
            objects.Columns.Add("X", 70, HorizontalAlignment.Right);
            objects.Columns.Add("Y", 70, HorizontalAlignment.Right);
            objects.Columns.Add("Z", 70, HorizontalAlignment.Right);
            objects.Columns.Add("Angles / radius / scale", 150);
            objects.Columns.Add("Select", 60, HorizontalAlignment.Right);
            objects.DoubleClick += delegate
            {
                if (objects.SelectedItems.Count != 1) return;
                Bs6Chunk c = ChunkOf(objects.SelectedItems[0].Tag);
                if (c != null && nodes.ContainsKey(c)) { tabs.SelectedIndex = 0; tree.SelectedNode = nodes[c]; }
            };

            map.Dock = DockStyle.Fill;
            map.SelectionChanged += delegate
            {
                Bs6Chunk c = ChunkOf(map.Selected);
                if (c != null) ShowChunk(c);
                status.Text = Summary(map.Selected);
            };

            TabPage chunkPage = new TabPage("Chunks");
            chunkPage.Controls.Add(chunkSplit);
            TabPage objPage = new TabPage("Objects");
            objPage.Controls.Add(objects);
            TabPage mapPage = new TabPage("Map");
            mapPage.Controls.Add(map);
            tabs.TabPages.AddRange(new TabPage[] { chunkPage, objPage, mapPage });
            tabs.Dock = DockStyle.Fill;
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab == mapPage) map.Fit(); };

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            bar.Items.Add(status);

            Controls.Add(tabs);
            Controls.Add(menu);
            Controls.Add(bar);
            MainMenuStrip = menu;

            Load += delegate
            {
                chunkSplit.SplitterDistance = 360;
                if (!string.IsNullOrEmpty(path)) OpenPath(path);
            };
        }

        static Bs6Chunk ChunkOf(object o)
        {
            if (o is LevelObject) return ((LevelObject)o).Chunk;
            if (o is LevelFlat) return ((LevelFlat)o).Chunk;
            if (o is LevelLight) return ((LevelLight)o).Chunk;
            return null;
        }

        static string Summary(object o)
        {
            LevelObject so = o as LevelObject;
            if (so != null) return string.Format("Object {0}: {1} at {2}, {3}, {4}; angles {5}, {6}, {7}", so.Id, so.ModelName, so.X, so.Y, so.Z, so.AngleX, so.AngleY, so.AngleZ);
            LevelFlat sf = o as LevelFlat;
            if (sf != null) return string.Format("Flat {0}: {1} at {2}, {3}, {4}; scale {5}", sf.Id, sf.Image, sf.X, sf.Y, sf.Z, sf.Scale);
            LevelLight sl = o as LevelLight;
            if (sl != null) return string.Format("Light {0}: type {1} at {2}, {3}, {4}; radius {5}, brightness {6}", sl.Id, sl.Type, sl.X, sl.Y, sl.Z, sl.Radius, sl.Brightness);
            return "";
        }

        void PromptOpen()
        {
            try
            {
                LoadedFile f = FileSource.Prompt(this, "Open level", "BS6 levels (*.bs6)|*.bs6", new string[] { ".BS6" });
                if (f != null) Open(f);
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open the level.", ex); }
        }

        void OpenPath(string path)
        {
            try
            {
                if (string.Equals(Path.GetExtension(path), ".bsa", StringComparison.OrdinalIgnoreCase))
                {
                    LoadedFile f = FileSource.PickFromArchive(this, path, new string[] { ".BS6" });
                    if (f != null) Open(f);
                }
                else Open(FileSource.Load(path));
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open " + path + ".", ex); }
        }

        void Open(LoadedFile f)
        {
            Bs6File l = Bs6File.Parse(f.Data);
            if (l.Root == null) throw new InvalidDataException("No chunks found.");
            level = l;
            source = f;
            Text = "Battlespire Level Viewer - " + f.Name;

            nodes.Clear();
            tree.BeginUpdate();
            tree.Nodes.Clear();
            foreach (Bs6Chunk c in l.Roots) tree.Nodes.Add(MakeNode(c));
            if (tree.Nodes.Count > 0) tree.Nodes[0].Expand();
            tree.EndUpdate();

            objects.BeginUpdate();
            objects.Items.Clear();
            foreach (LevelObject o in l.Objects)
                objects.Items.Add(Row("Object", o.Id, o.ModelName, o.X, o.Y, o.Z, string.Format("{0}, {1}, {2}", o.AngleX, o.AngleY, o.AngleZ), o.Select.ToString(), o));
            foreach (LevelFlat fl in l.Flats)
                objects.Items.Add(Row("Flat", fl.Id, fl.Image, fl.X, fl.Y, fl.Z, "scale " + fl.Scale, "", fl));
            foreach (LevelLight li in l.Lights)
                objects.Items.Add(Row("Light", li.Id, "type " + li.Type, li.X, li.Y, li.Z, string.Format("radius {0}, brightness {1}", li.Radius, li.Brightness), "", li));
            objects.EndUpdate();

            map.SetLevel(l);
            int missing = 0;
            foreach (LevelObject o in l.Objects) if (o.ModelName == "?") missing++;
            status.Text = string.Format("{0}: {1} objects ({2} model names{3}), {4} flats, {5} lights{6}",
                f.Description, l.Objects.Count, l.ModelNames.Count,
                missing > 0 ? ", " + missing + " objects point past the list" : "",
                l.Flats.Count, l.Lights.Count, l.Warnings.Count > 0 ? ". " + string.Join(" ", l.Warnings.ToArray()) : "");
        }

        static ListViewItem Row(string kind, int id, string name, int x, int y, int z, string extra, string sel, object tag)
        {
            ListViewItem i = new ListViewItem(kind);
            i.SubItems.Add(id.ToString());
            i.SubItems.Add(name ?? "");
            i.SubItems.Add(x.ToString());
            i.SubItems.Add(y.ToString());
            i.SubItems.Add(z.ToString());
            i.SubItems.Add(extra);
            i.SubItems.Add(sel);
            i.Tag = tag;
            return i;
        }

        TreeNode MakeNode(Bs6Chunk c)
        {
            string label = c.IsGroup ? c.Tag + "  (" + c.Children.Count + ")" : c.Tag + "  " + Truncate(c.Describe(), 60);
            if (c.Tag == "OBJD") label += "  " + ObjectLabel(c);
            TreeNode n = new TreeNode(label);
            n.Tag = c;
            nodes[c] = n;
            foreach (Bs6Chunk k in c.Children) n.Nodes.Add(MakeNode(k));
            return n;
        }

        string ObjectLabel(Bs6Chunk c)
        {
            foreach (LevelObject o in level.Objects)
                if (o.Chunk == c) return o.ModelName;
            return "";
        }

        static string Truncate(string s, int n) { return s.Length > n ? s.Substring(0, n) + "..." : s; }

        void ShowChunk(Bs6Chunk c)
        {
            if (c == null) { details.Text = ""; return; }
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0} at file offset 0x{1:X}, {2} bytes\r\n", c.Tag, c.Offset, c.Length);
            if (c.Parent != null) sb.AppendFormat("Inside {0} at 0x{1:X}\r\n", c.Parent.Tag, c.Parent.Offset);
            sb.AppendLine();
            if (c.Tag == "LFIL")
            {
                for (int i = 0, n = 0; i + Bs6File.LfilRecordSize <= c.Data.Length; i += Bs6File.LfilRecordSize, n++)
                    sb.AppendFormat("{0,4}  {1}\r\n", n, Bs6File.GameModelName(TextUtil.ReadCString(c.Data, i, 13)));
            }
            else if (c.IsGroup)
            {
                foreach (Bs6Chunk k in c.Children) sb.AppendFormat("{0}  {1}\r\n", k.Tag, k.Describe());
                if (c.Tag == "OBJD")
                    foreach (LevelObject o in level.Objects)
                        if (o.Chunk == c) sb.AppendFormat("\r\nModel: {0} (LFIL entry {1})\r\n", o.ModelName, o.ModelIndex);
            }
            else
            {
                sb.AppendLine(c.Describe());
                if (c.Data.Length > 4 && c.Data.Length % 4 == 0 && c.Tag != "FILN" && c.Tag != "DIRN")
                {
                    sb.AppendLine().AppendLine("As 32 bit integers:");
                    for (int i = 0; i < c.Data.Length / 4; i++) sb.AppendFormat("  [{0}] {1}\r\n", i, c.Int(i));
                }
                sb.AppendLine().Append(HexDump.Format(c.Data, 4096));
            }
            details.Text = sb.ToString();
        }

        void ExportXml()
        {
            if (level == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "XML (*.xml)|*.xml";
                dlg.FileName = Path.GetFileNameWithoutExtension(source.Name) + ".xml";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                using (StreamWriter w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(false)))
                    level.WriteXml(w);
                status.Text = "Saved " + dlg.FileName;
            }
        }

        void ExportObjects()
        {
            if (level == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Tab separated (*.tsv)|*.tsv";
                dlg.FileName = Path.GetFileNameWithoutExtension(source.Name) + "_objects.tsv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder("Kind\tId\tName\tX\tY\tZ\tA\tB\tC\r\n");
                foreach (LevelObject o in level.Objects)
                    sb.AppendFormat("Object\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n", o.Id, o.ModelName, o.X, o.Y, o.Z, o.AngleX, o.AngleY, o.AngleZ);
                foreach (LevelFlat f in level.Flats)
                    sb.AppendFormat("Flat\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t\t\r\n", f.Id, f.Image, f.X, f.Y, f.Z, f.Scale);
                foreach (LevelLight l in level.Lights)
                    sb.AppendFormat("Light\t{0}\ttype {1}\t{2}\t{3}\t{4}\t{5}\t{6}\t\r\n", l.Id, l.Type, l.X, l.Y, l.Z, l.Radius, l.Brightness);
                File.WriteAllText(dlg.FileName, sb.ToString());
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
