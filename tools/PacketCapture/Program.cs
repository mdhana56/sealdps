using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

// Capture traffic TCP ke/dari server Seal Online untuk riset format paket damage.
// Deteksi otomatis endpoint dari koneksi aktif proses game (termasuk proxy lokal
// seperti ExitLag yang lewat 127.0.0.1).

string[] candidateProcessNames = { "SO3DPlus", "SO3DPlus_x64" };

if (args.Contains("--login"))
{
    int secs = args.Length > 0 && int.TryParse(args[0], out var s0) ? s0 : 120;
    CaptureLogin(secs, candidateProcessNames);
    return;
}

var proc = candidateProcessNames.SelectMany(Process.GetProcessesByName).FirstOrDefault();
if (proc is null)
{
    Console.WriteLine("Proses Seal Online tidak ditemukan. Jalankan game dulu.");
    return;
}

var (ips, ports) = GetRemoteEndpoints(proc.Id);
// Buang koneksi non-game (HTTP log server, dsb).
for (int i = ips.Count - 1; i >= 0; i--)
{
    if (ports[i] == 80 || ports[i] == 443) { ips.RemoveAt(i); ports.RemoveAt(i); }
}
if (ips.Count == 0)
{
    Console.WriteLine("Tidak ada koneksi TCP aktif dari game. Pastikan sudah login & masuk map.");
    return;
}

bool loopback = ips.All(ip => ip == "127.0.0.1" || ip == "::1");
string filter = "tcp and (" + string.Join(" or ", ips.Zip(ports, (ip, p) => $"(host {ip} and port {p})")) + ")";

var devices = CaptureDeviceList.Instance;
if (devices.Count == 0)
{
    Console.WriteLine("Tidak ada network adapter yang terdeteksi oleh Npcap.");
    return;
}

Console.WriteLine("=== Seal Online Packet Capture ===");
Console.WriteLine($"Proses game: {proc.ProcessName} (PID {proc.Id})");
Console.WriteLine($"Endpoint: {string.Join(", ", ips.Zip(ports, (ip, p) => $"{ip}:{p}"))}");
if (loopback) Console.WriteLine("Terdeteksi proxy lokal (ExitLag/gamebooster) - pakai adapter loopback.");

ILiveDevice? device = loopback
    ? devices.FirstOrDefault(d => (d.Description ?? "").Contains("loopback", StringComparison.OrdinalIgnoreCase))
    : devices.FirstOrDefault(d => (d.Description ?? "").Contains("Wireless") || (d.Description ?? "").Contains("Wi-Fi") || (d.Description ?? "").Contains("Realtek"));
device ??= devices[0];

Console.WriteLine($"\nMemakai adapter: {device.Description}");
Console.WriteLine($"Filter BPF: {filter}");

