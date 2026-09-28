using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using PacketDotNet;
using SharpPcap;

// Seal Online DPS Meter (v0) - skill damage saja (auto-attack sementara dimatikan).
// Auto-attack: opcode 0xCC5D, damage di offset 12 (raw uint32, tanpa skala).
// Skill    : opcode 0xCEA4, sering "menumpang" di dalam paket lain (mis. 0xCC56),
//            jadi dideteksi dengan mencari magic bytes A4 CE 07 00 di mana pun
//            posisinya dalam payload. Damage ada 12 byte setelah awal magic itu.
//
// Field offset+8 SEMULA dikira skill ID, tapi terbukti (lewat cross-check 3 capture)
// itu cuma sequence counter global (naik terus tiap event, bukan identitas skill).
// Field offset+28 TERKONFIRMASI attacker token: konstan per karakter walau ganti
// skill (2628301726 = Vanbonang di 2 skill beda), beda di karakter lain
// (660993 = PemainB). Dipakai buat breakdown "siapa damage terbesar".

const bool ENABLE_AUTO_ATTACK = true;
const ushort OPCODE_AUTO_ATTACK_SELF = 0xCC5D; // auto-attack milik sendiri (bawa token panjang)
const int AUTO_ATTACK_DAMAGE_OFFSET = 12;
const int AUTO_ATTACK_ATTACKER_TOKEN_OFFSET = 32; // sama pola relatif dengan skill (opcode_start+28)

// Auto-attack milik ORANG LAIN dikirim lewat opcode CC5E berdiri sendiri (bukan
// nempel di CC5D), dan cuma bawa entity ID lokal pendek (bukan token panjang).
const ushort OPCODE_AUTO_ATTACK_OTHER = 0xCC5E;
const int OTHER_AUTO_ATTACK_ENTITY_OFFSET = 8;
const int OTHER_AUTO_ATTACK_DAMAGE_OFFSET = 32;

byte[] SKILL_MAGIC = { 0xA4, 0xCE, 0x07, 0x00 };
const int SKILL_DAMAGE_OFFSET_FROM_MAGIC = 12;
const int ATTACKER_TOKEN_OFFSET_FROM_MAGIC = 28;

// Skill milik ORANG LAIN dikirim lewat opcode CEA5 (beda 1 angka dari CEA4 milik
// sendiri), berdiri sendiri, bawa entity ID lokal pendek (sama seperti auto-attack).
const ushort OPCODE_SKILL_OTHER = 0xCEA5;
const int OTHER_SKILL_ENTITY_OFFSET = 12;
const int OTHER_SKILL_DAMAGE_OFFSET = 20;

// Token attacker (baik yang panjang/self maupun pendek/orang lain) berubah tiap
// sesi login/map, jadi nggak worth di-hardcode ke nama. Sebagai gantinya, tiap
// token unik yang pertama kali muncul di sesi ini otomatis dikasih label
// "Player 1", "Player 2", dst sesuai urutan kemunculan.
Dictionary<uint, string> attackerLabels = new();
int nextPlayerNumber = 1;

Dictionary<string, (int hits, double damage)> perAttackerStats = new();

string[] candidateProcessNames = { "SO3DPlus", "SO3DPlus_x64" };

var proc = candidateProcessNames
    .SelectMany(Process.GetProcessesByName)
    .FirstOrDefault();

if (proc is null)
{
    Console.WriteLine("Proses Seal Online tidak ditemukan. Jalankan game dulu.");
    return;
}

var (serverIps, remotePorts) = GetRemoteEndpoints(proc.Id);
// Buang koneksi non-game (HTTP log server, dsb).
for (int i = serverIps.Count - 1; i >= 0; i--)
{
    if (remotePorts[i] == 80 || remotePorts[i] == 443) { serverIps.RemoveAt(i); remotePorts.RemoveAt(i); }
}
if (serverIps.Count == 0)
{
    Console.WriteLine("Tidak ada koneksi TCP aktif dari game ke server. Pastikan sudah login & masuk map.");
    return;
}

bool loopback = serverIps.All(ip => ip == "127.0.0.1" || ip == "::1");

Console.WriteLine($"Proses game: {proc.ProcessName} (PID {proc.Id})");
Console.WriteLine($"Endpoint terdeteksi: {string.Join(", ", serverIps.Zip(remotePorts, (ip, p) => $"{ip}:{p}"))}");
if (loopback)
    Console.WriteLine("Terdeteksi proxy lokal (ExitLag/gamebooster) - capture akan pakai adapter loopback.");

