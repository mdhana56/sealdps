using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DpsMeterUI;

/// Nama monster per ID jenis, dibaca dari etc.SPAK -> monster_us.nam (English).
/// Format "SealOnline String Data v1" (TIDAK di-XOR kayak .edt): header 52 byte
/// (jumlah record sebagai teks ASCII di offset 32), lalu record lebar tetap
/// 210 byte = ID jenis (teks ASCII, 10 byte) + nama (200 byte, null-terminated).
/// Contoh: 2237 = "[Look A Like]Giant R. Rabbit" (dummy).
///
/// ID jenis ini didapat dari paket entity list CC35 (lihat CaptureEngine).
public static class MonsterNames
{
    static Dictionary<uint, string> _names = new();

    public static string LoadFromGame()
    {
        try
        {
            string spak = GameDir.EtcSpak;
            byte[] d = SkillTable.ReadSpakEntry(spak, "monster_us.nam");

            const int headerLen = 52;
            int count = int.Parse(AsciiField(d, 32, 10));
            int rec = (d.Length - headerLen) / count;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var enc = Encoding.GetEncoding(949);

            var names = new Dictionary<uint, string>();
            for (int i = 0; i < count; i++)
            {
                int b = headerLen + i * rec;
                if (!uint.TryParse(AsciiField(d, b, 10), out uint id)) continue;
                int end = b + 10;
                while (end < b + rec && d[end] != 0) end++;
                string name = enc.GetString(d, b + 10, end - (b + 10)).Trim();
                if (name.Length > 0) names.TryAdd(id, name);
            }
            _names = names;
            return $"{names.Count} nama monster dimuat";
        }
        catch (Exception ex)
        {
            return $"Nama monster gagal dibaca ({ex.Message})";
        }
    }

    static string AsciiField(byte[] d, int offset, int len)
    {
        int end = offset;
        while (end < offset + len && d[end] != 0) end++;
        return Encoding.ASCII.GetString(d, offset, end - offset).Trim();
    }

    public static string? Get(uint typeId) => _names.TryGetValue(typeId, out var n) ? n : null;
}