string outFile = Path.Combine(AppContext.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.pcap");
var writer = new CaptureFileWriterDevice(outFile);

device.Open(DeviceModes.Promiscuous, 1000);
device.Filter = filter;
writer.Open(device);

int count = 0;
device.OnPacketArrival += (s, e) =>
{
    count++;
    writer.Write(e.GetPacket());

    var raw = e.GetPacket();
    var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
    var tcp = packet.Extract<TcpPacket>();
    if (tcp is not null && tcp.PayloadData.Length > 0)
    {
        Console.WriteLine($"[{count}] {tcp.SourcePort} -> {tcp.DestinationPort}  {tcp.PayloadData.Length} bytes  hex[0..16]={Convert.ToHexString(tcp.PayloadData[..Math.Min(16, tcp.PayloadData.Length)])}");
    }
};

int durationSeconds = args.Length > 0 && int.TryParse(args[0], out var seconds) ? seconds : 45;

Console.WriteLine($"\nMenangkap paket... tersimpan ke: {outFile}");
Console.WriteLine($"Sekarang lakukan aksi di game (misal serang monster) supaya traffic damage tertangkap.");
Console.WriteLine($"Capture berjalan otomatis selama {durationSeconds} detik.\n");

device.StartCapture();
System.Threading.Thread.Sleep(durationSeconds * 1000);
device.StopCapture();
device.Close();
writer.Close();

Console.WriteLine($"\nSelesai. Total paket dengan payload: {count}");
Console.WriteLine($"File tersimpan di: {outFile}");

// Mode --login: koneksi login dibuka SETELAH capture mulai (port/server beda), jadi
// nggak bisa pakai filter endpoint di awal. Semua TCP (kecuali 80/443) ditahan di
// MEMORI, port lokal milik proses game dicatat tiap 200ms, dan di akhir HANYA
// paket yang port-nya milik game yang ditulis ke file. Paket app lain dibuang.
static void CaptureLogin(int seconds, string[] processNames)
{
    var devices = CaptureDeviceList.Instance;
    var chosen = devices.Where(d =>
        (d.Description ?? "").Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
        (d.Description ?? "").Contains("Wireless") || (d.Description ?? "").Contains("Wi-Fi") ||
        (d.Description ?? "").Contains("Realtek") || (d.Description ?? "").Contains("Ethernet")).ToList();

    var buffered = new List<(ILiveDevice dev, RawCapture raw)>();
    var gamePorts = new HashSet<int>();
    foreach (var d in chosen)
    {
        d.Open(DeviceModes.None, 1000);
        d.Filter = "tcp and not port 443 and not port 80";
        var dev = d;
        d.OnPacketArrival += (s, e) => { lock (buffered) buffered.Add((dev, e.GetPacket())); };
        d.StartCapture();
        Console.WriteLine($"Adapter: {d.Description}");
    }

    Console.WriteLine($"\nCapture LOGIN berjalan {seconds} detik. Silakan login sekarang.");
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        foreach (var p in processNames.SelectMany(Process.GetProcessesByName))
            foreach (int port in GetLocalPorts(p.Id))
                lock (gamePorts) gamePorts.Add(port);
        System.Threading.Thread.Sleep(200);
    }
    foreach (var d in chosen) { d.StopCapture(); d.Close(); }

    Console.WriteLine($"Port lokal game yang tercatat: {string.Join(", ", gamePorts.OrderBy(x => x))}");
    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    foreach (var grp in buffered.GroupBy(b => b.dev))
    {
        var keep = grp.Where(b =>
        {
            var tcp = Packet.ParsePacket(b.raw.LinkLayerType, b.raw.Data).Extract<TcpPacket>();
            return tcp != null && (gamePorts.Contains(tcp.SourcePort) || gamePorts.Contains(tcp.DestinationPort));
        }).ToList();
        if (keep.Count == 0) continue;
        bool lo = (grp.Key.Description ?? "").Contains("loopback", StringComparison.OrdinalIgnoreCase);
        string outFile = Path.Combine(AppContext.BaseDirectory, $"capture_login_{stamp}_{(lo ? "loopback" : "net")}.pcap");
        var w = new CaptureFileWriterDevice(outFile);
        w.Open(new DeviceConfiguration { LinkLayerType = keep[0].raw.LinkLayerType });
        foreach (var b in keep) w.Write(b.raw);
        w.Close();
        Console.WriteLine($"{keep.Count} paket game ({grp.Count() - keep.Count} paket app lain dibuang) -> {outFile}");
    }
}

// Semua port lokal TCP milik PID (state apa pun), dari netstat -ano.
static IEnumerable<int> GetLocalPorts(int pid)
{
    var psi = new ProcessStartInfo("netstat", "-ano -p TCP") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    foreach (var line in output.Split('\n'))
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5 && parts[0] == "TCP" && parts[^1] == pid.ToString())
        {
            string local = parts[1];
            if (int.TryParse(local[(local.LastIndexOf(':') + 1)..], out int port)) yield return port;
        }
    }
}

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
