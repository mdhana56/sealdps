using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PacketDotNet;
using SharpPcap.LibPcap;

// Analisa file .pcap hasil PacketCapture: kelompokkan paket server->client
// berdasarkan opcode (2 byte setelah length prefix) untuk membantu identifikasi
// paket mana yang berisi event damage.

if (args.Length < 1)
{
    Console.WriteLine("Usage: PacketAnalyze <file.pcap> [serverPort]");
    return;
}

string file = args[0];
int serverPort = args.Length > 1 ? int.Parse(args[1]) : 1818;

if (args.Length > 2 && args[2] == "scan")
{
    uint min = uint.Parse(args[3]);
    uint max = uint.Parse(args[4]);
    ScanForValue(file, serverPort, min, max);
    return;
}

if (args.Length > 2 && args[2] == "find")
{
    FindString(file, args[3]);
    return;
}

if (args.Length > 2 && args[2] == "findhex")
{
    FindHex(file, args[3]);
    return;
}

if (args.Length > 2 && args[2] == "fields")
{
    // Cari semua kemunculan magic bytes A4 CE 07 00, breakdown 10 field 4-byte pertama.
    FieldBreakdown(file);
    return;
}

if (args.Length > 2 && args[2] == "report")
{
    Report(file);
    return;
}

if (args.Length > 2 && args[2] == "partylist")
{
    ParsePartyList(file);
    return;
}

if (args.Length > 2 && args[2] == "timeline")
{
    int center = int.Parse(args[3]);
    int window = args.Length > 4 ? int.Parse(args[4]) : 15;
    Timeline(file, center, window);
    return;
}

if (args.Length > 2 && args[2] == "client")
{
    // Dump semua paket client->server (DestinationPort == serverPort) + tanda
    // kalau server membalas dengan paket damage skill sendiri (A4 CE 07 00).
    ClientDump(file, serverPort);
    return;
}

if (args.Length > 2 && args[2] == "pkt")
{
    // Hex dump lengkap 1 paket (nomor pkt# sama dengan mode lain), 16 byte/baris.
    int want = int.Parse(args[3]);
    using var r = new CaptureFileReaderDevice(file);
    r.Open(new SharpPcap.DeviceConfiguration());
    int idx = 0;
    r.OnPacketArrival += (s, e) =>
    {
        if (++idx != want) return;
        var tcp = Packet.ParsePacket(e.GetPacket().LinkLayerType, e.GetPacket().Data).Extract<TcpPacket>();
        var pl = tcp?.PayloadData ?? Array.Empty<byte>();
        Console.WriteLine($"pkt#{idx} {tcp?.SourcePort}->{tcp?.DestinationPort} len={pl.Length}");
        for (int o = 0; o < pl.Length; o += 16)
            Console.WriteLine($"{o,5}: {Convert.ToHexString(pl, o, Math.Min(16, pl.Length - o))}");
    };
    r.Capture();
    r.Close();
    return;
}

if (args.Length > 2 && args[2] == "castwin")
{
    // Per cast (C>S 0x4590), print semua pesan S>C (dipecah per length-prefix)
    // dari cast sampai `ms` milidetik sesudahnya.
    int ms = args.Length > 3 ? int.Parse(args[3]) : 600;
    CastWindows(file, serverPort, ms);
    return;
}

ushort? onlyOpcode = args.Length > 2 ? Convert.ToUInt16(args[2], 16) : null;

using var reader = new CaptureFileReaderDevice(file);
reader.Open(new SharpPcap.DeviceConfiguration());

var groups = new Dictionary<ushort, List<(DateTime ts, byte[] payload)>>();

reader.OnPacketArrival += (s, e) =>
{
    var raw = e.GetPacket();
    var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
    var tcp = packet.Extract<TcpPacket>();
    if (tcp is null || tcp.SourcePort != serverPort || tcp.PayloadData.Length < 8)
        return;

    var payload = tcp.PayloadData;
    ushort opcode = BitConverter.ToUInt16(payload, 4);
    if (!groups.TryGetValue(opcode, out var list))
        groups[opcode] = list = new List<(DateTime, byte[])>();
    list.Add((raw.Timeval.Date, payload));
};

reader.Capture();
reader.Close();

Console.WriteLine($"Total opcode unik dari server (port {serverPort}): {groups.Count}\n");

