using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Battlespire.Formats;
using Battlespire.Formats.Ui;

namespace ModelTool
{
    /// <summary>Software renderer for a model: flat shaded polygons sorted back to front.</summary>
    public class ModelView : Control
    {
        Model3D model;
        int frame;
        float yaw = 0.6f, pitch = -0.4f, distance = 3f;
        Point lastMouse;
        bool dragging;

        public bool Wireframe;
        public bool Solid = true;
        public int HighlightFace = -1;

        public ModelView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(40, 44, 52);
        }

        public void SetModel(Model3D m, int f)
        {
            model = m;
            frame = f;
            Invalidate();
        }

        public void ResetView()
        {
            yaw = 0.6f; pitch = -0.4f; distance = 3f;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            dragging = true;
            lastMouse = e.Location;
            Focus();
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            yaw += (e.X - lastMouse.X) * 0.01f;
            pitch += (e.Y - lastMouse.Y) * 0.01f;
            pitch = Math.Max(-1.55f, Math.Min(1.55f, pitch));
            lastMouse = e.Location;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 10% per wheel notch.
            distance *= e.Delta > 0 ? 1 / 1.1f : 1.1f;
            distance = Math.Max(1.1f, Math.Min(40f, distance));
            Invalidate();
        }

        struct DrawFace
        {
            public PointF[] Points;
            public float Depth;
            public float Light;
            public int Index;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (model == null || model.Frames.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Vec3 min, max;
            model.GetBounds(frame, out min, out max);
            Vec3 centre = (min + max) * 0.5f;
            float radius = Math.Max(1e-3f, (max - min).Length * 0.5f);

            float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw);
            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
            float scale = Math.Min(Width, Height) * 0.9f;
            float camDist = distance * radius;
            Vec3 lightDir = new Vec3(0.3f, -0.5f, -0.8f);
            lightDir = lightDir * (1f / lightDir.Length);

            Vec3[] pts = model.Frames[Math.Min(frame, model.Frames.Count - 1)].Points;
            Vec3[] view = new Vec3[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                // Battlespire's Y axis points down; flip it so models stand upright.
                Vec3 p = pts[i] - centre;
                p = new Vec3(p.X, -p.Y, p.Z);
                float x = p.X * cy - p.Z * sy;
                float z = p.X * sy + p.Z * cy;
                float y = p.Y * cp - z * sp;
                z = p.Y * sp + z * cp;
                view[i] = new Vec3(x, y, z + camDist);
            }

            List<DrawFace> faces = new List<DrawFace>();
            for (int i = 0; i < model.Faces.Count; i++)
            {
                Face f = model.Faces[i];
                if (f.Vertices.Length < 2) continue;
                PointF[] sp2 = new PointF[f.Vertices.Length];
                float depth = 0;
                bool ok = true;
                for (int k = 0; k < f.Vertices.Length; k++)
                {
                    int idx = f.Vertices[k].Point;
                    if (idx < 0 || idx >= view.Length || view[idx].Z <= 1e-3f) { ok = false; break; }
                    Vec3 v = view[idx];
                    sp2[k] = new PointF(Width / 2f + v.X / v.Z * scale, Height / 2f - v.Y / v.Z * scale);
                    depth += v.Z;
                }
                if (!ok) continue;
                DrawFace d = new DrawFace();
                d.Points = sp2;
                d.Depth = depth / f.Vertices.Length;
                d.Index = i;
                d.Light = 0.6f;
                if (f.Vertices.Length >= 3)
                {
                    Vec3 a = view[f.Vertices[0].Point], b = view[f.Vertices[1].Point], c = view[f.Vertices[2].Point];
                    Vec3 n = Vec3.Cross(b - a, c - a);
                    float len = n.Length;
                    if (len > 0) d.Light = 0.25f + 0.75f * Math.Abs(Vec3.Dot(n * (1f / len), lightDir));
                }
                faces.Add(d);
            }
            faces.Sort(delegate(DrawFace a, DrawFace b) { return b.Depth.CompareTo(a.Depth); });

            using (Pen edge = new Pen(Color.FromArgb(Solid ? 90 : 220, 20, 20, 20)))
            using (Pen wire = new Pen(Color.FromArgb(120, 200, 255)))
            using (Pen hi = new Pen(Color.Yellow, 2f))
            {
                foreach (DrawFace d in faces)
                {
                    if (Solid && d.Points.Length >= 3)
                    {
                        int c = (int)(d.Light * 220);
                        Color col = TextureColour(model.Faces[d.Index].Texture, c);
                        using (SolidBrush br = new SolidBrush(col)) g.FillPolygon(br, d.Points);
                    }
                    if (Wireframe || !Solid) g.DrawPolygon(Solid ? edge : wire, d.Points);
                    if (d.Index == HighlightFace) g.DrawPolygon(hi, d.Points);
                }
            }
        }

