using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DpsMeterUI;

/// Skill ID = field F1 (offset +4 dari magic A4 CE 07 00) di paket damage skill
/// sendiri. Terverifikasi 2026-09-26 (capture_20260926_082507): ID sama tiap
/// cast skill yang sama, dan cocok dengan paket cooldown 0x2236. Lihat
/// docs/skills-log.md. Nama diambil dari tabel skill game (lihat SkillTable);
/// skill yang tetap nggak dikenal tampil sebagai "Skill #ID".
public static class SkillNames
{
    // Cadangan kalau tabel game gagal dibaca (hasil identifikasi manual).
    static Dictionary<uint, string> _names = new()
    {
        // Assassin
        [182] = "Sudden Attack",
        [183] = "Vital Attack",
        [184] = "Soul Breaker",
        [186] = "Disorientation",
        [340] = "Deathly Slash",
    };

    /// Muat nama dari file game. Return pesan status buat ditampilkan di UI.
    public static string LoadFromGame()
    {
        try
        {
            var table = SkillTable.Load(GameDir.Path ?? throw new DirectoryNotFoundException("folder game belum ketemu - pilih lewat menu"));
            foreach (var kv in _names.Where(kv => !table.ContainsKey(kv.Key)))
                table[kv.Key] = kv.Value;
            _names = table;
            return $"{table.Count} nama skill dimuat dari data game";
        }
        catch (Exception ex)
        {
            return $"Tabel skill gagal dibaca ({ex.Message}) - pakai daftar manual";
        }
    }

    public static string Get(uint id) => _names.TryGetValue(id, out var name) ? name : $"Skill #{id}";
}