static void FindString(string file, string needle)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    byte[] needleBytes = System.Text.Encoding.ASCII.GetBytes(needle);
    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null) return;

        var payload = tcp.PayloadData;
        for (int i = 0; i + needleBytes.Length <= payload.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needleBytes.Length; j++)
            {
                if (payload[i + j] != needleBytes[j]) { match = false; break; }
            }
            if (match)
            {
                int start = Math.Max(0, i - 24);
                int len = Math.Min(payload.Length - start, 300);
                Console.WriteLine($"pkt#{pktIndex}  {tcp.SourcePort} -> {tcp.DestinationPort}  needle_offset={i}  payloadLen={payload.Length}");
                Console.WriteLine($"  context(offset {start}): {Convert.ToHexString(payload, start, len)}");
                bool hasToken = false;
                byte[] tok = { 0x9E, 0xB3, 0xA8, 0x9C };
                for (int k = 0; k + 4 <= payload.Length; k++)
                {
                    if (payload[k] == tok[0] && payload[k+1] == tok[1] && payload[k+2] == tok[2] && payload[k+3] == tok[3])
                    {
                        Console.WriteLine($"  >> token 9EB3A89C ditemukan di offset {k} (jarak dari nama: {k - i})");
                        hasToken = true;
                    }
                }
                if (!hasToken) Console.WriteLine("  >> token 9EB3A89C TIDAK ada di paket ini");
            }
        }
    };
    reader.Capture();
    reader.Close();
}

static void FieldBreakdown(string file)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    byte[] magic = { 0xA4, 0xCE, 0x07, 0x00 };
    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null) return;
        var payload = tcp.PayloadData;

        for (int i = 0; i + 4 <= payload.Length; i++)
        {
            if (payload[i] == magic[0] && payload[i+1] == magic[1] && payload[i+2] == magic[2] && payload[i+3] == magic[3])
            {
                Console.Write($"pkt#{pktIndex} magicAt={i}: ");
                for (int f = 0; f < 10; f++)
                {
                    int off = i + f * 4;
                    if (off + 4 <= payload.Length)
                        Console.Write($"F{f}={BitConverter.ToUInt32(payload, off),12} ");
                }
                Console.WriteLine();
            }
        }
    };
    reader.Capture();
    reader.Close();
}

static void Timeline(string file, int center, int window)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        if (pktIndex < center - window || pktIndex > center + window) return;

        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null || tcp.PayloadData.Length == 0) return;

        var payload = tcp.PayloadData;
        ushort opcode = payload.Length >= 6 ? BitConverter.ToUInt16(payload, 4) : (ushort)0;
        string marker = pktIndex == center ? " <== TARGET" : "";
        Console.WriteLine($"pkt#{pktIndex,4} {raw.Timeval.Date:HH:mm:ss.fff} {tcp.SourcePort,5}->{tcp.DestinationPort,-5} opcode=0x{opcode:X4} len={payload.Length,4} hex={Convert.ToHexString(payload, 0, Math.Min(24, payload.Length))}{marker}");
    };
    reader.Capture();
    reader.Close();
}

static void Report(string file)
{
    const ushort OPCODE_AUTO_SELF = 0xCC5D;
    const ushort OPCODE_AUTO_OTHER = 0xCC5E;
    const ushort OPCODE_SKILL_OTHER = 0xCEA5;
    byte[] skillMagic = { 0xA4, 0xCE, 0x07, 0x00 };
    const uint SELF_TOKEN = 0xFFFFFFFF;

    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    var byToken = new Dictionary<uint, (int hits, double dmg, int auto, double autoDmg, int skill, double skillDmg)>();

    void Add(uint token, string kind, double dmg)
    {
        if (dmg <= 0 || dmg > 1_000_000_000) return;
        var s = byToken.GetValueOrDefault(token, (0, 0, 0, 0, 0, 0));
        s.hits++; s.dmg += dmg;
        if (kind == "Auto") { s.auto++; s.autoDmg += dmg; } else { s.skill++; s.skillDmg += dmg; }
        byToken[token] = s;
    }

    reader.OnPacketArrival += (s, e) =>
    {
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null) return;
        var payload = tcp.PayloadData;
        if (payload.Length < 6) return;

        ushort opcode = BitConverter.ToUInt16(payload, 4);
        if (opcode == OPCODE_AUTO_SELF && payload.Length >= 16)
            Add(SELF_TOKEN, "Auto", BitConverter.ToUInt32(payload, 12));
        else if (opcode == OPCODE_AUTO_OTHER && payload.Length >= 36)
            Add(BitConverter.ToUInt32(payload, 8), "Auto", BitConverter.ToUInt32(payload, 32));
        else if (opcode == OPCODE_SKILL_OTHER && payload.Length >= 24)
            Add(BitConverter.ToUInt32(payload, 12), "Skill", BitConverter.ToUInt32(payload, 20));

        for (int i = 0; i + 4 <= payload.Length; i++)
        {
            if (payload[i] == skillMagic[0] && payload[i + 1] == skillMagic[1] &&
                payload[i + 2] == skillMagic[2] && payload[i + 3] == skillMagic[3])
            {
                if (i + 16 <= payload.Length)
                    Add(SELF_TOKEN, "Skill", BitConverter.ToUInt32(payload, i + 12));
            }
        }
    };
    reader.Capture();
    reader.Close();

    double total = byToken.Values.Sum(v => v.dmg);
    Console.WriteLine($"Total damage semua attacker: {total:N0}\n");
    Console.WriteLine($"{"Token",-12} {"Hits",6} {"TotalDmg",16} {"Auto(hits/dmg)",22} {"Skill(hits/dmg)",22}");
    foreach (var kv in byToken.OrderByDescending(x => x.Value.dmg))
    {
        string tokenLabel = kv.Key == SELF_TOKEN ? "DIRI SENDIRI" : kv.Key.ToString();
        Console.WriteLine($"{tokenLabel,-12} {kv.Value.hits,6} {kv.Value.dmg,16:N0} {kv.Value.auto,3}/{kv.Value.autoDmg,10:N0}      {kv.Value.skill,3}/{kv.Value.skillDmg,10:N0}");
    }
}

