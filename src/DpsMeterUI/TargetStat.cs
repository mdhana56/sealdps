using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DpsMeterUI;

/// Damage total (semua pemain yang ke-track) ke 1 monster/target - 1 baris di tab TARGET.
public class TargetStat : INotifyPropertyChanged
{
    public uint EntityId { get; }
    public TargetStat(uint entityId) => EntityId = entityId;

    /// Nama monster dari data game; null kalau jenisnya belum ke-detect (paket
    /// entity list CC35 belum lewat sejak meter nyala).
    public string? MonsterName { get; set; }
    public string Name => EntityId == 0 ? "Target ?" : MonsterName ?? $"Target #{EntityId}";
    // Pembeda kalau ada beberapa monster dengan nama sama (kalau nama belum
    // ketahuan, ID-nya sudah ada di Name).
    public string IdText => EntityId == 0 || MonsterName is null ? "" : $"#{EntityId}";

    public double Damage { get; private set; }
    public int Hits { get; private set; }
    /// HP penuh perkiraan = max(sisa HP + damage) yang pernah kelihatan.
    public double MaxHp { get; private set; }
    public DateTime FirstHit { get; private set; }
    public DateTime LastHit { get; private set; }

    public double PercentOfTotal { get; set; }
    public double BarScale { get; set; }

    public void AddHit(double damage, double hpAfter, DateTime time)
    {
        if (Hits == 0) FirstHit = time;
        LastHit = time;
        Hits++;
        if (hpAfter > 0 && hpAfter + damage > MaxHp) MaxHp = hpAfter + damage;
    }

    /// Damage = jumlah damage pemain yang lagi ditampilkan (tombol "di luar party"
    /// OFF -> cuma party). Target tanpa damage yang tampil ikut disembunyikan.
    public void ApplyFilter()
    {
        double sum = 0;
        foreach (var kv in _playerByGlobal)
        {
            kv.Value.IsShown = kv.Key.IsShown;
            if (kv.Value.IsShown) sum += kv.Value.Damage;
        }
        Damage = sum;
        IsShown = sum > 0;
    }

    public bool IsShown { get; private set; } = true;

    public double Dps => Damage / Math.Max((LastHit - FirstHit).TotalSeconds, 1.0);

    public string DamageNum => NumberFormat.Split(Damage).Num;
    public string DamageUnit => NumberFormat.Split(Damage).Unit;
    public string DamageFull => NumberFormat.Full(Damage);
    public string DpsNum => NumberFormat.Split(Dps).Num;
    public string DpsUnit => NumberFormat.Split(Dps).Unit;
    public string DpsFull => NumberFormat.Full(Dps);
    public string PercentText => $"{PercentOfTotal:0.#}";
    public string HpNum => MaxHp > 0 ? NumberFormat.Split(MaxHp).Num : "-";
    public string HpUnit => MaxHp > 0 ? NumberFormat.Split(MaxHp).Unit : "";
    public string HpFull => MaxHp > 0 ? NumberFormat.Full(MaxHp) : "HP belum diketahui";

    /// Statistik per pemain (+ breakdown skill) KHUSUS ke target ini - dipakai saat
    /// monster diklik di tab TARGET (tampilan DPS yang sama, tapi cuma damage ke sini).
    public ObservableCollection<PlayerStat> Players { get; } = new();
    // Kunci = baris pemain global (bukan entity ID, karena ID 1 karakter bisa ganti).
    readonly Dictionary<PlayerStat, PlayerStat> _playerByGlobal = new();

    public PlayerStat PlayerFor(PlayerStat global)
    {
        if (!_playerByGlobal.TryGetValue(global, out var p))
        {
            p = new PlayerStat(global.Name, global.ColorIndex);
            _playerByGlobal[global] = p;
            Players.Add(p);
        }
        return p;
    }

    /// Baris global -> baris khusus target ini, buat sinkron nama.
    public IEnumerable<KeyValuePair<PlayerStat, PlayerStat>> PlayersByGlobal => _playerByGlobal;

    /// Baris global `from` digabung ke `into` -> gabung juga baris per-target-nya.
    public void MergePlayer(PlayerStat from, PlayerStat into)
    {
        if (!_playerByGlobal.Remove(from, out var src)) return;
        Players.Remove(src);
        PlayerFor(into).MergeFrom(src);
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;
}
