using System;
using System.Collections.Generic;
using System.IO;

namespace DpsMeterUI;

public record DropItem(string Group, uint ItemId, string Name);

/// Daftar item yang bisa di-drop tiap jenis monster, dari file game di etc.SPAK
/// (semua "Seal Online Data v13", di-XOR kayak skill.edt: header 64 byte + int32
/// count + int32 field_count, lalu baris lebar tetap):
/// - monster.edt: 36 field int64 per monster. Field 0 = ID jenis (sama dengan ID di
///   monster_us.nam / paket CC35), field 14 = baris drop biasa, field 15 = baris
///   drop khusus (boss/event). Field 14 cocok 7542/7563 dengan kolom loot_id
///   monster.dat versi lama (nama kolom dari project "unsealed").
/// - drop_1/2/3.edt: 1 tabel 72 kolom yang dipecah jadi 3 file (24 int32 masing-
///   masing, jumlah baris sama). Isinya ID item saja, 0 = kosong.
/// - drop_4.edt: drop khusus, 24 int32 ID item per baris.
/// Peluang drop TIDAK ada di client (diundi server).
public static class MonsterDrops
{
    const int HeaderLen = 72;
    const int FieldDropRow = 14;
    const int FieldSpecialRow = 15;

    static Dictionary<uint, (int Normal, int Special)> _rowsByType = new();
    static int[][] _normal = Array.Empty<int[]>();
    static int[][] _special = Array.Empty<int[]>();

    public static string LoadFromGame()
    {
        try
        {
            string spak = GameDir.EtcSpak;
            int[][] Table(string name) => ReadIntTable(SkillTable.EdtDecode(SkillTable.ReadSpakEntry(spak, name)), name);

            var parts = new[] { Table("drop_1.edt"), Table("drop_2.edt"), Table("drop_3.edt") };
            if (parts[1].Length != parts[0].Length || parts[2].Length != parts[0].Length)
                throw new InvalidDataException("jumlah baris drop_1/2/3 beda");
            var normal = new int[parts[0].Length][];
            for (int r = 0; r < normal.Length; r++)
                normal[r] = [.. parts[0][r], .. parts[1][r], .. parts[2][r]];
            var special = Table("drop_4.edt");

            byte[] m = SkillTable.EdtDecode(SkillTable.ReadSpakEntry(spak, "monster.edt"));
            int count = BitConverter.ToInt32(m, 64);
            if (count <= 0 || (m.Length - HeaderLen) % count != 0 || (m.Length - HeaderLen) / count != 36 * 8)
                throw new InvalidDataException($"monster.edt: ukuran baris tidak terduga (count={count})");
            var rows = new Dictionary<uint, (int, int)>();
            for (int r = 0; r < count; r++)
            {
                int row = HeaderLen + r * 36 * 8;
                uint id = (uint)BitConverter.ToInt64(m, row);
                rows[id] = ((int)BitConverter.ToInt64(m, row + FieldDropRow * 8),
                            (int)BitConverter.ToInt64(m, row + FieldSpecialRow * 8));
            }

            _normal = normal;
            _special = special;
            _rowsByType = rows;
            return $"Drop list {rows.Count} monster dimuat";
        }
        catch (Exception ex)
        {
            return $"Drop list gagal dibaca ({ex.Message})";
        }
    }

    static int[][] ReadIntTable(byte[] d, string name)
    {
        int count = BitConverter.ToInt32(d, 64);
        if (count <= 0 || (d.Length - HeaderLen) % (count * 4) != 0)
            throw new InvalidDataException($"{name}: ukuran baris tidak rata (count={count})");
        int cells = (d.Length - HeaderLen) / count / 4;
        var rows = new int[count][];
        for (int r = 0; r < count; r++)
        {
            rows[r] = new int[cells];
            Buffer.BlockCopy(d, HeaderLen + r * cells * 4, rows[r], 0, cells * 4);
        }
        return rows;
    }

    /// Item drop jenis monster ini (drop khusus dulu, lalu drop biasa; tanpa duplikat).
    /// null kalau jenisnya tidak ada di data game.
    public static IReadOnlyList<DropItem>? Of(uint typeId)
    {
        if (!_rowsByType.TryGetValue(typeId, out var r)) return null;
        var list = new List<DropItem>();
        var seen = new HashSet<int>();
        void Add(int[][] table, int row, string group)
        {
            if (row <= 0 || row >= table.Length) return;
            foreach (int id in table[row])
                if (id > 0 && seen.Add(id))
                    list.Add(new DropItem(group, (uint)id, ItemNames.Get((uint)id)));
        }
        Add(_special, r.Special, "Khusus");
        Add(_normal, r.Normal, "Biasa");
        return list;
    }
}
