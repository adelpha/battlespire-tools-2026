using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Battlespire.Formats
{
    public class RscEntry
    {
        public ushort Id;
        public int Offset;
        /// <summary>Bytes up to the next record's offset (0 when two records share text).</summary>
        public int Length;
        /// <summary>Bytes from the offset up to (not including) the 0xFE terminator.</summary>
        public byte[] Raw;
        /// <summary>Alternative texts separated by 0xFF; the game picks one at random.</summary>
        public List<byte[]> Variants = new List<byte[]>();
    }

    /// <summary>
    /// TEXT.RSC ("DAGTEXT"), read as in 0x0003c08a: a 16 bit table length, then
    /// table length / 6 records of (16 bit id, 32 bit file offset). The last record
    /// (id 0xFFFF) only marks the end of the final text. A text runs from its offset to
    /// 0xFE and may hold several variants split by 0xFF.
    /// Control bytes (0x3c3dc): 0xF8 dropped, 0xF9/0xFA dropped with one argument byte,
    /// 0xFB tab plus an argument byte, 0xFC carriage return, 0xFD new line.
    /// </summary>
    public class RscFile
    {
        public List<RscEntry> Entries = new List<RscEntry>();
        public int TableLength;

        public static RscFile Parse(byte[] d)
        {
            if (d.Length < 2) throw new InvalidDataException("File is too small.");
            RscFile f = new RscFile();
            f.TableLength = BitConverter.ToUInt16(d, 0);
            int count = f.TableLength / 6;
            if (2 + count * 6 > d.Length) throw new InvalidDataException("Index runs past the end of the file.");

            List<KeyValuePair<ushort, int>> table = new List<KeyValuePair<ushort, int>>();
            for (int i = 0; i < count; i++)
                table.Add(new KeyValuePair<ushort, int>(BitConverter.ToUInt16(d, 2 + i * 6), BitConverter.ToInt32(d, 4 + i * 6)));

            for (int i = 0; i < table.Count; i++)
            {
                int start = table[i].Value;
                int end = i + 1 < table.Count ? table[i + 1].Value : d.Length;
                if (table[i].Key == 0xFFFF && i == table.Count - 1) break;
                if (start < 0 || start > d.Length) continue;
                end = Math.Max(start, Math.Min(end, d.Length));

                RscEntry e = new RscEntry();
                e.Id = table[i].Key;
                e.Offset = start;
                e.Length = end - start;
                // The game reads from the offset up to the first 0xFE, whatever the next
                // record's offset is; some records (e.g. id 14) share an offset with a
                // later one and have no bytes of their own.
                int stop = start;
                while (stop < d.Length && d[stop] != 0xFE) stop++;
                e.Raw = new byte[stop - start];
                Buffer.BlockCopy(d, start, e.Raw, 0, e.Raw.Length);

                // Split into variants at 0xFF, stopping at 0xFE.
                List<byte> cur = new List<byte>();
                for (int k = 0; k < e.Raw.Length; k++)
                {
                    byte b = e.Raw[k];
                    if (b == 0xFE) break;
                    if (b == 0xFF)
                    {
                        if (k + 1 < e.Raw.Length && e.Raw[k + 1] == 0xFE) break;
                        e.Variants.Add(cur.ToArray());
                        cur.Clear();
                        continue;
                    }
                    cur.Add(b);
                }
                e.Variants.Add(cur.ToArray());
                f.Entries.Add(e);
            }
            return f;
        }

        /// <summary>
        /// Converts a variant to readable text. With <paramref name="showCodes"/> the
        /// control bytes are shown as {XX} or {XX yy}; otherwise they are applied the
        /// way the game's plain-text formatter does.
        /// </summary>
        public static string Decode(byte[] v, bool showCodes)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < v.Length; i++)
            {
                byte b = v[i];
                switch (b)
                {
                    case 0xF8:
                        if (showCodes) sb.Append("{F8}");
                        break;
                    case 0xF9:
                    case 0xFA:
                    case 0xFB:
                        {
                            int arg = i + 1 < v.Length ? v[i + 1] : 0;
                            if (showCodes) sb.AppendFormat("{{{0:X2} {1:X2}}}", b, arg);
                            else if (b == 0xFB) sb.Append('\t');
                            i++;
                            break;
                        }
                    case 0xFC:
                        sb.Append(showCodes ? "{FC}\r\n" : "\r\n");
                        break;
                    case 0xFD:
                        sb.Append(showCodes ? "{FD}\r\n" : "\r\n");
                        break;
                    default:
                        if (b < 32 && showCodes) sb.AppendFormat("{{{0:X2}}}", b);
                        else if (b < 32 && b != 9) sb.Append(' ');
                        else sb.Append(TextUtil.Latin1.GetString(new byte[] { b }));
                        break;
                }
            }
            return sb.ToString();
        }

        public string ToTsv(bool showCodes)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Id\tId(hex)\tOffset\tVariant\tText");
            foreach (RscEntry e in Entries)
                for (int v = 0; v < e.Variants.Count; v++)
                    sb.AppendFormat("{0}\t0x{0:X}\t0x{1:X}\t{2}\t{3}\r\n", e.Id, e.Offset, v,
                        Decode(e.Variants[v], showCodes).Replace("\r\n", "\\n").Replace("\t", "\\t"));
            return sb.ToString();
        }
    }
}