var devices = CaptureDeviceList.Instance;
ILiveDevice? device;
if (loopback)
{
    device = devices.FirstOrDefault(d => (d.Description ?? "").Contains("loopback", StringComparison.OrdinalIgnoreCase));
}
else
{
    device = devices.FirstOrDefault(d =>
        (d.Description ?? "").Contains("Wireless") ||
        (d.Description ?? "").Contains("Wi-Fi") ||
        (d.Description ?? "").Contains("Realtek"));
}
device ??= devices.FirstOrDefault();

if (device is null)
{
    Console.WriteLine("Tidak ada network adapter terdeteksi.");
    return;
}

string filter = "tcp and (" + string.Join(" or ", serverIps.Zip(remotePorts, (ip, p) => $"(host {ip} and port {p})")) + ")";
Console.WriteLine($"Adapter: {device.Description}");
Console.WriteLine($"Filter : {filter}\n");

double totalDamage = 0;
int hitCount = 0;
DateTime? firstHit = null;
DateTime lastHit = DateTime.MinValue;

// Anti-duplikat: server kadang broadcast 1 event combat lewat lebih dari satu
// jalur (mis. "combat log diri sendiri" + "broadcast umum"), jadi kita bisa
// nangkep hit yang sama 2x dengan attacker id beda. Kalau ada kombinasi
// (kind, damage) yang sama persis muncul lagi dalam <150ms, anggap duplikat.
var recentHits = new List<(string kind, double damage, DateTime time)>();
bool IsDuplicate(string kind, double damage)
{
    var now = DateTime.Now;
    recentHits.RemoveAll(h => (now - h.time).TotalMilliseconds > 150);
    if (recentHits.Any(h => h.kind == kind && Math.Abs(h.damage - damage) < 0.5))
        return true;
    recentHits.Add((kind, damage, now));
    return false;
}

device.Open(DeviceModes.Promiscuous, 1000);
device.Filter = filter;

void RegisterHit(string kind, double damage, uint attackerToken)
{
    if (damage <= 0 || damage > 1_000_000_000) return; // filter noise
    if (IsDuplicate(kind, damage)) return;

    totalDamage += damage;
    hitCount++;
    firstHit ??= DateTime.Now;
    lastHit = DateTime.Now;

    double elapsed = Math.Max((lastHit - firstHit.Value).TotalSeconds, 1.0);
    double dps = totalDamage / elapsed;

    if (!attackerLabels.TryGetValue(attackerToken, out var attackerName))
    {
        attackerName = $"Player {nextPlayerNumber++}";
        attackerLabels[attackerToken] = attackerName;
    }
    var stat = perAttackerStats.GetValueOrDefault(attackerName, (0, 0));
    perAttackerStats[attackerName] = (stat.hits + 1, stat.damage + damage);

    Console.WriteLine($"[{attackerName,-14} {hitCount,3}] {kind,-8} dmg={damage,12:N0}   total={totalDamage,15:N0}   DPS={dps,12:N1}");
}

