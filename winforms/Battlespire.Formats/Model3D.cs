using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Battlespire.Formats
{
    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator *(Vec3 a, float s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public static Vec3 Cross(Vec3 a, Vec3 b) { return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        public static float Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public override string ToString() { return string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", X, Y, Z); }
    }

    public struct FaceVertex
    {
        /// <summary>Index into the frame's point list.</summary>
        public int Point;
        /// <summary>Texture coordinates in texels (stored as 12.4 fixed point).</summary>
        public float U, V;
        public short RawU, RawV;
    }

    public class Face
    {
        public byte Unknown1;
        /// <summary>Texture/material word at +2. Values at or above 0xFFF00000 are skipped by the game's plane setup.</summary>
        public uint Texture;
        public byte Unknown6, Unknown7;
        public ushort Unknown8;
        public FaceVertex[] Vertices;
        public Vec3 Normal;
    }

    public class ModelFrame
    {
        public Vec3[] Points;
        public Vec3[] Normals;
    }

    public class ModelObjectRecord
    {
        public float[] Values = new float[4];
        public List<KeyValuePair<int, short>> Entries = new List<KeyValuePair<int, short>>();
    }

    /// <summary>
    /// Battlespire .3D model (v2.5, v2.6, v2.7; the game converts them all to an
    /// in-memory v2.8 in 0x0008767c). Header is 64 bytes:
    ///   0x00 version, 0x04 point count, 0x08 plane count, 0x0C radius,
    ///   0x10 frame count, 0x14 frame table offset, 0x18 plane data offset,
    ///   0x1C object data offset, 0x20 object data count, 0x24..0x2C unknown,
    ///   0x30 point list offset, 0x34 normal list offset, 0x38 unknown,
    ///   0x3C plane list offset.
    /// Points and normals are 24.8 fixed point (the game multiplies by 1/256),
    /// texture coordinates are 12.4 fixed point (1/16).
    /// Before v2.6 face point offsets are stored as index*4; from v2.6 they are index*12.
    /// </summary>
    public class Model3D
    {
        public string Version;
        public int PointCount;
        public int PlaneCount;
        public int Radius;
        public int FrameCount;
        public int[] Header = new int[16];
        public List<Face> Faces = new List<Face>();
        public List<ModelFrame> Frames = new List<ModelFrame>();
        public List<ModelObjectRecord> ObjectRecords = new List<ModelObjectRecord>();
        public List<string> Warnings = new List<string>();

        public const float PointScale = 1f / 256f;
        public const float UvScale = 1f / 16f;

        public bool UsesQuarterOffsets
        {
            get { return string.CompareOrdinal(Version, "v2.6") < 0; }
        }

        public static Model3D Parse(byte[] d)
        {
            if (d.Length < 64) throw new InvalidDataException("File is too small for a .3D header.");
            Model3D m = new Model3D();
            m.Version = TextUtil.FourCC(d, 0);
            if (!m.Version.StartsWith("v2.") && !m.Version.StartsWith("v3."))
                throw new InvalidDataException("Not a .3D model (version '" + m.Version + "').");
            for (int i = 0; i < 16; i++) m.Header[i] = BitConverter.ToInt32(d, i * 4);
            m.PointCount = m.Header[1];
            m.PlaneCount = m.Header[2];
            m.Radius = m.Header[3];
            m.FrameCount = m.Header[4];

            if (m.PointCount < 0 || m.PointCount > 0x100000 || m.PlaneCount < 0 || m.PlaneCount > 0x100000)
                throw new InvalidDataException("Implausible point or plane count.");

            // Frames: animated models keep per-frame point and normal offsets in a
            // table of 16 byte records at 0x14; static models use 0x30 and 0x34.
            if (m.FrameCount > 0)
            {
                int table = m.Header[5];
                for (int f = 0; f < m.FrameCount; f++)
                {
                    int rec = table + f * 16;
                    if (rec + 8 > d.Length) { m.Warnings.Add("Frame table is truncated."); break; }
                    m.Frames.Add(ReadFrame(m, d, BitConverter.ToInt32(d, rec), BitConverter.ToInt32(d, rec + 4)));
                }
            }
            else
            {
                m.Frames.Add(ReadFrame(m, d, m.Header[12], m.Header[13]));
            }

            // Planes
            int p = m.Header[15];
            int divisor = m.UsesQuarterOffsets ? 4 : 12;
            for (int i = 0; i < m.PlaneCount; i++)
            {
                if (p + 10 > d.Length) { m.Warnings.Add("Plane list is truncated."); break; }
                Face face = new Face();
                int n = d[p];
                face.Unknown1 = d[p + 1];
                face.Texture = BitConverter.ToUInt32(d, p + 2);
                face.Unknown6 = d[p + 6];
                face.Unknown7 = d[p + 7];
                face.Unknown8 = BitConverter.ToUInt16(d, p + 8);
                p += 10;
                face.Vertices = new FaceVertex[n];
                for (int k = 0; k < n; k++)
                {
                    if (p + 8 > d.Length) { m.Warnings.Add("Plane list is truncated."); break; }
                    int off = BitConverter.ToInt32(d, p);
                    FaceVertex v = new FaceVertex();
                    v.Point = off / divisor;
                    if (off % divisor != 0 || v.Point < 0 || v.Point >= m.PointCount)
                        m.Warnings.Add(string.Format("Plane {0} has a bad point offset {1}.", i, off));
                    v.RawU = BitConverter.ToInt16(d, p + 4);
                    v.RawV = BitConverter.ToInt16(d, p + 6);
                    v.U = v.RawU * UvScale;
                    v.V = v.RawV * UvScale;
                    face.Vertices[k] = v;
                    p += 8;
                }
                if (m.Frames.Count > 0 && m.Frames[0].Normals != null && i < m.Frames[0].Normals.Length)
                    face.Normal = m.Frames[0].Normals[i];
                m.Faces.Add(face);
            }

            // Object data records: 18 byte header (four 24.8 values, then a 16 bit count)
            // followed by count 6 byte entries (plane offset, value).
            int o = m.Header[7];
            for (int i = 0; i < m.Header[8] && o > 0; i++)
            {
                if (o + 18 > d.Length) { m.Warnings.Add("Object data is truncated."); break; }
                ModelObjectRecord r = new ModelObjectRecord();
                for (int k = 0; k < 4; k++) r.Values[k] = BitConverter.ToInt32(d, o + k * 4) * PointScale;
                int count = BitConverter.ToUInt16(d, o + 16);
                o += 18;
                for (int k = 0; k < count && o + 6 <= d.Length; k++, o += 6)
                    r.Entries.Add(new KeyValuePair<int, short>(BitConverter.ToInt32(d, o), BitConverter.ToInt16(d, o + 4)));
                m.ObjectRecords.Add(r);
            }

            return m;
        }

        static ModelFrame ReadFrame(Model3D m, byte[] d, int pointOffset, int normalOffset)
        {
            ModelFrame f = new ModelFrame();
            f.Points = new Vec3[m.PointCount];
            for (int i = 0; i < m.PointCount; i++)
            {
                int q = pointOffset + i * 12;
                if (q + 12 > d.Length) { m.Warnings.Add("Point list is truncated."); break; }
                f.Points[i] = new Vec3(BitConverter.ToInt32(d, q) * PointScale,
                                       BitConverter.ToInt32(d, q + 4) * PointScale,
                                       BitConverter.ToInt32(d, q + 8) * PointScale);
            }
            f.Normals = new Vec3[m.PlaneCount];
            for (int i = 0; i < m.PlaneCount; i++)
            {
                int q = normalOffset + i * 12;
                if (normalOffset <= 0 || q + 12 > d.Length) break;
                f.Normals[i] = new Vec3(BitConverter.ToInt32(d, q) * PointScale,
                                        BitConverter.ToInt32(d, q + 4) * PointScale,
                                        BitConverter.ToInt32(d, q + 8) * PointScale);
            }
            return f;
        }

        public void GetBounds(int frame, out Vec3 min, out Vec3 max)
        {
            min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            Vec3[] pts = Frames[Math.Max(0, Math.Min(frame, Frames.Count - 1))].Points;
            foreach (Vec3 v in pts)
            {
                min = new Vec3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
                max = new Vec3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
            }
            if (pts.Length == 0) { min = new Vec3(); max = new Vec3(); }
        }

        /// <summary>
        /// Wavefront OBJ with positions, per-face normals and texture coordinates.
        /// UVs are divided by <paramref name="textureSize"/> (texels) and V is flipped.
        /// </summary>
        public string ToObj(int frame, string name, float textureSize)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Battlespire " + Version + " model" + (string.IsNullOrEmpty(name) ? "" : " " + name));
            sb.AppendLine("# Coordinates are game units (fixed point / 256); UVs are texels / " + textureSize.ToString(ci));
            if (!string.IsNullOrEmpty(name)) sb.AppendLine("o " + name);
            ModelFrame fr = Frames[Math.Max(0, Math.Min(frame, Frames.Count - 1))];
            foreach (Vec3 v in fr.Points)
                sb.AppendLine(string.Format(ci, "v {0} {1} {2}", v.X, v.Y, v.Z));

            int vt = 1, vn = 1;
            uint lastTex = 0xFFFFFFFF;
            for (int i = 0; i < Faces.Count; i++)
            {
                Face f = Faces[i];
                if (f.Vertices.Length < 3) continue;
                if (f.Texture != lastTex)
                {
                    sb.AppendLine(string.Format("usemtl tex_{0:X8}", f.Texture));
                    lastTex = f.Texture;
                }
                Vec3 n = i < fr.Normals.Length ? fr.Normals[i] : new Vec3();
                sb.AppendLine(string.Format(ci, "vn {0} {1} {2}", n.X, n.Y, n.Z));
                foreach (FaceVertex v in f.Vertices)
                    sb.AppendLine(string.Format(ci, "vt {0} {1}", v.U / textureSize, 1f - v.V / textureSize));
                sb.Append("f");
                for (int k = 0; k < f.Vertices.Length; k++)
                    sb.AppendFormat(" {0}/{1}/{2}", f.Vertices[k].Point + 1, vt + k, vn);
                sb.AppendLine();
                vt += f.Vertices.Length;
                vn++;
            }
            return sb.ToString();
        }

        /// <summary>ASCII PLY, as the original 3dtool produced, but with float coordinates.</summary>
        public string ToPly(int frame)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            ModelFrame fr = Frames[Math.Max(0, Math.Min(frame, Frames.Count - 1))];
            int faces = 0;
            foreach (Face f in Faces) if (f.Vertices.Length >= 3) faces++;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ply");
            sb.AppendLine("format ascii 1.0");
            sb.AppendLine("comment Battlespire " + Version + " model");
            sb.AppendLine("element vertex " + fr.Points.Length);
            sb.AppendLine("property float x");
            sb.AppendLine("property float y");
            sb.AppendLine("property float z");
            sb.AppendLine("element face " + faces);
            sb.AppendLine("property list uchar int vertex_indices");
            sb.AppendLine("end_header");
            foreach (Vec3 v in fr.Points)
                sb.AppendLine(string.Format(ci, "{0} {1} {2}", v.X, v.Y, v.Z));
            foreach (Face f in Faces)
            {
                if (f.Vertices.Length < 3) continue;
                sb.Append(f.Vertices.Length);
                foreach (FaceVertex v in f.Vertices) sb.Append(' ').Append(v.Point);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
