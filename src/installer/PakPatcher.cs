// SPDX-License-Identifier: GPL-3.0-only
// Reads and rewrites SnowRunner's shader.pak in memory.
//   shader.pak is a plain zip: every entry stored, no extra fields, zero padded to a 4096 byte multiple behind the end
//   record. Its entry ...\shaders_cache_mr\shadercachedx11.sdc is a 12 byte header (date stamp, packed size, unpacked
//   size) and one zlib stream. Unpacked: a list of include files, a program count, a blob count, then
//   count x (u32 size, DXBC shader), then a program table that refers to shaders by index, so a shader may change size.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SnowRunnerGtao
{
    class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    enum PakState { NotInstalled, Installed, InstalledOther, UnknownShader }

    static class Crc32
    {
        static readonly uint[] Table = MakeTable();
        static uint[] MakeTable()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1; t[n] = c; }
            return t;
        }
        public static uint Compute(byte[] b, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset, end = offset + count; i < end; i++) c = (c >> 8) ^ Table[(c ^ b[i]) & 255];
            return ~c;
        }
    }

    static class Zlib
    {
        static uint Adler32(byte[] b)
        {
            uint a = 1, s = 0;
            int i = 0;
            while (i < b.Length)
            {
                int end = Math.Min(i + 5552, b.Length);
                for (; i < end; i++) { a += b[i]; s += a; }
                a %= 65521; s %= 65521;
            }
            return (s << 16) | a;
        }

        public static byte[] Inflate(byte[] data, int offset, int count, int expectedSize)
        {
            if (count < 8 || (data[offset] & 0x0F) != 8 || ((data[offset] << 8) | data[offset + 1]) % 31 != 0) throw new PatchException(Text.NotShaderPak);
            byte[] result = new byte[expectedSize];
            using (MemoryStream ms = new MemoryStream(data, offset + 2, count - 6, false))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < expectedSize) { int n = ds.Read(result, read, expectedSize - read); if (n <= 0) break; read += n; }
                if (read != expectedSize || ds.ReadByte() != -1) throw new PatchException(Text.NotShaderPak);
            }
            int p = offset + count - 4;
            uint stored = ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) | ((uint)data[p + 2] << 8) | data[p + 3];
            if (stored != Adler32(result)) throw new PatchException(Text.NotShaderPak);
            return result;
        }

        public static byte[] Deflate(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data.Length / 3))
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);   // the shipped stream carries the same header
                using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Fastest, true)) ds.Write(data, 0, data.Length);
                uint adler = Adler32(data);
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }
    }

    class ZipEntry
    {
        public int Cd, CdLength, Lho, DataOffset, CSize;
        public ushort Flags, Method;
        public uint Crc;
        public string Name;
    }

    class ZipLayout
    {
        public List<ZipEntry> Entries = new List<ZipEntry>();
        public int CdOffset, Eocd, CommentLength;
    }

    class PakAnalysis
    {
        public PakState State;
        public string Sha256;          // of the whole pak as it is on disk
        public int[] Slots;            // indices of the two SSAO generation shaders
    }

    static class PakPatcher
    {
        const string CacheName = "shadercachedx11.sdc";
        static readonly uint[] OriginalCrcs = { 0xEA2414F8u, 0xA3716E2Bu };
        // every shader this mod has shipped, so later versions recognise earlier ones
        static readonly uint[] ShippedCrcs = { 0x6B795573u };

        static ushort U16(byte[] b, int p) { return (ushort)(b[p] | (b[p + 1] << 8)); }
        static uint U32(byte[] b, int p) { return (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)); }
        static void W32(byte[] b, int p, uint v) { b[p] = (byte)v; b[p + 1] = (byte)(v >> 8); b[p + 2] = (byte)(v >> 16); b[p + 3] = (byte)(v >> 24); }
        static void Need(bool ok) { if (!ok) throw new PatchException(Text.NotShaderPak); }

        // ---- zip
        static ZipLayout ParseZip(byte[] b)
        {
            Need(b.Length > 22);
            int e = b.Length - 22, floor = Math.Max(0, b.Length - 22 - 65535 - 8192);
            while (e >= floor && U32(b, e) != 0x06054B50u) e--;
            Need(e >= floor);
            ZipLayout z = new ZipLayout();
            z.Eocd = e; z.CommentLength = U16(b, e + 20);
            int count = U16(b, e + 10);
            long cdOff = U32(b, e + 16);
            Need(cdOff < e && e + 22 + z.CommentLength <= b.Length);
            z.CdOffset = (int)cdOff;
            for (int t = e + 22 + z.CommentLength; t < b.Length; t++) Need(b[t] == 0);
            int p = z.CdOffset;
            for (int i = 0; i < count; i++)
            {
                Need(p + 46 <= e && U32(b, p) == 0x02014B50u);
                ZipEntry ent = new ZipEntry();
                int nameLen = U16(b, p + 28), extraLen = U16(b, p + 30), cmtLen = U16(b, p + 32);
                ent.Cd = p; ent.CdLength = 46 + nameLen + extraLen + cmtLen;
                ent.Flags = U16(b, p + 8); ent.Method = U16(b, p + 10); ent.Crc = U32(b, p + 16);
                long csize = U32(b, p + 20), lho = U32(b, p + 42);
                Need(p + ent.CdLength <= e && lho + 30 <= z.CdOffset);
                ent.Lho = (int)lho; ent.CSize = (int)csize;
                ent.Name = Encoding.ASCII.GetString(b, p + 46, nameLen);
                Need(U32(b, ent.Lho) == 0x04034B50u);
                ent.DataOffset = ent.Lho + 30 + U16(b, ent.Lho + 26) + U16(b, ent.Lho + 28);
                Need(csize >= 0 && (long)ent.DataOffset + csize <= z.CdOffset);
                z.Entries.Add(ent);
                p += ent.CdLength;
            }
            return z;
        }

        // Rewrites one stored entry. Everything else is copied as it is; offsets, sizes and checksums are repaired.
        static byte[] RewriteZip(byte[] b, ZipLayout z, ZipEntry target, byte[] data)
        {
            Need(target.Method == 0 && target.Flags == 0);
            List<ZipEntry> order = new List<ZipEntry>(z.Entries);
            order.Sort(delegate(ZipEntry x, ZipEntry y) { return x.Lho.CompareTo(y.Lho); });
            uint crc = Crc32.Compute(data, 0, data.Length);
            Dictionary<ZipEntry, int> newLho = new Dictionary<ZipEntry, int>();
            using (MemoryStream ms = new MemoryStream(b.Length + data.Length))
            {
                for (int i = 0; i < order.Count; i++)
                {
                    ZipEntry ent = order[i];
                    int end = i + 1 < order.Count ? order[i + 1].Lho : z.CdOffset;
                    newLho[ent] = (int)ms.Position;
                    if (ent == target)
                    {
                        Need(end == ent.DataOffset + ent.CSize);
                        byte[] head = new byte[ent.DataOffset - ent.Lho];
                        Buffer.BlockCopy(b, ent.Lho, head, 0, head.Length);
                        W32(head, 14, crc); W32(head, 18, (uint)data.Length); W32(head, 22, (uint)data.Length);
                        ms.Write(head, 0, head.Length);
                        ms.Write(data, 0, data.Length);
                    }
                    else ms.Write(b, ent.Lho, end - ent.Lho);
                }
                int cdStart = (int)ms.Position;
                foreach (ZipEntry ent in z.Entries)
                {
                    byte[] rec = new byte[ent.CdLength];
                    Buffer.BlockCopy(b, ent.Cd, rec, 0, rec.Length);
                    W32(rec, 42, (uint)newLho[ent]);
                    if (ent == target) { W32(rec, 16, crc); W32(rec, 20, (uint)data.Length); W32(rec, 24, (uint)data.Length); }
                    ms.Write(rec, 0, rec.Length);
                }
                int cdSize = (int)ms.Position - cdStart;
                byte[] eocd = new byte[22 + z.CommentLength];
                Buffer.BlockCopy(b, z.Eocd, eocd, 0, eocd.Length);
                W32(eocd, 12, (uint)cdSize); W32(eocd, 16, (uint)cdStart);
                ms.Write(eocd, 0, eocd.Length);
                // the game's paks end on a 4096 byte boundary, zero filled
                if (b.Length % 4096 == 0) { int rest = (int)(ms.Position % 4096); if (rest != 0) ms.Write(new byte[4096 - rest], 0, 4096 - rest); }
                else ms.Write(b, z.Eocd + 22 + z.CommentLength, b.Length - (z.Eocd + 22 + z.CommentLength));
                return ms.ToArray();
            }
        }

        // ---- shader cache
        class Cache
        {
            public uint Stamp;
            public byte[] Data;          // unpacked
            public int BlobsStart, TableOffset;
            public int[] BlobOffset, BlobSize;
        }

        static Cache ReadCache(byte[] pak, ZipEntry ent)
        {
            Need(ent.Method == 0 && ent.CSize > 12);
            int o = ent.DataOffset;
            Need(U32(pak, o + 4) == (uint)(ent.CSize - 12));
            long unpacked = U32(pak, o + 8);
            Need(unpacked > 1024 && unpacked < 1500L * 1024 * 1024);
            Cache c = new Cache();
            c.Stamp = U32(pak, o);
            c.Data = Zlib.Inflate(pak, o + 12, ent.CSize - 12, (int)unpacked);
            byte[] d = c.Data;
            int p = 4;
            uint includes = U32(d, 0);
            Need(includes < 4096);
            for (uint i = 0; i < includes; i++) { Need(p + 2 <= d.Length); p += 2 + U16(d, p) + 4; Need(p + 8 <= d.Length); }
            uint count = U32(d, p + 4);
            Need(count > 0 && count < 200000);
            p += 8;
            c.BlobsStart = p;
            c.BlobOffset = new int[count]; c.BlobSize = new int[count];
            for (int i = 0; i < count; i++)
            {
                Need(p + 4 + 32 <= d.Length);
                long size = U32(d, p);
                Need(size >= 32 && p + 4 + size <= d.Length && U32(d, p + 4) == 0x43425844u && U32(d, p + 4 + 24) == size);   // "DXBC"
                c.BlobOffset[i] = p + 4; c.BlobSize[i] = (int)size;
                p += 4 + (int)size;
            }
            c.TableOffset = p;
            return c;
        }

        static bool Contains(byte[] d, int offset, int count, string ascii)
        {
            byte[] n = Encoding.ASCII.GetBytes(ascii + "\0");
            for (int i = offset, end = offset + count - n.Length; i <= end; i++)
            {
                int k = 0;
                while (k < n.Length && d[i + k] == n[k]) k++;
                if (k == n.Length) return true;
            }
            return false;
        }

        // The SSAO generation shaders are the ones whose resource table names these three, whoever compiled them
        static bool IsSsaoShader(byte[] d, int offset, int size)
        {
            if (size > 65536) return false;
            uint chunks = U32(d, offset + 28);
            if (chunks > 16) return false;
            for (uint i = 0; i < chunks; i++)
            {
                long co = U32(d, offset + 32 + (int)i * 4);
                if (co + 8 > size) return false;
                int c = offset + (int)co;
                if (U32(d, c) != 0x46454452u) continue;   // "RDEF"
                long len = U32(d, c + 4);
                if (co + 8 + len > size) return false;
                return Contains(d, c + 8, (int)len, "g_txDither") && Contains(d, c + 8, (int)len, "g_txZ") && Contains(d, c + 8, (int)len, "g_vRadiusMinMax");
            }
            return false;
        }

        static ZipEntry FindCacheEntry(ZipLayout z)
        {
            foreach (ZipEntry e in z.Entries) if (e.Name.EndsWith(CacheName, StringComparison.OrdinalIgnoreCase)) return e;
            throw new PatchException(Text.NotShaderPak);
        }

        static PakAnalysis Classify(Cache c, uint ownCrc)
        {
            List<int> slots = new List<int>();
            for (int i = 0; i < c.BlobOffset.Length; i++) if (IsSsaoShader(c.Data, c.BlobOffset[i], c.BlobSize[i])) slots.Add(i);
            PakAnalysis a = new PakAnalysis();
            a.Slots = slots.ToArray();
            if (slots.Count != 2) { a.State = PakState.UnknownShader; return a; }
            int original = 0, own = 0, shipped = 0;
            foreach (int i in slots)
            {
                uint crc = Crc32.Compute(c.Data, c.BlobOffset[i], c.BlobSize[i]);
                if (Array.IndexOf(OriginalCrcs, crc) >= 0) original++;
                else if (crc == ownCrc) own++;
                else if (Array.IndexOf(ShippedCrcs, crc) >= 0) shipped++;
            }
            if (original == 2) a.State = PakState.NotInstalled;
            else if (own == 2) a.State = PakState.Installed;
            else if (own + shipped == 2) a.State = PakState.InstalledOther;
            else a.State = PakState.UnknownShader;
            return a;
        }

        public static string Sha256Hex(byte[] b)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }

        public static PakAnalysis Analyze(byte[] pak, byte[] shader)
        {
            ZipLayout z = ParseZip(pak);
            Cache c = ReadCache(pak, FindCacheEntry(z));
            PakAnalysis a = Classify(c, Crc32.Compute(shader, 0, shader.Length));
            a.Sha256 = Sha256Hex(pak);
            return a;
        }

        // Returns the new pak. Throws unless the pak holds the original shaders or a version of this mod.
        public static byte[] Patch(byte[] pak, byte[] shader)
        {
            ZipLayout z = ParseZip(pak);
            ZipEntry ent = FindCacheEntry(z);
            Cache c = ReadCache(pak, ent);
            uint ownCrc = Crc32.Compute(shader, 0, shader.Length);
            PakAnalysis a = Classify(c, ownCrc);
            if (a.State == PakState.UnknownShader) throw new PatchException(Text.UnknownShader);

            byte[] rebuilt;
            using (MemoryStream ms = new MemoryStream(c.Data.Length + 2 * shader.Length))
            {
                ms.Write(c.Data, 0, c.BlobsStart);
                byte[] size = new byte[4];
                for (int i = 0; i < c.BlobOffset.Length; i++)
                {
                    bool swap = Array.IndexOf(a.Slots, i) >= 0;
                    W32(size, 0, (uint)(swap ? shader.Length : c.BlobSize[i]));
                    ms.Write(size, 0, 4);
                    if (swap) ms.Write(shader, 0, shader.Length); else ms.Write(c.Data, c.BlobOffset[i], c.BlobSize[i]);
                }
                ms.Write(c.Data, c.TableOffset, c.Data.Length - c.TableOffset);
                rebuilt = ms.ToArray();
            }
            int tableLength = c.Data.Length - c.TableOffset;
            c = null;

            byte[] packed = Zlib.Deflate(rebuilt);
            byte[] sdc = new byte[12 + packed.Length];
            W32(sdc, 0, U32(pak, ent.DataOffset)); W32(sdc, 4, (uint)packed.Length); W32(sdc, 8, (uint)rebuilt.Length);
            Buffer.BlockCopy(packed, 0, sdc, 12, packed.Length);
            packed = null; rebuilt = null;

            byte[] result = RewriteZip(pak, z, ent, sdc);
            sdc = null;

            // read back what was built before it goes anywhere near the game
            ZipLayout z2 = ParseZip(result);
            if (z2.Entries.Count != z.Entries.Count) throw new PatchException(Text.WriteFailed);
            for (int i = 0; i < z.Entries.Count; i++)
            {
                ZipEntry before = z.Entries[i], after = z2.Entries[i];
                if (before.Name != after.Name || Crc32.Compute(result, after.DataOffset, after.CSize) != after.Crc) throw new PatchException(Text.WriteFailed);
                if (before != ent && (before.CSize != after.CSize || before.Crc != after.Crc)) throw new PatchException(Text.WriteFailed);
            }
            Cache check = ReadCache(result, FindCacheEntry(z2));
            if (check.Data.Length - check.TableOffset != tableLength || Classify(check, ownCrc).State != PakState.Installed) throw new PatchException(Text.WriteFailed);
            return result;
        }
    }
}