static void ParsePartyList(string file)
{
    const ushort OPCODE_PARTY_LIST = 0x041C;

    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null) return;
        var payload = tcp.PayloadData;

        if (payload.Length < 6) return;
        ushort opcode = BitConverter.ToUInt16(payload, 2); // format 2-byte length disini
        if (opcode != OPCODE_PARTY_LIST) return;

        Console.WriteLine($"pkt#{pktIndex} len={payload.Length} - mencari blok nama 16-byte...");

        // Cari semua posisi yang mirip "nama ASCII diikuti null padding sampai 16 byte"
        for (int i = 0; i + 16 <= payload.Length; i++)
        {
            if (!char.IsLetter((char)payload[i])) continue;

            int nameLen = 0;
            while (nameLen < 16 && i + nameLen < payload.Length &&
                   payload[i + nameLen] >= 0x20 && payload[i + nameLen] < 0x7F)
                nameLen++;
            if (nameLen < 3) continue;

            bool restIsNull = true;
            for (int k = nameLen; k < 16 && i + k < payload.Length; k++)
                if (payload[i + k] != 0) { restIsNull = false; break; }
            if (!restIsNull) continue;

            string name = System.Text.Encoding.ASCII.GetString(payload, i, nameLen);

            // print 4 byte SEBELUM nama dan 40 byte SESUDAH blok nama (16 byte) buat dianalisa presisi
            int before = Math.Max(0, i - 4);
            int afterStart = i + 16;
            int afterLen = Math.Min(40, payload.Length - afterStart);
            Console.WriteLine($"  nameAt={i} name=\"{name}\"");
            Console.WriteLine($"    before(4B) : {Convert.ToHexString(payload, before, i - before)}");
            Console.WriteLine($"    after(40B) : {Convert.ToHexString(payload, afterStart, afterLen)}");

            i += 15; // skip block ini
        }
    };
    reader.Capture();
    reader.Close();
}

static void FindHex(string file, string hexStr)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    byte[] needle = Convert.FromHexString(hexStr);
    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null) return;

        var payload = tcp.PayloadData;
        for (int i = 0; i + needle.Length <= payload.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (payload[i + j] != needle[j]) { match = false; break; }
            }
            if (match)
            {
                ushort opcode = payload.Length >= 6 ? BitConverter.ToUInt16(payload, 4) : (ushort)0;
                string ascii = System.Text.Encoding.ASCII.GetString(payload)
                    .Select(c => c >= 32 && c < 127 ? c : '.').Aggregate("", (a, c) => a + c);
                bool hasReadableText = System.Text.RegularExpressions.Regex.IsMatch(ascii, "[A-Za-z]{4,}");
                Console.WriteLine($"pkt#{pktIndex} {tcp.SourcePort}->{tcp.DestinationPort} opcode=0x{opcode:X4} offset={i} len={payload.Length} hasText={hasReadableText}");
                if (hasReadableText)
                {
                    var texts = System.Text.RegularExpressions.Regex.Matches(ascii, "[A-Za-z][A-Za-z0-9_]{3,}");
                    foreach (System.Text.RegularExpressions.Match m in texts)
                        Console.WriteLine($"    text: {m.Value}");
                }
            }
        }
    };
    reader.Capture();
    reader.Close();
}

