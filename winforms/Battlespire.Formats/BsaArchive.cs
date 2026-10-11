using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Battlespire.Formats
{
    public enum BsaKind
    {
        /// <summary>Type 0x100: 18 byte footer records with 13 byte names.</summary>
        Named,
        /// <summary>Type 0x200: 8 byte footer records with 32 bit ids.</summary>
        Numeric,
        /// <summary>No type word (Arena / Terminator era). Named records, data at offset 2.</summary>
        NamedUntyped
    }

    public class BsaEntry
    {
        public int Index;
        public string Name;
        public uint Id;
        /// <summary>Raw flag byte (byte 13 of a named record). Bit 0 means LZSS compressed.</summary>
        public byte Flags;
        public long Offset;
        public int Size;

        public bool Compressed { get { return (Flags & 1) != 0; } }

        public override string ToString() { return Name; }
    }

    /// <summary>
    /// BSA archive reader, following BsaOpenArchive (0x0002d9af),
    /// the lookup in 0x0002dce8 and the loader in 0x0002df52.
    /// Entry data is concatenated starting after the header; the directory
    /// sits at the end of the file.
    /// </summary>
    public class BsaArchive : IDisposable
    {
        public const ushort TypeNamed = 0x100;
        public const ushort TypeNumeric = 0x200;
        public const int NamedRecordSize = 18;
        public const int NumericRecordSize = 8;

        FileStream stream;

        public string Path { get; private set; }
        public BsaKind Kind { get; private set; }
        public ushort TypeWord { get; private set; }
        public List<BsaEntry> Entries { get; private set; }
        public long FileLength { get; private set; }
        /// <summary>Non-empty when the directory does not account for the whole file.</summary>
        public string Warning { get; private set; }

        public BsaArchive(string path)
        {
            Path = path;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Load();
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        void Load()
        {
            FileLength = stream.Length;
            if (FileLength < 4)
                throw new InvalidDataException("File is too small to be a BSA archive.");

            byte[] header = ReadAt(0, 4);
            int count = BitConverter.ToUInt16(header, 0);
            TypeWord = BitConverter.ToUInt16(header, 2);

            long dataStart;
            int recordSize;
            if (TypeWord == TypeNamed)
            {
                Kind = BsaKind.Named;
                dataStart = 4;
                recordSize = NamedRecordSize;
            }
            else if (TypeWord == TypeNumeric)
            {
                Kind = BsaKind.Numeric;
                dataStart = 4;
                recordSize = NumericRecordSize;
            }
            else
            {
                Kind = BsaKind.NamedUntyped;
                dataStart = 2;
                recordSize = NamedRecordSize;
            }

            long dirSize = (long)count * recordSize;
            if (dirSize > FileLength - dataStart)
                throw new InvalidDataException("Directory is larger than the file; this is not a BSA archive.");

            byte[] dir = ReadAt(FileLength - dirSize, (int)dirSize);
            Entries = new List<BsaEntry>(count);
            long offset = dataStart;
            for (int i = 0; i < count; i++)
            {
                int p = i * recordSize;
                BsaEntry e = new BsaEntry();
                e.Index = i;
                e.Offset = offset;
                if (Kind == BsaKind.Numeric)
                {
                    // The game compares all 32 bits as the id (0x0002dce8) and has no
                    // compression flag for numeric records.
                    e.Id = BitConverter.ToUInt32(dir, p);
                    e.Size = BitConverter.ToInt32(dir, p + 4);
                    e.Name = e.Id.ToString();
                }
                else
                {
                    e.Name = ReadCString(dir, p, 13);
                    e.Flags = dir[p + 13];
                    e.Size = BitConverter.ToInt32(dir, p + 14);
                }
                if (e.Size < 0 || offset + e.Size > FileLength - dirSize)
                    throw new InvalidDataException(string.Format("Entry {0} ({1}) runs past the directory.", i, e.Name));
                offset += e.Size;
                Entries.Add(e);
            }

            if (offset != FileLength - dirSize)
                Warning = string.Format("{0} bytes between the last entry and the directory are not used by any entry.",
                    FileLength - dirSize - offset);
        }

        static string ReadCString(byte[] buf, int offset, int max)
        {
            int n = 0;
            while (n < max && buf[offset + n] != 0) n++;
            return TextUtil.Latin1.GetString(buf, offset, n);
        }

        byte[] ReadAt(long offset, int length)
        {
            byte[] buf = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(buf, read, length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return buf;
        }

        /// <summary>Returns the stored bytes of an entry without decompressing.</summary>
        public byte[] ReadRaw(BsaEntry e)
        {
            return ReadAt(e.Offset, e.Size);
        }

        /// <summary>Returns the entry's contents, decompressed when flagged.</summary>
        public byte[] Read(BsaEntry e)
        {
            byte[] raw = ReadRaw(e);
            return e.Compressed ? Lzss.Decompress(raw) : raw;
        }

        public BsaEntry Find(string name)
        {
            foreach (BsaEntry e in Entries)
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return e;
            return null;
        }

        public BsaEntry Find(uint id)
        {
            foreach (BsaEntry e in Entries)
                if (e.Id == id)
                    return e;
            return null;
        }

        /// <summary>
        /// A file name for extracting an entry. Numeric entries get an extension guessed
        /// from their contents; duplicates are handled by the caller.
        /// </summary>
        public string SuggestFileName(BsaEntry e, byte[] data)
        {
            if (Kind != BsaKind.Numeric)
                return SanitizeFileName(e.Name);
            return e.Name + FileSniffer.GuessExtension(data);
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            StringBuilder sb = new StringBuilder(name.Length);
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in name)
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public void Dispose()
        {
            if (stream != null)
            {
                stream.Dispose();
                stream = null;
            }
        }
    }
}
