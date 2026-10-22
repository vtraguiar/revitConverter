using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VerificadorRevit
{
    // Lê os metadados que o Revit grava em texto dentro do .rvt/.rfa (contêiner OLE / Compound File).
    public sealed class RvtInfo
    {
        public string File, Version, Build, Worksharing, Locale, LastSavePath, Error;
        public byte[] Preview; // PNG

        public static RvtInfo Read(string path)
        {
            var info = new RvtInfo { File = path };
            try
            {
                using (var cfb = new CompoundFile(path))
                {
                    var basic = cfb.ReadStream("BasicFileInfo");
                    if (basic == null) { info.Error = "Não parece ser um arquivo do Revit (sem BasicFileInfo)."; return info; }
                    // O texto é UTF-16; decodifica nos dois alinhamentos possíveis por segurança.
                    var text = Encoding.Unicode.GetString(basic) + "\n" + Encoding.Unicode.GetString(basic, 1, basic.Length - 1);
                    info.Version = Match(text, @"Format:\s*(\d{4})") ?? Match(text, @"Autodesk Revit (\d{4})");
                    info.Build = Match(text, @"Build:\s*([^\r\n\0)]+\)?)");
                    info.Worksharing = Match(text, @"Worksharing:\s*([^\r\n\0]+)");
                    info.Locale = Match(text, @"Locale when saved:\s*([^\r\n\0]+)");
                    info.LastSavePath = Match(text, @"Last Save Path:\s*([^\r\n\0]+)");
                    if (info.Version == null) info.Error = "Arquivo do Revit, mas a versão não foi encontrada.";

                    var prev = cfb.ReadStream("RevitPreview4.0");
                    int png = prev == null ? -1 : IndexOf(prev, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                    if (png >= 0) { info.Preview = new byte[prev.Length - png]; Array.Copy(prev, png, info.Preview, 0, info.Preview.Length); }
                }
            }
            catch (Exception ex) { info.Error = ex.Message; }
            return info;
        }

        static string Match(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static int IndexOf(byte[] data, byte[] sig)
        {
            for (int i = 0; i + sig.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < sig.Length && data[i + j] == sig[j]) j++;
                if (j == sig.Length) return i;
            }
            return -1;
        }
    }

    // Leitor mínimo de Compound File Binary (MS-CFB): só o necessário para ler um fluxo pelo nome.
    sealed class CompoundFile : IDisposable
    {
        const uint EndOfChain = 0xFFFFFFFE;
        readonly FileStream fs;
        readonly int sectorSize, miniSectorSize;
        readonly uint miniCutoff;
        readonly uint[] fat, miniFat;
        readonly List<(string name, int type, uint start, long size)> dir = new List<(string, int, uint, long)>();
        byte[] miniStream;

        public CompoundFile(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var h = new byte[512];
            if (fs.Read(h, 0, 512) != 512 || BitConverter.ToUInt64(h, 0) != 0xE11AB1A1E011CFD0)
                throw new InvalidDataException("Não é um arquivo do Revit (formato OLE não reconhecido).");

            sectorSize = 1 << BitConverter.ToUInt16(h, 0x1E);
            miniSectorSize = 1 << BitConverter.ToUInt16(h, 0x20);
            miniCutoff = BitConverter.ToUInt32(h, 0x38);

            // Setores da FAT: 109 no cabeçalho + cadeia DIFAT.
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++) Add(fatSectors, BitConverter.ToUInt32(h, 0x4C + 4 * i));
            uint difat = BitConverter.ToUInt32(h, 0x44);
            for (int n = 0; difat < EndOfChain - 3 && n < 1_000_000; n++)
            {
                var s = Sector(difat);
                for (int i = 0; i < sectorSize / 4 - 1; i++) Add(fatSectors, BitConverter.ToUInt32(s, 4 * i));
                difat = BitConverter.ToUInt32(s, sectorSize - 4);
            }
            fat = new uint[fatSectors.Count * sectorSize / 4];
            for (int i = 0; i < fatSectors.Count; i++) Buffer.BlockCopy(Sector(fatSectors[i]), 0, fat, i * sectorSize, sectorSize);

            var d = Chain(BitConverter.ToUInt32(h, 0x30), -1);
            for (int off = 0; off + 128 <= d.Length; off += 128)
            {
                int nameLen = BitConverter.ToUInt16(d, off + 64);
                string name = nameLen >= 2 ? Encoding.Unicode.GetString(d, off, Math.Min(nameLen, 64) - 2) : "";
                long size = sectorSize == 512 ? BitConverter.ToUInt32(d, off + 120) : BitConverter.ToInt64(d, off + 120);
                dir.Add((name, d[off + 66], BitConverter.ToUInt32(d, off + 116), size));
            }

            uint mf = BitConverter.ToUInt32(h, 0x3C);
            var mfBytes = mf < EndOfChain - 3 ? Chain(mf, -1) : new byte[0];
            miniFat = new uint[mfBytes.Length / 4];
            Buffer.BlockCopy(mfBytes, 0, miniFat, 0, mfBytes.Length);
        }

        static void Add(List<uint> list, uint v) { if (v < EndOfChain - 3) list.Add(v); }

        public byte[] ReadStream(string name)
        {
            var e = dir.Find(x => x.type == 2 && x.name == name);
            if (e.name == null) return null;
            if (e.size >= miniCutoff) return Chain(e.start, e.size);

            if (miniStream == null) { var root = dir.Find(x => x.type == 5); miniStream = Chain(root.start, root.size); }
            var data = new byte[e.size];
            uint s = e.start;
            for (long pos = 0; pos < e.size; pos += miniSectorSize)
            {
                Array.Copy(miniStream, s * miniSectorSize, data, pos, Math.Min(miniSectorSize, e.size - pos));
                s = miniFat[s];
            }
            return data;
        }

        // Segue a cadeia na FAT; size < 0 lê até o fim da cadeia.
        byte[] Chain(uint start, long size)
        {
            var ms = new MemoryStream();
            for (uint s = start, n = 0; s < EndOfChain - 3 && n <= fat.Length; s = fat[s], n++)
            {
                ms.Write(Sector(s), 0, sectorSize);
                if (size >= 0 && ms.Length >= size) break;
            }
            var buf = ms.ToArray();
            if (size >= 0 && buf.Length > size) Array.Resize(ref buf, (int)size);
            return buf;
        }

        byte[] Sector(uint n)
        {
            var b = new byte[sectorSize];
            fs.Position = (long)(n + 1) * sectorSize;
            int read = 0;
            while (read < sectorSize) { int r = fs.Read(b, read, sectorSize - read); if (r == 0) break; read += r; }
            return b;
        }

        public void Dispose() => fs.Dispose();
    }
}
