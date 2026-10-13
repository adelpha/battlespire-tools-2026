using System;
using System.IO;

namespace Battlespire.Formats
{
    /// <summary>
    /// LZSS decompressor used for compressed BSA entries.
    /// Mirrors the game's decompressor (GAME.EXE 0x0002e3e8): a 4096 byte window
    /// whose first 0xFEE bytes are spaces, writing from 0xFEE, flags read LSB first,
    /// a set bit is a literal, a clear bit is a 12 bit offset / 4 bit length pair.
    /// </summary>
    public static class Lzss
    {
        const int WindowSize = 4096;
        const int WindowStart = 0xFEE;

        public static byte[] Decompress(byte[] src)
        {
            return Decompress(src, 0, src.Length);
        }

        public static byte[] Decompress(byte[] src, int offset, int length)
        {
            byte[] window = new byte[WindowSize];
            for (int i = 0; i < WindowStart; i++)
                window[i] = 0x20;
            int pos = WindowStart;

            MemoryStream output = new MemoryStream(length * 2);
            int end = offset + length;
            int i0 = offset;
            uint flags = 0;

            while (true)
            {
                flags >>= 1;
                if ((flags & 0x100) == 0)
                {
                    if (i0 >= end) break;
                    flags = 0xFF00u | src[i0++];
                }

                if ((flags & 1) != 0)
                {
                    if (i0 >= end) break;
                    byte b = src[i0++];
                    output.WriteByte(b);
                    window[pos] = b;
                    pos = (pos + 1) & 0xFFF;
                }
                else
                {
                    // The game stops if the stream ends half way through a code.
                    if (i0 + 1 >= end) break;
                    int lo = src[i0++];
                    int hi = src[i0++];
                    int copyFrom = lo | ((hi & 0xF0) << 4);
                    int count = (hi & 0x0F) + 3;
                    for (int k = 0; k < count; k++)
                    {
                        byte b = window[(copyFrom + k) & 0xFFF];
                        output.WriteByte(b);
                        window[pos] = b;
                        pos = (pos + 1) & 0xFFF;
                    }
                }
            }

            return output.ToArray();
        }
    }
}