        // Tints faces by texture word so different materials are distinguishable.
        static Color TextureColour(uint tex, int brightness)
        {
            uint h = tex * 2654435761u;
            float r = 0.75f + 0.25f * ((h >> 24) & 0xFF) / 255f;
            float g = 0.75f + 0.25f * ((h >> 16) & 0xFF) / 255f;
            float b = 0.75f + 0.25f * ((h >> 8) & 0xFF) / 255f;
            return Color.FromArgb(Clamp(brightness * r), Clamp(brightness * g), Clamp(brightness * b));
        }

        static int Clamp(float v) { return Math.Max(0, Math.Min(255, (int)v)); }
    }

    /// <summary>
    /// .3D model viewer and exporter (replaces 3dtool.py): reads loose files or entries
    /// from 3D.BSA, shows the model, and exports OBJ (with UVs and normals) or PLY.
    /// </summary>
    public class MainForm : Form
    {
        Model3D model;
        LoadedFile source;
        readonly ModelView view = new ModelView();
        readonly ListView faces = new ListView();
        readonly TextBox info = new TextBox();
        readonly NumericUpDown frameBox = new NumericUpDown();
        readonly NumericUpDown textureSize = new NumericUpDown();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();

        public MainForm(string path)
        {
            Text = "Battlespire Model Viewer";
            Size = new Size(1100, 720);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) OpenPath(f[0]); };

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open (3D or BSA)...", null, delegate { PromptOpen(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("Export &OBJ...", null, delegate { Export(true); }, Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripMenuItem("Export &PLY...", null, delegate { Export(false); }));
            file.DropDownItems.Add(new ToolStripMenuItem("Export a whole &BSA to OBJ...", null, delegate { ExportArchive(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, delegate { Close(); }));
            ToolStripMenuItem viewMenu = new ToolStripMenuItem("&View");
            ToolStripMenuItem solid = new ToolStripMenuItem("&Solid") { Checked = true, CheckOnClick = true };
            solid.CheckedChanged += delegate { view.Solid = solid.Checked; view.Invalidate(); };
            ToolStripMenuItem wire = new ToolStripMenuItem("&Edges") { CheckOnClick = true };
            wire.CheckedChanged += delegate { view.Wireframe = wire.Checked; view.Invalidate(); };
            viewMenu.DropDownItems.Add(solid);
            viewMenu.DropDownItems.Add(wire);
            viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Reset view", null, delegate { view.ResetView(); }, Keys.Control | Keys.R));
            menu.Items.Add(file);
            menu.Items.Add(viewMenu);

            Panel side = new Panel();
            side.Dock = DockStyle.Right;
            side.Width = 360;

            FlowLayoutPanel opts = new FlowLayoutPanel();
            opts.Dock = DockStyle.Top;
            opts.Height = 36;
            opts.Padding = new Padding(4);
            frameBox.Minimum = 0;
            frameBox.Width = 50;
            frameBox.ValueChanged += delegate { if (model != null) view.SetModel(model, (int)frameBox.Value); };
            textureSize.Minimum = 1;
            textureSize.Maximum = 4096;
            textureSize.Value = 64;
            textureSize.Width = 60;
            opts.Controls.Add(Label("Frame"));
            opts.Controls.Add(frameBox);
            opts.Controls.Add(Label("Texture size for OBJ UVs"));
            opts.Controls.Add(textureSize);

            info.Dock = DockStyle.Top;
            info.Multiline = true;
            info.ReadOnly = true;
            info.Height = 200;
            info.ScrollBars = ScrollBars.Vertical;
            info.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            info.BackColor = SystemColors.Window;

            faces.Dock = DockStyle.Fill;
            faces.View = View.Details;
            faces.FullRowSelect = true;
            faces.HideSelection = false;
            faces.Columns.Add("Face", 45, HorizontalAlignment.Right);
            faces.Columns.Add("Points", 50, HorizontalAlignment.Right);
            faces.Columns.Add("Texture", 80);
            faces.Columns.Add("Vertices (point u,v)", 400);
            faces.SelectedIndexChanged += delegate
            {
                view.HighlightFace = faces.SelectedIndices.Count == 1 ? faces.SelectedIndices[0] : -1;
                view.Invalidate();
            };

            side.Controls.Add(faces);
            side.Controls.Add(info);
            side.Controls.Add(opts);

            view.Dock = DockStyle.Fill;

            StatusStrip bar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "Drag to rotate, wheel to zoom.";
            bar.Items.Add(status);

            Controls.Add(view);
            Controls.Add(side);
            Controls.Add(menu);
            Controls.Add(bar);
            MainMenuStrip = menu;

            Load += delegate { if (!string.IsNullOrEmpty(path)) OpenPath(path); };
        }

        static Label Label(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 6, 3, 0);
            return l;
        }

        void PromptOpen()
        {
            try
            {
                LoadedFile f = FileSource.Prompt(this, "Open model", "3D models (*.3d)|*.3d", new string[] { ".3D" });
                if (f != null) Open(f);
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open the model.", ex); }
        }

        void OpenPath(string path)
        {
            try
            {
                if (string.Equals(Path.GetExtension(path), ".bsa", StringComparison.OrdinalIgnoreCase))
                {
                    LoadedFile f = FileSource.PickFromArchive(this, path, new string[] { ".3D" });
                    if (f != null) Open(f);
                }
                else Open(FileSource.Load(path));
            }
            catch (Exception ex) { FileSource.ShowError(this, "Could not open " + path + ".", ex); }
        }

        void Open(LoadedFile f)
        {
            Model3D m = Model3D.Parse(f.Data);
            model = m;
            source = f;
            Text = "Battlespire Model Viewer - " + f.Name;
            frameBox.Maximum = Math.Max(0, m.Frames.Count - 1);
            frameBox.Value = 0;
            frameBox.Enabled = m.Frames.Count > 1;
            view.HighlightFace = -1;
            view.SetModel(m, 0);

            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0}\r\n", f.Description);
            sb.AppendFormat("Version {0}, {1} points, {2} planes, {3} frame(s)\r\n", m.Version, m.PointCount, m.PlaneCount, m.Frames.Count);
            sb.AppendFormat("Radius {0} ({1:0.##} units)\r\n", m.Radius, m.Radius * Model3D.PointScale);
            Vec3 min, max;
            m.GetBounds(0, out min, out max);
            sb.AppendFormat("Bounds {0}  to  {1}\r\n", min, max);
            sb.AppendFormat("Object records {0}\r\n", m.ObjectRecords.Count);
            sb.AppendLine("Header:");
            for (int i = 0; i < 16; i++)
                sb.AppendFormat("  0x{0:X2} {1,10}  0x{1:X8}\r\n", i * 4, m.Header[i]);
            foreach (string w in m.Warnings) sb.AppendLine("Warning: " + w);
            info.Text = sb.ToString();

            faces.BeginUpdate();
            faces.Items.Clear();
            for (int i = 0; i < m.Faces.Count; i++)
            {
                Face face = m.Faces[i];
                ListViewItem item = new ListViewItem(i.ToString());
                item.SubItems.Add(face.Vertices.Length.ToString());
                item.SubItems.Add(face.Texture.ToString("X8"));
                StringBuilder v = new StringBuilder();
                foreach (FaceVertex fv in face.Vertices)
                    v.AppendFormat("{0} ({1:0.#},{2:0.#})  ", fv.Point, fv.U, fv.V);
                item.SubItems.Add(v.ToString());
                faces.Items.Add(item);
            }
            faces.EndUpdate();
            status.Text = m.Warnings.Count > 0 ? m.Warnings.Count + " warning(s); see details." : "Drag to rotate, wheel to zoom.";
        }

        void Export(bool obj)
        {
            if (model == null) return;
            string name = source == null ? "model" : Path.GetFileNameWithoutExtension(source.Name);
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = obj ? "Wavefront OBJ (*.obj)|*.obj" : "PLY (*.ply)|*.ply";
                dlg.FileName = name + (obj ? ".obj" : ".ply");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, obj ? model.ToObj((int)frameBox.Value, name, (float)textureSize.Value) : model.ToPly((int)frameBox.Value));
                status.Text = "Saved " + dlg.FileName;
            }
        }

        void ExportArchive()
        {
            string bsaPath;
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Filter = "BSA archives (*.bsa)|*.bsa";
                open.Title = "Choose an archive of models (e.g. 3D.BSA)";
                if (open.ShowDialog(this) != DialogResult.OK) return;
                bsaPath = open.FileName;
            }
            string outDir;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Save OBJ files to:";
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
                            if (FileSniffer.Detect(e.Name, data) != FileKind.Model3D) { skipped++; continue; }
                            Model3D m = Model3D.Parse(data);
                            string name = Path.GetFileNameWithoutExtension(BsaArchive.SanitizeFileName(e.Name));
                            File.WriteAllText(Path.Combine(outDir, name + ".obj"), m.ToObj(0, name, (float)textureSize.Value));
                            ok++;
                        }
                        catch (Exception) { skipped++; }
                        if ((ok + skipped) % 100 == 0)
                        {
                            status.Text = string.Format("Exported {0} of {1}...", ok + skipped, bsa.Entries.Count);
                            Application.DoEvents();
                        }
                    }
                }
            }
            catch (Exception ex) { FileSource.ShowError(this, "Export failed.", ex); }
            finally { Cursor = Cursors.Default; }
            status.Text = string.Format("Exported {0} models to {1}; skipped {2} entries that are not models.", ok, outDir, skipped);
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
