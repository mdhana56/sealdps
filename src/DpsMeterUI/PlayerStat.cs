using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace DpsMeterUI;

/// Statistik 1 skill (atau "Auto") milik 1 pemain - 1 baris di halaman breakdown.
public class KindStat : INotifyPropertyChanged
{
    // Hit skill yang sama dalam jarak ini dianggap 1 cast (mis. Deathly Slash
    // kena 5 target = 5 hit, 1 cast). Semua hit 1 cast datang barengan (~200ms
    // setelah request cast), sedangkan cooldown skill terpendek 4 detik.
    static readonly TimeSpan SameCastWindow = TimeSpan.FromMilliseconds(500);

    public string Kind { get; }
    public KindStat(string kind) => Kind = kind;

    public double Damage { get; private set; }
    public int Hits { get; private set; }
    public int Casts { get; private set; }
    public double MaxHit { get; private set; }
    public double Dps { get; private set; }
    public double PercentOfPlayer { get; private set; }
    public double BarScale { get; private set; }
    DateTime _lastHitTime;

    public void AddHit(double damage, DateTime time, bool isAuto)
    {
        if (isAuto || time - _lastHitTime > SameCastWindow) Casts++;
        _lastHitTime = time;
        Damage += damage;
        Hits++;
        if (damage > MaxHit) MaxHit = damage;
    }

    /// Gabung statistik skill yang sama dari baris dobel (karakter sama, entity ID beda).
    public void MergeFrom(KindStat other)
    {
        Damage += other.Damage;
        Hits += other.Hits;
        Casts += other.Casts;
        if (other.MaxHit > MaxHit) MaxHit = other.MaxHit;
        if (other._lastHitTime > _lastHitTime) _lastHitTime = other._lastHitTime;
    }

    public void Refresh(double playerDamage, double elapsedSeconds, double topSkillDamage)
    {
        Dps = Damage / elapsedSeconds;
        PercentOfPlayer = playerDamage > 0 ? Damage / playerDamage * 100.0 : 0;
        BarScale = topSkillDamage > 0 ? Damage / topSkillDamage : 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // refresh semua binding
    }

