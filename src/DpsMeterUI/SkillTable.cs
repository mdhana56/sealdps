using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DpsMeterUI;

/// Baca tabel nama skill langsung dari file data game (offline, cuma baca file
/// di disk - nggak nyentuh proses game). Format dari project open-source
/// "unsealed" (github.com/feryandi/unsealed):
///
/// - `.spak` = zip biasa yang dienkripsi ZipCrypto. Password-nya diturunkan dari
///   versi di komentar zip ("Seal Online Zip v&lt;V&gt;"):
///     buf = ("Pass" + (99999999 - V)) di-pad null sampai 260 byte
///     password = Templates[V % 4] diformat dengan crc32(buf)
/// - `.edt` = XOR stream cipher berbasis LCG (seed 0x11CFD).
/// - `skill.edt` (di etc.SPAK) = "Seal Online Data v13": header 64 byte,
///   int32 count, int32 field_count, lalu `count` baris lebar tetap. Tiap baris:
///   int32 skill ID di offset 0, nama (null-terminated) di offset 4. Satu baris
///   per (skill, level), jadi ID yang sama muncul berulang.
///
/// Skill ID di sini = field F1 di paket damage skill (terverifikasi 2026-09-26:
/// 184 = Soul Breaker, 340 = Deathly Slash, 182 = Sudden Attack).
public static class SkillTable
{
    static readonly string[] PasswordTemplates = { "#!#0{0:x}&&!!", "^^&!@{0:X}&&*", "#$#$&*{0:x}!!@", "!@####{0:x}*@#@" };
    static readonly uint[] Crc = BuildCrcTable();

    public static Dictionary<uint, string> Load(string gameDir)
    {
        string spak = Path.Combine(gameDir, "etc", "etc.SPAK");
        byte[] data = EdtDecode(ReadSpakEntry(spak, "skill.edt"));

        int count = BitConverter.ToInt32(data, 64);
        const int headerLen = 72;
        if (count <= 0 || (data.Length - headerLen) % count != 0)
            throw new InvalidDataException($"skill.edt: ukuran baris tidak rata (count={count}, len={data.Length})");
        int width = (data.Length - headerLen) / count;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var euckr = Encoding.GetEncoding(949);

        var names = new Dictionary<uint, string>();
        for (int r = 0; r < count; r++)
        {
            int row = headerLen + r * width;
            uint id = BitConverter.ToUInt32(data, row);
            if (id == 0 || names.ContainsKey(id)) continue;

            int nameStart = row + 4;
            int nameEnd = nameStart;
            while (nameEnd < row + width && data[nameEnd] != 0) nameEnd++;
            string name = euckr.GetString(data, nameStart, nameEnd - nameStart).Trim();
            if (name.Length > 0) names[id] = name;
        }
        return names;
    }

    internal static byte[] ReadSpakEntry(string spakPath, string entryName)
    {
        byte[] zip = File.ReadAllBytes(spakPath);

        // End of central directory (scan mundur, komentar zip ada setelahnya).
        int eocd = -1;
        for (int i = zip.Length - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(zip, i) == 0x06054B50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("SPAK: EOCD tidak ketemu");

        int commentLen = BitConverter.ToUInt16(zip, eocd + 20);
        string comment = Encoding.ASCII.GetString(zip, eocd + 22, commentLen);
        var m = Regex.Match(comment, @"Seal Online Zip v(\d+)");
        if (!m.Success) throw new InvalidDataException($"SPAK: versi tidak ada di komentar zip ({comment})");
        byte[] password = SpakPassword(int.Parse(m.Groups[1].Value));

        int cdOffset = (int)BitConverter.ToUInt32(zip, eocd + 16);
        int entries = BitConverter.ToUInt16(zip, eocd + 10);
        int p = cdOffset;
        for (int e = 0; e < entries; e++)
        {
            ushort flags = BitConverter.ToUInt16(zip, p + 8);
            ushort method = BitConverter.ToUInt16(zip, p + 10);
            uint crc = BitConverter.ToUInt32(zip, p + 16);
            int compSize = (int)BitConverter.ToUInt32(zip, p + 20);
            int nameLen = BitConverter.ToUInt16(zip, p + 28);
            int extraLen = BitConverter.ToUInt16(zip, p + 30);
            int cmtLen = BitConverter.ToUInt16(zip, p + 32);
            int localOffset = (int)BitConverter.ToUInt32(zip, p + 42);
            string name = Encoding.ASCII.GetString(zip, p + 46, nameLen);
            p += 46 + nameLen + extraLen + cmtLen;

            if (!name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) continue;

            // Offset data asli pakai panjang nama/extra dari local header.
            int dataStart = localOffset + 30 + BitConverter.ToUInt16(zip, localOffset + 26) + BitConverter.ToUInt16(zip, localOffset + 28);
            byte[] raw = new byte[compSize];
            Array.Copy(zip, dataStart, raw, 0, compSize);

            if ((flags & 1) != 0)
            {
                ZipCryptoDecrypt(password, raw);
                raw = raw.AsSpan(12).ToArray(); // buang header enkripsi 12 byte
            }

            byte[] output;
            if (method == 8)
            {
                using var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                ds.CopyTo(ms);
                output = ms.ToArray();
            }
            else if (method == 0) output = raw;
            else throw new InvalidDataException($"SPAK: kompresi {method} tidak didukung");

            if (Crc32(output) != crc) throw new InvalidDataException($"SPAK: CRC {entryName} tidak cocok (password salah?)");
            return output;
        }
        throw new FileNotFoundException($"{entryName} tidak ada di {spakPath}");
    }

    static byte[] SpakPassword(int version)
    {
        byte[] buf = new byte[260];
        Encoding.ASCII.GetBytes("Pass" + (99999999 - version)).CopyTo(buf, 0);
        return Encoding.ASCII.GetBytes(string.Format(PasswordTemplates[version % 4], Crc32(buf)));
    }

    static void ZipCryptoDecrypt(byte[] password, byte[] data)
    {
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte b)
        {
            k0 = (k0 >> 8) ^ Crc[(k0 ^ b) & 0xFF];
            k1 = (k1 + (k0 & 0xFF)) * 134775813 + 1;
            k2 = (k2 >> 8) ^ Crc[(k2 ^ (k1 >> 24)) & 0xFF];
        }
        foreach (byte b in password) Update(b);
        for (int i = 0; i < data.Length; i++)
        {
            uint k = k2 | 2;
            byte plain = (byte)(data[i] ^ (byte)((k * (k ^ 1)) >> 8));
            Update(plain);
            data[i] = plain;
        }
    }

    internal static byte[] EdtDecode(byte[] data)
    {
        byte[] output = new byte[data.Length];
        int seed = 0x11CFD;
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            output[i] = (byte)(c ^ ((seed >> 8) & 0xFF));
            seed = ((seed + (sbyte)c) * 52845 + 22719) & 0xFFFF;
        }
        return output;
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = (c >> 8) ^ Crc[(c ^ b) & 0xFF];
        return ~c;
    }

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
