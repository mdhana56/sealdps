using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DpsMeterUI;

/// Nama item per ID, dibaca dari etc.SPAK -> ItemString.edt ("Seal Online Data v13",
/// di-XOR kayak skill.edt): header 64 byte + int32 count + int32 field_count, lalu
/// baris lebar tetap (776 byte) = int32 ID + nama[260] + deskripsi[512]. Nama English
/// kebanyakan ASCII, sebagian item cuma punya nama Korea (UTF-8 / CP949) - sama dengan
/// tools/export_items.py. ID item-nya dipakai di slot equip paket CC1B.
public static class ItemNames
{
    static Dictionary<uint, string> _names = new();

    public static string LoadFromGame()
    {
        try
        {
            string spak = GameDir.EtcSpak;
            byte[] d = SkillTable.EdtDecode(SkillTable.ReadSpakEntry(spak, "ItemString.edt"));

            const int headerLen = 72;
            int count = BitConverter.ToInt32(d, 64);
            if (count <= 0 || (d.Length - headerLen) % count != 0)
                throw new InvalidDataException($"ItemString.edt: ukuran baris tidak rata (count={count})");
            int width = (d.Length - headerLen) / count;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var cp949 = Encoding.GetEncoding(949);

            var names = new Dictionary<uint, string>();
            for (int r = 0; r < count; r++)
            {
                int row = headerLen + r * width;
                uint id = BitConverter.ToUInt32(d, row);
                int start = row + 4, end = start;
                while (end < row + 4 + 260 && d[end] != 0) end++;
                if (id == 0 || end == start) continue;
                string name;
                try { name = utf8.GetString(d, start, end - start); }
                catch (DecoderFallbackException) { name = cp949.GetString(d, start, end - start); }
                names.TryAdd(id, name.Trim());
            }
            _names = names;
            return $"{names.Count} nama item dimuat";
        }
        catch (Exception ex)
        {
            return $"Nama item gagal dibaca ({ex.Message})";
        }
    }

    public static string Get(uint id) => _names.TryGetValue(id, out var n) ? n : $"Item #{id}";
}
