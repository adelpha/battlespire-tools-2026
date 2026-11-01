using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Battlespire.Formats
{
    public class Bs6Chunk
    {
        public string Tag;
        public int Offset;
        public int Length;
        public byte[] Data;
        public List<Bs6Chunk> Children = new List<Bs6Chunk>();
        public Bs6Chunk Parent;

        public bool IsGroup { get { return Bs6File.IsGroupTag(Tag); } }

        public Bs6Chunk Child(string tag)
        {
            foreach (Bs6Chunk c in Children) if (c.Tag == tag) return c;
            return null;
        }

        public int Int(int index)
        {
            return Data != null && Data.Length >= index * 4 + 4 ? BitConverter.ToInt32(Data, index * 4) : 0;
        }

        public string String
        {
            get { return Data == null ? "" : TextUtil.ReadCString(Data, 0, Data.Length); }
        }

        /// <summary>Human readable summary of a leaf chunk's contents.</summary>
        public string Describe()
        {
            if (IsGroup) return Children.Count + " children";
            if (Data == null || Data.Length == 0) return "(empty)";
            switch (Tag)
            {
                case "FILN":
                case "DIRN":
                    return "\"" + String + "\"";
                case "POSI":
                case "OFST":
                case "CENT":
                case "ANGS":
                    return string.Format("{0}, {1}, {2}", Int(0), Int(1), Int(2));
                case "BBOX":
                    return string.Format("({0}, {1}, {2}) - ({3}, {4}, {5})", Int(0), Int(1), Int(2), Int(3), Int(4), Int(5));
                case "LFIL":
                    return (Data.Length / Bs6File.LfilRecordSize) + " model names";
                default:
                    if (Data.Length == 4) return Int(0).ToString(CultureInfo.InvariantCulture);
                    return Hex(Data, 64);
            }
        }

        public static string Hex(byte[] data, int max)
        {
            StringBuilder sb = new StringBuilder();
            int n = Math.Min(data.Length, max);
            for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2"));
            if (data.Length > max) sb.Append("...");
            return sb.ToString();
        }

        public override string ToString() { return Tag; }
    }

    public class LevelObject
    {
        public int Id;
        public int ModelIndex;
        public string ModelName;
        public int X, Y, Z;
        public int AngleX, AngleY, AngleZ;
        public int Select;
        public Bs6Chunk Chunk;
    }

    public class LevelLight
    {
        public int Id, Type, X, Y, Z, Radius, Brightness;
        public Bs6Chunk Chunk;
    }

    public class LevelFlat
    {
        public int Id, X, Y, Z, Scale, Ambient;
        public string Image;
        public Bs6Chunk Chunk;
    }

    /// <summary>
    /// A .BS6 level: little endian chunk tree (4 byte tag, 4 byte length) rooted at GNRL.
    /// The game's loader (LoadMap 0x00022ecc) reads WATR, FLAS, OBJS and LITS from
    /// inside GNRL and skips the rest. OBJS/LFIL lists model names in 260 byte records;
    /// the game reads 13 characters and adds ".3d" when there is no extension.
    /// </summary>
    public class Bs6File
    {
        public const int LfilRecordSize = 260;

        static readonly HashSet<string> GroupTags = new HashSet<string>(new string[] {
            "GNRL", "TEXI", "STRU", "SNAP", "VIEW", "CTRL", "LINK",
            "OBJS", "OBJD", "LITS", "LITD", "FLAS", "FLAD"
        });

        public List<Bs6Chunk> Roots = new List<Bs6Chunk>();
        public List<string> ModelNames = new List<string>();
        public List<LevelObject> Objects = new List<LevelObject>();
        public List<LevelLight> Lights = new List<LevelLight>();
        public List<LevelFlat> Flats = new List<LevelFlat>();
        public List<string> Warnings = new List<string>();

        public static bool IsGroupTag(string tag) { return GroupTags.Contains(tag); }

        public Bs6Chunk Root { get { return Roots.Count > 0 ? Roots[0] : null; } }

        public static Bs6File Parse(byte[] data)
        {
            Bs6File f = new Bs6File();
            f.ReadChunks(data, 0, data.Length, null, f.Roots, 0);
            f.BuildLevel();
            return f;
        }

        void ReadChunks(byte[] d, int start, int end, Bs6Chunk parent, List<Bs6Chunk> into, int depth)
        {
            int pos = start;
            // A zero length chunk is a complete 8 byte header, so read while a header fits.
            while (pos + 8 <= end)
            {
                Bs6Chunk c = new Bs6Chunk();
                c.Tag = TextUtil.FourCC(d, pos);
                c.Length = BitConverter.ToInt32(d, pos + 4);
                c.Offset = pos;
                c.Parent = parent;
                if (c.Length < 0 || pos + 8 + (long)c.Length > end)
                {
                    Warnings.Add(string.Format("Chunk {0} at 0x{1:X} runs past its parent; truncated.", c.Tag, pos));
                    c.Length = Math.Max(0, end - pos - 8);
                }
                c.Data = new byte[c.Length];
                Buffer.BlockCopy(d, pos + 8, c.Data, 0, c.Length);
                if (c.IsGroup && depth < 32)
                    ReadChunks(d, pos + 8, pos + 8 + c.Length, c, c.Children, depth + 1);
                into.Add(c);
                pos += 8 + c.Length;
            }
        }

        void BuildLevel()
        {
            Bs6Chunk root = Root;
            if (root == null) return;
            if (root.Tag != "GNRL") Warnings.Add("Level does not start with GNRL; the game would reject it.");

            foreach (Bs6Chunk top in root.Children)
            {
                if (top.Tag == "OBJS")
                {
                    // LFIL names accumulate before OBJD indexes into them.
                    foreach (Bs6Chunk c in top.Children)
                        if (c.Tag == "LFIL")
                            for (int i = 0; i + LfilRecordSize <= c.Data.Length; i += LfilRecordSize)
                                ModelNames.Add(GameModelName(TextUtil.ReadCString(c.Data, i, 13)));
                    foreach (Bs6Chunk c in top.Children)
                        if (c.Tag == "OBJD") Objects.Add(ReadObject(c));
                }
                else if (top.Tag == "LITS")
                {
                    foreach (Bs6Chunk c in top.Children)
                        if (c.Tag == "LITD") Lights.Add(ReadLight(c));
                }
                else if (top.Tag == "FLAS")
                {
                    foreach (Bs6Chunk c in top.Children)
                        if (c.Tag == "FLAD") Flats.Add(ReadFlat(c));
                }
            }
        }

        public static string GameModelName(string name)
        {
            return name.IndexOf('.') < 0 ? name + ".3d" : name;
        }

        LevelObject ReadObject(Bs6Chunk c)
        {
            LevelObject o = new LevelObject();
            o.Chunk = c;
            Bs6Chunk x;
            if ((x = c.Child("IDNB")) != null) o.Id = x.Int(0);
            if ((x = c.Child("IDFI")) != null) o.ModelIndex = x.Int(0);
            if ((x = c.Child("SELE")) != null) o.Select = x.Int(0);
            if ((x = c.Child("POSI")) != null) { o.X = x.Int(0); o.Y = x.Int(1); o.Z = x.Int(2); }
            if ((x = c.Child("ANGS")) != null) { o.AngleX = x.Int(0); o.AngleY = x.Int(1); o.AngleZ = x.Int(2); }
            o.ModelName = o.ModelIndex >= 0 && o.ModelIndex < ModelNames.Count ? ModelNames[o.ModelIndex] : "?";
            return o;
        }

        static LevelLight ReadLight(Bs6Chunk c)
        {
            LevelLight l = new LevelLight();
            l.Chunk = c;
            Bs6Chunk x;
            if ((x = c.Child("IDNB")) != null) l.Id = x.Int(0);
            if ((x = c.Child("IDTY")) != null) l.Type = x.Int(0);
            if ((x = c.Child("RADI")) != null) l.Radius = x.Int(0);
            if ((x = c.Child("BRIT")) != null) l.Brightness = x.Int(0);
            if ((x = c.Child("POSI")) != null) { l.X = x.Int(0); l.Y = x.Int(1); l.Z = x.Int(2); }
            return l;
        }

        static LevelFlat ReadFlat(Bs6Chunk c)
        {
            LevelFlat f = new LevelFlat();
            f.Chunk = c;
            Bs6Chunk x;
            if ((x = c.Child("IDNB")) != null) f.Id = x.Int(0);
            if ((x = c.Child("SCAL")) != null) f.Scale = x.Int(0);
            if ((x = c.Child("AMBI")) != null) f.Ambient = x.Int(0);
            if ((x = c.Child("FILN")) != null) f.Image = x.String;
            if ((x = c.Child("POSI")) != null) { f.X = x.Int(0); f.Y = x.Int(1); f.Z = x.Int(2); }
            return f;
        }

        /// <summary>XML dump in the same spirit as the original bs6tool.</summary>
        public void WriteXml(TextWriter tw)
        {
            XmlWriterSettings s = new XmlWriterSettings();
            s.Indent = true;
            s.IndentChars = "  ";
            using (XmlWriter w = XmlWriter.Create(tw, s))
            {
                w.WriteStartDocument();
                if (Roots.Count == 1) WriteChunk(w, Roots[0]);
                else
                {
                    w.WriteStartElement("BS6");
                    foreach (Bs6Chunk c in Roots) WriteChunk(w, c);
                    w.WriteEndElement();
                }
                w.WriteEndDocument();
            }
        }

        static string XmlName(string tag)
        {
            string t = tag.Trim();
            StringBuilder sb = new StringBuilder();
            foreach (char ch in t) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        void WriteChunk(XmlWriter w, Bs6Chunk c)
        {
            w.WriteStartElement(XmlName(c.Tag));
            w.WriteAttributeString("_length", c.Length.ToString(CultureInfo.InvariantCulture));
            if (c.IsGroup)
            {
                foreach (Bs6Chunk k in c.Children) WriteChunk(w, k);
            }
            else
            {
                switch (c.Tag)
                {
                    case "POSI":
                    case "OFST":
                    case "CENT":
                    case "ANGS":
                        w.WriteElementString("x", c.Int(0).ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("y", c.Int(1).ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("z", c.Int(2).ToString(CultureInfo.InvariantCulture));
                        break;
                    case "BBOX":
                        for (int i = 0; i < 2; i++)
                        {
                            w.WriteStartElement("corner");
                            w.WriteElementString("x", c.Int(i * 3).ToString(CultureInfo.InvariantCulture));
                            w.WriteElementString("y", c.Int(i * 3 + 1).ToString(CultureInfo.InvariantCulture));
                            w.WriteElementString("z", c.Int(i * 3 + 2).ToString(CultureInfo.InvariantCulture));
                            w.WriteEndElement();
                        }
                        break;
                    case "LFIL":
                        for (int i = 0, n = 0; i + LfilRecordSize <= c.Data.Length; i += LfilRecordSize, n++)
                        {
                            w.WriteStartElement("name");
                            w.WriteAttributeString("id", n.ToString(CultureInfo.InvariantCulture));
                            w.WriteString(TextUtil.ReadCString(c.Data, i, LfilRecordSize));
                            w.WriteEndElement();
                        }
                        break;
                    case "FILN":
                    case "DIRN":
                        w.WriteString(c.String);
                        break;
                    default:
                        if (c.Data.Length == 4) w.WriteString(c.Int(0).ToString(CultureInfo.InvariantCulture));
                        else w.WriteString(Bs6Chunk.Hex(c.Data, int.MaxValue));
                        break;
                }
            }
            w.WriteEndElement();
        }
    }
}
