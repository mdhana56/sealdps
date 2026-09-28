using System;

namespace DpsMeterUI;

/// Format angka ringkas ala LOA Logs: 104254112 -> "104.3m", 12500 -> "12.5k".
public static class NumberFormat
{
    public static string Abbrev(double n)
    {
        if (n >= 1e12) return $"{n / 1e12:0.##}t";
        if (n >= 1e9) return $"{n / 1e9:0.##}b";
        if (n >= 1e6) return $"{n / 1e6:0.#}m";
        if (n >= 1e3) return $"{n / 1e3:0.#}k";
        return n.ToString("0");
    }

    /// Angka dan satuan dipisah, biar satuannya bisa ditampilkan lebih kecil &
    /// abu-abu kayak LOA: ("104.3", "m").
    public static (string Num, string Unit) Split(double n)
    {
        if (n >= 1e12) return ($"{n / 1e12:0.##}", "t");
        if (n >= 1e9) return ($"{n / 1e9:0.##}", "b");
        if (n >= 1e6) return ($"{n / 1e6:0.#}", "m");
        if (n >= 1e3) return ($"{n / 1e3:0.#}", "k");
        return (n.ToString("0"), "");
    }

    public static string Full(double n) => Math.Round(n).ToString("N0");

    public static string Duration(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
}