device.OnPacketArrival += (s, e) =>
{
    var raw = e.GetPacket();
    var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
    var tcp = packet.Extract<TcpPacket>();
    if (tcp is null) return;

    var payload = tcp.PayloadData;

    if (ENABLE_AUTO_ATTACK && payload.Length >= 6)
    {
        ushort opcode = BitConverter.ToUInt16(payload, 4);
        if (opcode == OPCODE_AUTO_ATTACK_SELF && payload.Length >= AUTO_ATTACK_DAMAGE_OFFSET + 4)
        {
            uint raw32 = BitConverter.ToUInt32(payload, AUTO_ATTACK_DAMAGE_OFFSET);
            uint attackerToken = payload.Length >= AUTO_ATTACK_ATTACKER_TOKEN_OFFSET + 4
                ? BitConverter.ToUInt32(payload, AUTO_ATTACK_ATTACKER_TOKEN_OFFSET)
                : 0;
            RegisterHit("Auto", raw32, attackerToken);
        }
        else if (opcode == OPCODE_AUTO_ATTACK_OTHER && payload.Length >= OTHER_AUTO_ATTACK_DAMAGE_OFFSET + 4)
        {
            // Field ini kemungkinan besar "slot target-lock", bukan ID karakter
            // permanen - akurat kalau attacker tetap kunci ke 1 target yang sama,
            // tapi bisa keliru kalau attacker sering ganti target dengan cepat
            // (mis. farming solo banyak monster). Lihat docs/skills-log.md.
            uint entityId = BitConverter.ToUInt32(payload, OTHER_AUTO_ATTACK_ENTITY_OFFSET);
            uint raw32 = BitConverter.ToUInt32(payload, OTHER_AUTO_ATTACK_DAMAGE_OFFSET);
            RegisterHit("Auto", raw32, entityId);
        }
    }

    // Skill milik sendiri: cari magic bytes di mana pun posisinya (paket suka ter-bundle).
    for (int i = 0; i + 4 <= payload.Length; i++)
    {
        if (payload[i] == SKILL_MAGIC[0] && payload[i + 1] == SKILL_MAGIC[1] &&
            payload[i + 2] == SKILL_MAGIC[2] && payload[i + 3] == SKILL_MAGIC[3])
        {
            int dmgOffset = i + SKILL_DAMAGE_OFFSET_FROM_MAGIC;
            int attackerOffset = i + ATTACKER_TOKEN_OFFSET_FROM_MAGIC;
            if (dmgOffset + 4 <= payload.Length)
            {
                uint raw32 = BitConverter.ToUInt32(payload, dmgOffset);
                uint attackerToken = attackerOffset + 4 <= payload.Length ? BitConverter.ToUInt32(payload, attackerOffset) : 0;
                RegisterHit("Skill", raw32, attackerToken);
            }
        }
    }

    // Skill milik orang lain: opcode CEA5 berdiri sendiri (sama pola dengan CC5E).
    if (payload.Length >= 6)
    {
        ushort opcode = BitConverter.ToUInt16(payload, 4);
        if (opcode == OPCODE_SKILL_OTHER && payload.Length >= OTHER_SKILL_DAMAGE_OFFSET + 4)
        {
            uint entityId = BitConverter.ToUInt32(payload, OTHER_SKILL_ENTITY_OFFSET);
            uint raw32 = BitConverter.ToUInt32(payload, OTHER_SKILL_DAMAGE_OFFSET);
            RegisterHit("Skill", raw32, entityId);
        }
    }
};

Console.WriteLine("Menunggu skill... (Ctrl+C untuk berhenti)\n");
device.StartCapture();

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    device.StopCapture();
    device.Close();
    Console.WriteLine("\n=== Ringkasan ===");
    Console.WriteLine($"Total hit    : {hitCount}");
    Console.WriteLine($"Total damage : {totalDamage:N0}");
    if (firstHit.HasValue)
    {
        double elapsed = Math.Max((lastHit - firstHit.Value).TotalSeconds, 0.001);
        Console.WriteLine($"Durasi       : {elapsed:N1} s");
        Console.WriteLine($"DPS rata-rata: {(totalDamage / elapsed):N1}");
    }

    if (perAttackerStats.Count > 0)
    {
        Console.WriteLine("\n--- Breakdown per karakter (siapa damage terbesar) ---");
        foreach (var kv in perAttackerStats.OrderByDescending(x => x.Value.damage))
        {
            double pct = totalDamage > 0 ? kv.Value.damage / totalDamage * 100 : 0;
            Console.WriteLine($"{kv.Key,-14} hits={kv.Value.hits,3}  damage={kv.Value.damage,15:N0}  ({pct,5:N1}%)");
        }
    }

    Environment.Exit(0);
};

while (true) System.Threading.Thread.Sleep(1000);

static (List<string> ips, List<int> ports) GetRemoteEndpoints(int pid)
{
    var ips = new List<string>();
    var ports = new List<int>();
    try
    {
        var psi = new ProcessStartInfo("netstat", "-ano")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[0] == "TCP" && parts[3] == "ESTABLISHED" && parts[^1] == pid.ToString())
            {
                var remote = parts[2];
                int idx = remote.LastIndexOf(':');
                if (idx > 0)
                {
                    string ip = remote[..idx];
                    if (int.TryParse(remote[(idx + 1)..], out int port) && ip != "0.0.0.0")
                    {
                        ips.Add(ip);
                        ports.Add(port);
                    }
                }
            }
        }
    }
    catch { }
    return (ips, ports);
}
