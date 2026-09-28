using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace DpsMeterUI;

/// Job ID (dari roster party 041C / entity broadcast CC1B / kolom job_id di
/// skill.edt) -> nama job. Nama diambil dari tabel teks game sendiri (etc.SPAK ->
/// usa.edt baris 408-434), dan ID-nya sama dengan parameter `job=` di halaman
/// ranking resmi PlayRohan. Pola ID: satuan = job dasar (1 Warrior .. 6 Craftsman,
/// 9 Hunter, 31 Cook), puluhan = cabang job ke-2 (13 = Jester cabang 1 = Assassin,
/// 23 = cabang 2 = Gambler). Dicocokkan 2026-09-27 dengan skill yang dipakai tiap
/// karakter (35/35 cocok) + roster (Vanbonang 13, PemainB 16).
///
/// Ikon job di UI = badge bulat warna rumpun job dasar + ikon vektor (JobIcons),
/// singkatan cuma cadangan kalau ikon nggak ada (gaya LOA Logs: ikon class di kiri
/// nama, nama job lengkap di tooltip).
public static class JobNames
{
    public record Info(string Name, string Abbrev, Brush Color);

    static Brush B(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly Brush Neutral = B(0x8B, 0x8B, 0x93);
    static readonly Brush Warrior = B(0xD9, 0x4A, 0x3D);
    static readonly Brush Knight = B(0x3E, 0x7C, 0xE0);
    static readonly Brush Jester = B(0x9B, 0x6B, 0xD9);
    static readonly Brush Mage = B(0x22, 0xA6, 0xC8);
    static readonly Brush Priest = B(0xD4, 0xA0, 0x17);
    static readonly Brush Craftsman = B(0xE0, 0x7A, 0x1F);
    static readonly Brush Hunter = B(0x3F, 0xA8, 0x5C);
    static readonly Brush Cook = B(0xD9, 0x5B, 0x9A);

    static readonly Dictionary<int, Info> ById = new()
    {
        [0] = new("Beginner", "BG", Neutral),
        [7] = new("Game Master", "GM", Neutral),
        [8] = new("Vagabond", "VB", Neutral),
        [1] = new("Warrior", "WR", Warrior), [11] = new("Berserker", "BS", Warrior), [21] = new("Swordmaster", "SM", Warrior),
        [2] = new("Knight", "KN", Knight), [12] = new("Renegade", "RN", Knight), [22] = new("Defender", "DF", Knight),
        [3] = new("Jester", "JS", Jester), [13] = new("Assassin", "AS", Jester), [23] = new("Gambler", "GB", Jester),
        [4] = new("Mage", "MG", Mage), [14] = new("Ice Wizard", "IW", Mage), [24] = new("Fire Wizard", "FW", Mage),
        [5] = new("Priest", "PR", Priest), [15] = new("Templar", "TP", Priest), [25] = new("Apostle", "AP", Priest),
        [6] = new("Craftsman", "CR", Craftsman), [16] = new("Demolitionist", "DM", Craftsman), [26] = new("Artisan", "AR", Craftsman),
        [9] = new("Hunter", "HT", Hunter), [19] = new("Archer", "AC", Hunter), [29] = new("Gunner", "GN", Hunter),
        [31] = new("Cook", "CK", Cook), [131] = new("Chef", "CF", Cook), [231] = new("Food Fighter", "FF", Cook),
    };

    static readonly Dictionary<string, Info> ByName = ById.Values.ToDictionary(i => i.Name);
    static readonly Dictionary<string, int> IdByName = ById.ToDictionary(kv => kv.Value.Name, kv => kv.Key);

    /// Ikon vektor job (lihat JobIcons), null kalau job belum dikenal.
    public static Geometry? IconOf(string? name) =>
        name != null && IdByName.TryGetValue(name, out int id) ? JobIcons.Get(id) : null;

    /// Null kalau ID bukan job yang dikenal (dipakai juga buat validasi field).
    public static string? Get(uint id) => ById.TryGetValue((int)id, out var i) ? i.Name : null;

    public static Info? ByJobName(string? name) => name != null && ByName.TryGetValue(name, out var i) ? i : null;
}