    public string DamageNum => NumberFormat.Split(Damage).Num;
    public string DamageUnit => NumberFormat.Split(Damage).Unit;
    public string DamageFull => NumberFormat.Full(Damage);
    public string DpsNum => NumberFormat.Split(Dps).Num;
    public string DpsUnit => NumberFormat.Split(Dps).Unit;
    public string DpsFull => NumberFormat.Full(Dps);
    public string PercentText => $"{PercentOfPlayer:0.#}";
    public double Aph => Hits > 0 ? Damage / Hits : 0;
    public string AphNum => NumberFormat.Split(Aph).Num;
    public string AphUnit => NumberFormat.Split(Aph).Unit;
    public string AphFull => NumberFormat.Full(Aph);
    public string MaxNum => NumberFormat.Split(MaxHit).Num;
    public string MaxUnit => NumberFormat.Split(MaxHit).Unit;
    public string MaxFull => NumberFormat.Full(MaxHit);

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class PlayerStat : INotifyPropertyChanged
{
    static readonly Brush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
        new SolidColorBrush(Color.FromRgb(0x3E, 0x9C, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A)),
        new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
        new SolidColorBrush(Color.FromRgb(0xB0, 0x7C, 0xE8)),
        new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x93)),
    };

    private string _name;
    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(nameof(Name)); }
    }
    public Brush BarBrush { get; }

    private string? _job;
    /// Nama job (Assassin, Apostle, ...), null kalau belum ke-detect.
    public string? Job
    {
        get => _job;
        set { if (_job == value) return; _job = value; OnChanged(string.Empty); }
    }

    // Badge job (ikon + warna rumpun), disembunyikan kalau job belum ke-detect.
    public Geometry? JobIcon => JobNames.IconOf(_job);
    public string? JobAbbrev => JobIcon == null ? JobNames.ByJobName(_job)?.Abbrev : null;
    public Brush? JobBrush => JobNames.ByJobName(_job)?.Color;
    public bool HasJob => _job != null;

    /// False = baris disembunyikan (pemain di luar party saat tombol "di luar party" OFF).
    public bool IsShown { get; set; } = true;

    readonly Dictionary<string, KindStat> _byKind = new();
    public ObservableCollection<KindStat> Breakdown { get; } = new();

    public PlayerStat(string name, int colorIndex)
    {
        _name = name;
        ColorIndex = colorIndex;
        BarBrush = Palette[colorIndex % Palette.Length];
    }

    /// Dipakai ulang buat statistik per-target, biar warna pemain sama di semua tampilan.
    public int ColorIndex { get; }

    public double Damage { get; private set; }
    public int Hits { get; private set; }
    public double MaxHit { get; private set; }
    public int Casts => Breakdown.Sum(k => k.Casts);

    public DateTime FirstHit { get; set; }
    public DateTime LastHit { get; set; }

    double ElapsedSeconds => Math.Max((LastHit - FirstHit).TotalSeconds, 1.0);
    public double Dps => Damage / ElapsedSeconds;

    /// Lebar bar relatif ke pemain dengan damage tertinggi (0-100).
    public double PercentOfTop { get; set; } = 100;
    public double BarScale => PercentOfTop / 100.0;

    /// D% = porsi damage pemain ini dari total damage semua pemain.
    public double PercentOfTotal { get; set; }
    public string PercentText => $"{PercentOfTotal:0.#}";

    public string DamageNum => NumberFormat.Split(Damage).Num;
    public string DamageUnit => NumberFormat.Split(Damage).Unit;
    public string DamageFull => NumberFormat.Full(Damage);
    public string DpsNum => NumberFormat.Split(Dps).Num;
    public string DpsUnit => NumberFormat.Split(Dps).Unit;
    public string DpsFull => NumberFormat.Full(Dps);
    public double Aph => Hits > 0 ? Damage / Hits : 0;
    public string AphNum => NumberFormat.Split(Aph).Num;
    public string AphUnit => NumberFormat.Split(Aph).Unit;
    public string AphFull => NumberFormat.Full(Aph);
    public string MaxNum => NumberFormat.Split(MaxHit).Num;
    public string MaxUnit => NumberFormat.Split(MaxHit).Unit;
    public string MaxFull => NumberFormat.Full(MaxHit);

    public void AddHit(string kind, double damage, DateTime time)
    {
        if (Hits == 0) FirstHit = time;
        LastHit = time;
        Damage += damage;
        Hits++;
        if (damage > MaxHit) MaxHit = damage;

        if (!_byKind.TryGetValue(kind, out var ks))
        {
            ks = new KindStat(kind);
            _byKind[kind] = ks;
            Breakdown.Add(ks);
        }
        ks.AddHit(damage, time, isAuto: kind == "Auto");
    }

    /// Gabung baris lain milik karakter yang sama ke baris ini. Entity ID karakter
    /// bisa ganti (masuk dungeon, relog), jadi 1 karakter bisa sempat punya >1 baris.
    public void MergeFrom(PlayerStat other)
    {
        if (other.Hits == 0) return;
        if (Hits == 0 || other.FirstHit < FirstHit) FirstHit = other.FirstHit;
        if (Hits == 0 || other.LastHit > LastHit) LastHit = other.LastHit;
        Damage += other.Damage;
        Hits += other.Hits;
        if (other.MaxHit > MaxHit) MaxHit = other.MaxHit;

        foreach (var ok in other.Breakdown)
        {
            if (!_byKind.TryGetValue(ok.Kind, out var ks))
            {
                ks = new KindStat(ok.Kind);
                _byKind[ok.Kind] = ks;
                Breakdown.Add(ks);
            }
            ks.MergeFrom(ok);
        }
    }

    public void Refresh()
    {
        double top = Breakdown.Count > 0 ? Breakdown.Max(k => k.Damage) : 0;
        foreach (var ks in Breakdown)
            ks.Refresh(Damage, ElapsedSeconds, top);

        // urutkan breakdown dari damage terbesar
        var sorted = Breakdown.OrderByDescending(k => k.Damage).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int idx = Breakdown.IndexOf(sorted[i]);
            if (idx != i) Breakdown.Move(idx, i);
        }

        OnChanged(string.Empty); // refresh semua binding
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