static void ClientDump(string file, int serverPort)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    byte[] skillMagic = { 0xA4, 0xCE, 0x07, 0x00 };
    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null || tcp.PayloadData.Length == 0) return;
        var payload = tcp.PayloadData;
        string ts = raw.Timeval.Date.ToString("HH:mm:ss.fff");

        if (tcp.DestinationPort == serverPort)
        {
            ushort opcode = payload.Length >= 6 ? BitConverter.ToUInt16(payload, 4) : (ushort)0;
            Console.WriteLine($"C>S pkt#{pktIndex,5} {ts} opcode=0x{opcode:X4} len={payload.Length,4} hex={Convert.ToHexString(payload, 0, Math.Min(64, payload.Length))}");
        }
        else if (tcp.SourcePort == serverPort)
        {
            for (int i = 0; i + 16 <= payload.Length; i++)
            {
                if (payload[i] == skillMagic[0] && payload[i + 1] == skillMagic[1] &&
                    payload[i + 2] == skillMagic[2] && payload[i + 3] == skillMagic[3])
                    Console.WriteLine($"S>C pkt#{pktIndex,5} {ts} ** SKILL DAMAGE {BitConverter.ToUInt32(payload, i + 12):N0}");
            }
        }
    };
    reader.Capture();
    reader.Close();
}

static void CastWindows(string file, int serverPort, int ms)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    DateTime? castAt = null;
    int castNo = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null || tcp.PayloadData.Length < 6) return;
        var payload = tcp.PayloadData;
        var ts = raw.Timeval.Date;

        if (tcp.DestinationPort == serverPort)
        {
            if (BitConverter.ToUInt16(payload, 4) == 0x4590)
            {
                castNo++;
                castAt = ts;
                Console.WriteLine($"\n##### CAST {castNo} @ {ts:HH:mm:ss.fff}");
            }
            return;
        }
        if (castAt is null || (ts - castAt.Value).TotalMilliseconds > ms) return;

        // Pecah per pesan: [uint32 len][uint16 opcode]...
        int pos = 0;
        while (pos + 6 <= payload.Length)
        {
            int len = (int)BitConverter.ToUInt32(payload, pos);
            if (len < 6 || pos + len > payload.Length) len = payload.Length - pos;
            ushort op = BitConverter.ToUInt16(payload, pos + 4);
            if (op != 0xCC18) // noise gerakan
                Console.WriteLine($"  +{(ts - castAt.Value).TotalMilliseconds,4:F0}ms 0x{op:X4} len={len,3} {Convert.ToHexString(payload, pos, len)}");
            pos += len;
        }
    };
    reader.Capture();
    reader.Close();
}

static void ScanForValue(string file, int serverPort, uint min, uint max)
{
    using var reader = new CaptureFileReaderDevice(file);
    reader.Open(new SharpPcap.DeviceConfiguration());

    int pktIndex = 0;
    reader.OnPacketArrival += (s, e) =>
    {
        pktIndex++;
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null || tcp.SourcePort != serverPort) return;

        var payload = tcp.PayloadData;
        for (int off = 0; off + 4 <= payload.Length; off++)
        {
            uint val = BitConverter.ToUInt32(payload, off);
            if (val >= min && val <= max)
            {
                ushort opcode = payload.Length >= 6 ? BitConverter.ToUInt16(payload, 4) : (ushort)0;
                Console.WriteLine($"pkt#{pktIndex} opcode=0x{opcode:X4} offset={off,3} value={val,12}   hex={Convert.ToHexString(payload)}");
            }
        }
    };
    reader.Capture();
    reader.Close();
}

foreach (var kv in groups.OrderByDescending(g => g.Value.Count))
{
    if (onlyOpcode.HasValue && kv.Key != onlyOpcode.Value) continue;

    var sizes = kv.Value.Select(v => v.payload.Length).Distinct().OrderBy(x => x).ToList();
    Console.WriteLine($"Opcode 0x{kv.Key:X4}  count={kv.Value.Count}  sizes=[{string.Join(",", sizes)}]");

    int take = onlyOpcode.HasValue ? int.MaxValue : 5;
    foreach (var (ts, payload) in kv.Value.Take(take))
    {
        Console.WriteLine($"    {ts:HH:mm:ss.fff}  {Convert.ToHexString(payload)}");
    }
    Console.WriteLine();
}
