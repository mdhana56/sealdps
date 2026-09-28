using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DpsMeterUI;

/// Lokasi folder instalasi Seal Online (yang berisi etc\etc.SPAK). Urutan cari:
/// 1. folder yang pernah dipilih user lewat menu (disimpan di %AppData%\SealDpsMeter)
/// 2. registry uninstall "SealOnline..." (UninstallString / DisplayIcon - InstallLocation
///    kosong di installer resmi)
/// 3. lokasi umum di semua drive
/// Path proses game nggak bisa dipakai: GameGuard memblok OpenProcess.
public static class GameDir
{
    static readonly string SettingsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SealDpsMeter", "gamedir.txt");

    static string? _path;
    static bool _searched;

    /// Folder game, null kalau nggak ketemu (user perlu pilih lewat menu).
    public static string? Path
    {
        get
        {
            if (!_searched) { _path = Find(); _searched = true; }
            return _path;
        }
    }

    /// Path etc.SPAK, atau exception yang jelas kalau folder game belum ketemu.
    public static string EtcSpak =>
        System.IO.Path.Combine(Path ?? throw new DirectoryNotFoundException("folder game belum ketemu - pilih lewat menu"), "etc", "etc.SPAK");

    public static bool IsValid(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(System.IO.Path.Combine(dir, "etc", "etc.SPAK"));

    /// Simpan pilihan user (dipakai lagi di start berikutnya).
    public static bool TrySet(string dir)
    {
        if (!IsValid(dir)) return false;
        _path = dir;
        _searched = true;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, dir);
        }
        catch { }
        return true;
    }

    static string? Find()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string saved = File.ReadAllText(SettingsFile).Trim();
                if (IsValid(saved)) return saved;
            }
        }
        catch { }

        return FromRegistry().Concat(CommonPaths()).FirstOrDefault(IsValid);
    }

    static IEnumerable<string> FromRegistry()
    {
        var found = new List<string>();
        foreach (var (hive, key) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(key);
                if (root is null) continue;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using var app = root.OpenSubKey(sub);
                    string name = app?.GetValue("DisplayName") as string ?? sub;
                    if (!name.Contains("Seal", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string field in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
                    {
                        string? v = (app?.GetValue(field) as string)?.Trim().Trim('"');
                        if (string.IsNullOrEmpty(v)) continue;
                        // DisplayIcon bisa berbentuk "path.exe,0".
                        int comma = v.IndexOf(".exe,", StringComparison.OrdinalIgnoreCase);
                        if (comma >= 0) v = v[..(comma + 4)];
                        found.Add(v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetDirectoryName(v)! : v);
                    }
                }
            }
            catch { }
        }
        return found;
    }

    static IEnumerable<string> CommonPaths()
    {
        string[] subs =
        {
            @"Program Files (x86)\SealOnline", @"Program Files\SealOnline", @"SealOnline", @"Games\SealOnline",
            @"Program Files (x86)\Steam\steamapps\common\SealOnline", @"SteamLibrary\steamapps\common\SealOnline",
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (string sub in subs)
                yield return System.IO.Path.Combine(drive.RootDirectory.FullName, sub);
    }
}
