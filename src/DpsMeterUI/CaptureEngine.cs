using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace DpsMeterUI;

/// Label = teks yang ditampilkan di breakdown (nama skill untuk skill sendiri,
/// selain itu sama dengan Kind). Kind tetap dipakai buat anti-duplikat.
/// TargetId = entity ID monster yang kena (0 = tidak diketahui), TargetHpAfter =
/// sisa HP target setelah hit (0 = tidak diketahui).
/// 1 slot equip karakter (dari broadcast CC1B). Refine 0 = +0 / tidak berlaku.
public record EquipItem(string Slot, uint ItemId, string Name, int Refine)
{
    public string RefineText => Refine > 0 ? $"+{Refine}" : "";
}

public record HitEvent(uint AttackerToken, string Kind, double Damage, bool IsSelf, string Label, uint TargetId = 0, double TargetHpAfter = 0);

public class CaptureEngine
{
    // Token tetap/reserved buat "diri sendiri" - CC5D/skill-magic SELALU berarti
    // itu kamu, jadi nggak perlu nebak/cocokin token asli sama sekali. Ini
    // menghindari masalah token panjang yang beda tiap sesi dan nggak match
    // party roster.
    public const uint SELF_TOKEN = 0xFFFFFFFF;

    // Semua offset di bawah RELATIF KE POSISI OPCODE pesan (bukan awal segmen TCP).

    const ushort OPCODE_AUTO_ATTACK_SELF = 0xCC5D;
    // F339 = auto-attack kedua (kemungkinan tangan kiri, Assassin dual wield),
    // layout sama persis dengan CC5D, damage ~setengahnya. F33C = versi siaran
    // (layout sama dengan CC5E). Terverifikasi 2026-09-26 lewat rantai sisa HP.
    const ushort OPCODE_AUTO_ATTACK2_SELF = 0xF339;
    const ushort OPCODE_AUTO_ATTACK2_OTHER = 0xF33C;
    // CC5D = [opcode][target][damage][sisa HP target]...
    const int AUTO_ATTACK_TARGET_OFFSET = 4;
    const int AUTO_ATTACK_DAMAGE_OFFSET = 8;
    const int AUTO_ATTACK_HP_OFFSET = 12;

    const ushort OPCODE_AUTO_ATTACK_OTHER = 0xCC5E;
    // CC5E = [opcode][attacker +4][?][x][y][1][target +24][damage +28][sisa HP +32]
    const int OTHER_AUTO_ATTACK_ENTITY_OFFSET = 4;
    const int OTHER_AUTO_ATTACK_TARGET_OFFSET = 24;
    const int OTHER_AUTO_ATTACK_DAMAGE_OFFSET = 28;
    const int OTHER_AUTO_ATTACK_HP_OFFSET = 32;

    // Skill sendiri, lihat ParseSkill. CEA6 = hit yang MEMBUNUH target (sisa HP 0,
    // layout sama dengan CEA4) - dulu nggak kebaca, makanya one-hit kill hilang.
    const ushort SKILL_SELF_OPCODE = 0xCEA4;
    const ushort SKILL_SELF_KILL_OPCODE = 0xCEA6;
    // F360 = varian skill sendiri, layout = CEA4 (len 40). Sering damage 0 (miss)
    // atau damage kecil (hit tertahan) - lihat ParseSkillVariant.
    const ushort SKILL_SELF_VARIANT_OPCODE = 0xF360;

    // Skill orang lain, lihat ParseSkill.
    const ushort OPCODE_SKILL_OTHER = 0xCEA5;

    // Party roster: opcode 0x041C, framing beda (2-byte length, bukan 4-byte).
    // Dipakai buat dapat DAFTAR NAMA anggota party (stabil), TAPI id di paket ini
    // ternyata cuma snapshot sesaat - bisa nggak sama lagi dengan id combat yang
    // dipakai belakangan (terutama kalau ada yang relog). Jadi id dari sini TIDAK
    // dipakai langsung buat filter/cocokin - cuma buat kumpulin NAMA yang valid.
    const ushort OPCODE_PARTY_LIST = 0x041C;

    // Entity broadcast: opcode 0xCC1B, dikirim server tiap ada entity (karakter
    // lain) yang masuk area pandang kamu - jauh lebih sering daripada party list,
    // termasuk kemungkinan besar re-fire kalau seseorang relog & balik ke map yang
    // sama. Dipakai buat terus-menerus refresh mapping id(combat) -> nama secara
    // live, supaya nggak kena masalah id "basi" kayak party list.
    const ushort OPCODE_ENTITY_BROADCAST = 0xCC1B;
    const int ENTITY_BROADCAST_ID_OFFSET = 12;   // relatif dari posisi opcode
    const int ENTITY_BROADCAST_NAME_OFFSET = 16; // relatif dari posisi opcode
    const int ENTITY_BROADCAST_JOB_OFFSET = 44;  // relatif dari posisi opcode (record +36)

    // Entity list: opcode 0xCC35 (magic 35 CC 07 00), dikirim tiap ada entity masuk
    // jarak pandang (dan ikut ter-bundle ulang secara berkala). Pesan =
    // [len][magic][count] + count record x 112 byte; record = [entity ID][x][y]
    // [ID jenis monster]... Terverifikasi 2026-09-26: entity 5314 -> jenis 2237
    // = "[Look A Like]Giant R. Rabbit" (dummy).
    const ushort ENTITY_LIST_OPCODE = 0xCC35;
    // CC36 = spawn/RESPAWN 1 monster: [opcode][entity +4][x][y][jenis +16]... (len 120).
    // Monster yang respawn cuma diumumkan lewat sini, bukan CC35.
    const ushort ENTITY_SPAWN_OPCODE = 0xCC36;
    const int ENTITY_RECORD_SIZE = 112;
    const int ENTITY_TYPE_OFFSET_IN_RECORD = 12;
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> _entityTypes = new();

    public void RememberEntityType(uint entityId, uint typeId) => _entityTypes[entityId] = typeId;

    /// Nama monster buat entity ID target, null kalau jenisnya belum ke-detect.
    public string? TargetName(uint entityId) =>
        _entityTypes.TryGetValue(entityId, out uint type) ? MonsterNames.Get(type) : null;

    /// ID jenis monster buat entity ID target, null kalau belum ke-detect.
    public uint? TargetType(uint entityId) =>
        _entityTypes.TryGetValue(entityId, out uint type) ? type : null;

    static readonly string[] CandidateProcessNames = { "SO3DPlus", "SO3DPlus_x64" };

    public event Action<HitEvent>? HitDetected;
    public event Action<string>? StatusChanged;
    public event Action? PartyUpdated;

    // Nama-nama yang TERKONFIRMASI anggota party (dari paket roster). Stabil,
    // nggak berubah kecuali party berubah beneran.
    readonly HashSet<string> _partyMemberNames = new();

    // id(combat, live) -> nama, terus di-refresh dari broadcast entity. Ini yang
    // dipakai buat resolve nama SEKARANG, bukan roster yang bisa basi.
    readonly Dictionary<uint, string> _liveEntityNames = new();

    // nama karakter -> nama job. Beda dengan entity ID, job nempel ke nama (stabil).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _jobByName = new();

    /// Nama job karakter, null kalau belum ke-detect (belum ada di roster party /
    /// belum lewat broadcast CC1B).
    public string? JobOf(string name) => _jobByName.TryGetValue(name, out var job) ? job : null;

    void RememberJob(string name, uint jobId)
    {
        string? job = JobNames.Get(jobId);
        if (job != null) _jobByName[name] = job;
    }

    /// Buat ditampilkan di UI (nama per token yang udah pernah ke-resolve).
    public IReadOnlyDictionary<uint, string> PartyMembers => _liveEntityNames
        .Where(kv => _partyMemberNames.Contains(kv.Value))
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// Kalau true, hit dari token yang namanya nggak dikenali sebagai anggota
    /// party (lewat resolusi nama live, bukan id mentah) akan diabaikan.
    public bool FilterToPartyOnly { get; set; }

    /// True kalau token combat ini (lewat nama live-nya) anggota party SAAT INI.
    public bool IsInParty(uint token) =>
        _liveEntityNames.TryGetValue(token, out var name) && _partyMemberNames.Contains(name);

    /// True kalau nama karakter ini ada di roster party saat ini.
    public bool IsPartyName(string name) => _partyMemberNames.Contains(name);

    /// Nama live token combat (dari broadcast CC1B / roster), null kalau belum ketahuan.
    public string? NameOf(uint token) => _liveEntityNames.TryGetValue(token, out var name) ? name : null;

    readonly List<ILiveDevice> _devices = new();
    string? _filter;
    System.Threading.Timer? _watchdog;
    readonly object _syncLock = new();
    // Paket bisa datang dari beberapa adapter (thread capture masing-masing) -> parser
    // (state sambungan segmen, dictionary nama) diproses satu per satu.
    readonly object _packetLock = new();

    /// True kalau driver Npcap belum terpasang (UI menampilkan petunjuk install).
    public bool NpcapMissing { get; private set; }

    // Koneksi game dicek ulang terus (bukan cuma sekali pas start): dulu kalau meter
    // dibuka sebelum game login / masuk server game, filter ke-set ke port yang salah
    // (atau gagal) dan capture nggak pernah jalan. Port proxy ExitLag juga bisa
    // berubah (63216, 11000, ...) dan pindah server/channel bikin koneksi baru.
    static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);

    public bool Start()
    {
        if (!NpcapInstalled())
        {
            NpcapMissing = true;
            SetStatus("Npcap belum terpasang - install dari npcap.com lalu buka ulang meter.");
            return false;
        }
        SyncCapture();
        _watchdog = new System.Threading.Timer(_ => SyncCapture(), null, WatchdogInterval, WatchdogInterval);
        return true;
    }

    static bool NpcapInstalled()
    {
        string sys = Environment.SystemDirectory;
        return System.IO.File.Exists(System.IO.Path.Combine(sys, "Npcap", "wpcap.dll")) ||
               System.IO.File.Exists(System.IO.Path.Combine(sys, "wpcap.dll"));
    }

    /// Samakan filter capture dengan koneksi game SAAT INI; buka/ganti adapter kalau perlu.
    /// Adapter dipilih dari IP LOKAL koneksi game, jadi jalan untuk semua cara main:
    /// - langsung (LAN / Wi-Fi apa pun)        -> adapter yang punya IP lokal itu
    /// - lewat proxy lokal (ExitLag, dll)     -> koneksi ke 127.0.0.1 -> adapter loopback
    /// - lewat VPN / tunnel (PingZapper, dll)  -> adapter virtual yang punya IP lokal itu
    /// Kalau nggak ada adapter yang cocok, semua adapter dibuka dengan filter yang sama.
    void SyncCapture()
    {
        if (!System.Threading.Monitor.TryEnter(_syncLock)) return; // cek sebelumnya masih jalan
        try
        {
            var proc = CandidateProcessNames.SelectMany(Process.GetProcessesByName).FirstOrDefault();
            if (proc is null)
            {
                if (_devices.Count == 0) SetStatus("Menunggu game... (proses Seal Online belum jalan)");
                return;
            }

            var conns = NativeTcp.Established(proc.Id)
                .Where(c => c.RemotePort != 80 && c.RemotePort != 443)
                .ToList();
            if (conns.Count == 0)
            {
                // Tetap pakai filter lama kalau sudah pernah aktif (koneksi bisa putus sebentar).
                if (_devices.Count == 0) SetStatus("Menunggu koneksi game... (login & masuk map)");
                return;
            }

            string filter = "tcp and (" + string.Join(" or ", conns
                .Select(c => $"(host {c.RemoteIp} and port {c.RemotePort})").Distinct().OrderBy(x => x)) + ")";
            if (filter == _filter && _devices.Count > 0) return;

            var all = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().ToList();
            var chosen = PickDevices(all, conns);
            if (chosen.Count == 0)
            {
                SetStatus("Tidak ada network adapter terdeteksi oleh Npcap.");
                return;
            }

            CloseDevices();
            foreach (var device in chosen)
            {
                try
                {
                    device.Open(DeviceModes.Promiscuous, 1000);
                    device.Filter = filter;
                    device.OnPacketArrival += OnPacketArrival;
                    device.StartCapture();
                    _devices.Add(device);
                }
                catch { try { device.Close(); } catch { } } // adapter yang nggak bisa dibuka dilewati
            }
            if (_devices.Count == 0)
            {
                SetStatus("Adapter jaringan gagal dibuka (jalankan sebagai Administrator?)");
                return;
            }
            _filter = filter;

            string via = _devices.Count == 1 ? _devices[0].Description ?? _devices[0].Name : $"{_devices.Count} adapter";
            string ports = string.Join(", ", conns.Select(c => c.RemotePort).Distinct());
            SetStatus($"Aktif - {proc.ProcessName} (PID {proc.Id}) via {via}\nServer: {conns[0].RemoteIp} port {ports}");
        }
        catch (Exception ex)
        {
            SetStatus($"Gagal mulai capture: {ex.Message}");
        }
        finally
        {
            System.Threading.Monitor.Exit(_syncLock);
        }
    }

    static List<LibPcapLiveDevice> PickDevices(List<LibPcapLiveDevice> all, List<NativeTcp.Connection> conns)
    {
        if (conns.All(c => System.Net.IPAddress.IsLoopback(c.RemoteIp)))
        {
            var lo = all.Where(d => d.Loopback || (d.Description ?? "").Contains("loopback", StringComparison.OrdinalIgnoreCase)).ToList();
            if (lo.Count > 0) return lo.Take(1).ToList();
        }

        var localIps = conns.Select(c => c.LocalIp).Where(ip => !System.Net.IPAddress.IsLoopback(ip)).ToHashSet();
        var byIp = all.Where(d => d.Addresses.Any(a => a.Addr?.ipAddress != null && localIps.Contains(a.Addr.ipAddress))).ToList();
        if (byIp.Count > 0) return byIp;

        // Nggak ada yang cocok (mis. driver VPN yang IP-nya nggak dilaporkan) -> buka semua.
        return all;
    }

    string? _lastStatus;
    void SetStatus(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    void CloseDevices()
    {
        foreach (var device in _devices)
        {
            try
            {
                device.OnPacketArrival -= OnPacketArrival;
                device.StopCapture();
                device.Close();
            }
            catch { }
        }
        _devices.Clear();
        _filter = null;
    }

    /// Bersihin semua "ingatan" party (nama, mapping id) - dipanggil pas user klik
    /// Reset, biar data party lama (yang udah nggak relevan, mis. abis keluar
    /// party) nggak nyangkut terus.
    public void ClearParty()
    {
        _partyMemberNames.Clear();
        _liveEntityNames.Clear();
        SelfName = null;
    }

    /// Khusus development: putar ulang file .pcap lewat parser yang sama persis
    /// dengan capture live (env DPSMETER_REPLAY=path.pcap).
    public void StartReplay(string pcapPath)
    {
        var reader = new SharpPcap.LibPcap.CaptureFileReaderDevice(pcapPath);
        reader.Open(new DeviceConfiguration());
        reader.OnPacketArrival += OnPacketArrival;
        StatusChanged?.Invoke($"REPLAY {System.IO.Path.GetFileName(pcapPath)}");
        System.Threading.Tasks.Task.Run(() => { reader.Capture(); reader.Close(); });
    }

    public void Stop()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        lock (_syncLock) CloseDevices();
    }

    void OnPacketArrival(object? s, PacketCapture e)
    {
        lock (_packetLock) HandlePacket(e);
    }

    void HandlePacket(PacketCapture e)
    {
        var raw = e.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp is null) return;

        var payload = tcp.PayloadData;
        ContinueSplitMessages(tcp, payload);
        if (payload.Length < 6) return;

        // Party list pakai framing 2-byte length, opcode di offset 2 (bukan 4).
        if (BitConverter.ToUInt16(payload, 2) == OPCODE_PARTY_LIST)
        {
            TryParsePartyList(payload);
        }

        // Entity broadcast (CC1B) suka ter-bundle di tengah paket lain (bukan
        // cuma di posisi awal), jadi discan di seluruh payload kayak skill magic.
        for (int i = 0; i + 2 <= payload.Length; i++)
        {
            if (BitConverter.ToUInt16(payload, i) == OPCODE_ENTITY_BROADCAST)
                TryParseEntityBroadcast(payload, i, tcp);
        }

        // Server sering membundel beberapa pesan dalam 1 segmen TCP, jadi SEMUA
        // pesan damage dicari di seluruh payload (bukan cuma di offset 4 / awal
        // segmen). Tiap pesan = [uint32 len][opcode 2B][07 00][isi...]; posisi i di
        // bawah = posisi opcode, len ada di i-4 (dipakai buat validasi).
        for (int i = 0; i + 4 <= payload.Length; i++)
        {
            if (payload[i + 2] != 0x07 || payload[i + 3] != 0x00) continue;
            ushort op = BitConverter.ToUInt16(payload, i);
            uint len = i >= 4 ? BitConverter.ToUInt32(payload, i - 4) : 0;

            if ((op == SKILL_SELF_OPCODE || op == SKILL_SELF_KILL_OPCODE || op == OPCODE_SKILL_OTHER) && len >= 24 && len <= 400)
            {
                ParseSkill(payload, i, len, isSelf: op != OPCODE_SKILL_OTHER);
            }
            else if (op == SKILL_SELF_VARIANT_OPCODE && len == 40 && i + 20 <= payload.Length)
            {
                ParseSkillVariant(payload, i);
            }
            else if ((op == OPCODE_AUTO_ATTACK_SELF || op == OPCODE_AUTO_ATTACK2_SELF) && len >= 20 && len <= 64 && i + AUTO_ATTACK_HP_OFFSET + 4 <= payload.Length)
            {
                uint raw32 = BitConverter.ToUInt32(payload, i + AUTO_ATTACK_DAMAGE_OFFSET);
                uint target = BitConverter.ToUInt32(payload, i + AUTO_ATTACK_TARGET_OFFSET);
                uint hp = BitConverter.ToUInt32(payload, i + AUTO_ATTACK_HP_OFFSET);
                Emit(SELF_TOKEN, "Auto", raw32, isSelf: true, target: target, targetHp: hp);
            }
            else if (op == ENTITY_SPAWN_OPCODE && len == 4 + 4 + ENTITY_RECORD_SIZE && i + 20 <= payload.Length)
            {
                // Spawn/respawn 1 entity = 1 record CC35 tanpa count.
                RememberEntityType(BitConverter.ToUInt32(payload, i + 4), BitConverter.ToUInt32(payload, i + 16));
            }
            else if ((op == OPCODE_AUTO_ATTACK_OTHER || op == OPCODE_AUTO_ATTACK2_OTHER) && len >= 40 && len <= 80 && i + OTHER_AUTO_ATTACK_HP_OFFSET + 4 <= payload.Length)
            {
                uint token = BitConverter.ToUInt32(payload, i + OTHER_AUTO_ATTACK_ENTITY_OFFSET);
                uint raw32 = BitConverter.ToUInt32(payload, i + OTHER_AUTO_ATTACK_DAMAGE_OFFSET);
                uint target = BitConverter.ToUInt32(payload, i + OTHER_AUTO_ATTACK_TARGET_OFFSET);
                uint hp = BitConverter.ToUInt32(payload, i + OTHER_AUTO_ATTACK_HP_OFFSET);
                Emit(token, "Auto", raw32, isSelf: false, target: target, targetHp: hp);
            }
            else if (op == ENTITY_LIST_OPCODE)
            {
                TryParseEntityList(payload, i, tcp);
            }
        }
    }

    // Pesan CC35 sering kepotong ke beberapa segmen TCP (di capture dummy 5 dari 6
    // pesan kepotong - bisa sampai ~2KB), CC1B juga (pesan 5-8KB, sering cuma ~1,3KB
    // di segmen pertama). Sisa pesannya disambung dari segmen berikutnya di stream
    // yang sama, dicek pakai sequence number TCP.
    sealed class SplitMessage
    {
        byte[]? _buf;
        int _have;
        uint _nextSeq;
        ushort _srcPort, _dstPort;

        /// Simpan awal pesan (dari msgStart sampai akhir payload), tunggu sisanya.
        public void Start(TcpPacket tcp, byte[] payload, int msgStart, uint msgLen)
        {
            _buf = new byte[msgLen];
            _have = payload.Length - msgStart;
            Array.Copy(payload, msgStart, _buf, 0, _have);
            _nextSeq = unchecked(tcp.SequenceNumber + (uint)payload.Length);
            _srcPort = tcp.SourcePort;
            _dstPort = tcp.DestinationPort;
        }

        /// Dipanggil tiap segmen: ambil byte lanjutan dari awal payload. Balikin
        /// pesan utuh ([len][opcode]...) kalau sudah lengkap, selain itu null.
        public byte[]? Continue(TcpPacket tcp, byte[] payload)
        {
            if (_buf is null || payload.Length == 0 ||
                tcp.SourcePort != _srcPort || tcp.DestinationPort != _dstPort) return null;

            int gap = unchecked((int)(tcp.SequenceNumber - _nextSeq));
            if (gap < 0) return null;                    // retransmit segmen lama, abaikan
            if (gap > 0) { _buf = null; return null; }   // ada segmen yang hilang - buang

            int take = Math.Min(_buf.Length - _have, payload.Length);
            Array.Copy(payload, 0, _buf, _have, take);
            _have += take;
            _nextSeq = unchecked(_nextSeq + (uint)payload.Length);
            if (_have < _buf.Length) return null;

            var complete = _buf;
            _buf = null;
            return complete;
        }
    }

    readonly SplitMessage _cc35Split = new();
    readonly SplitMessage _cc1bSplit = new();

    void ContinueSplitMessages(TcpPacket tcp, byte[] payload)
    {
        if (_cc35Split.Continue(tcp, payload) is { } entityList)
            TryParseEntityList(entityList, 4, tcp: null);
        if (_cc1bSplit.Continue(tcp, payload) is { } broadcast)
            TryParseEntityBroadcast(broadcast, 4, tcp: null);
    }

    void TryParseEntityList(byte[] payload, int magicPos, TcpPacket? tcp)
    {
        int msgStart = magicPos - 4;
        if (msgStart < 0 || magicPos + 8 > payload.Length) return;
        uint msgLen = BitConverter.ToUInt32(payload, msgStart);
        uint count = BitConverter.ToUInt32(payload, magicPos + 4);
        // Validasi: panjang pesan harus pas = 12 + count*112, biar nggak salah
        // baca byte acak yang kebetulan mirip magic.
        if (count == 0 || count > 200 || msgLen != 12 + count * ENTITY_RECORD_SIZE) return;

        // Record yang sudah lengkap (cukup id + jenis) langsung dibaca.
        for (int r = 0; r < count; r++)
        {
            int rec = magicPos + 8 + r * ENTITY_RECORD_SIZE;
            if (rec + ENTITY_TYPE_OFFSET_IN_RECORD + 4 > payload.Length) break;
            uint id = BitConverter.ToUInt32(payload, rec);
            uint type = BitConverter.ToUInt32(payload, rec + ENTITY_TYPE_OFFSET_IN_RECORD);
            RememberEntityType(id, type);
        }

        // Pesan kepotong -> simpan, tunggu sisanya di segmen berikutnya.
        if (tcp != null && msgStart + msgLen > payload.Length)
            _cc35Split.Start(tcp, payload, msgStart, msgLen);
    }

    public int KnownEntityCount => _entityTypes.Count;

    /// Nama diri sendiri, ke-detect otomatis begitu hit sendiri (SELF_TOKEN)
    /// ketauan "kembar" sama broadcast umum yang resolve ke nama party. Null
    /// kalau belum ke-detect.
    public string? SelfName { get; private set; }
    public event Action? SelfNameDetected;

    // Anti-duplikat: server kadang broadcast 1 event combat lewat >1 jalur
    // (combat log diri sendiri + broadcast umum), jadi hit yang sama bisa
    // ke-capture 2x. Kombinasi (kind, damage) yang sama persis dalam <150ms
    // dianggap duplikat - dipakai juga buat auto-detect nama sendiri (kalau
    // versi "lain"-nya resolve ke nama party, berarti itu nama kita).
    readonly List<(string kind, double damage, uint token, DateTime time)> _recentHits = new();

    bool IsDuplicate(string kind, double damage, uint token, bool isSelf)
    {
        var now = DateTime.Now;
        _recentHits.RemoveAll(h => (now - h.time).TotalMilliseconds > 150);
        var match = _recentHits.FirstOrDefault(h => h.kind == kind && Math.Abs(h.damage - damage) < 0.5);
        if (match.kind != null)
        {
            if (SelfName is null)
            {
                uint otherToken = isSelf ? match.token : token;
                bool otherIsSelfEvent = isSelf ? false : match.token == SELF_TOKEN;
                if ((isSelf || otherIsSelfEvent) && _liveEntityNames.TryGetValue(otherToken, out var nm) && _partyMemberNames.Contains(nm))
                {
                    SelfName = nm;
                    SelfNameDetected?.Invoke();
                }
            }
            return true;
        }
        _recentHits.Add((kind, damage, token, now));
        return false;
    }

    // Damage skill: CEA4 = skill kamu sendiri, CEA5 = skill orang lain. Offset
    // relatif ke posisi opcode (op). Ada 2 format, dibedakan dari field +8
    // (jumlah hit 1-20 vs entity ID > 20):
    // 1) Daftar hit: [op][skill +4][jumlah hit +8] + tiap hit 16 byte mulai +12 =
    //    [target][damage][sisa HP][0], lalu [attacker] tepat setelah daftar hit.
    //    CEA5 PemainA: [326 Judgement Strike][1][5315][601.179][HP][0][405]...
    //    CEA4 PemainB: [83 Throw Bomb][1][598][385.902][HP]...
    // 2) Single:
    //    CEA4: [op][skill +4][target +8][damage +12][sisa HP +16]
    //          (Vanbonang: [184 Soul Breaker][5314][35.093.124][HP])
    //    CEA5: [op][skill +4][attacker +8][target +12][damage +16][sisa HP +20]
    //          ([171 Mega Freeze][273][5314][11.296.759][HP])
    // Terverifikasi 2026-09-26 (capture_login_20260926_134048 + capture lama).
    void ParseSkill(byte[] p, int op, uint len, bool isSelf)
    {
        if (op + 24 > p.Length) return;
        string label = SkillNames.Get(BitConverter.ToUInt32(p, op + 4));
        uint field8 = BitConverter.ToUInt32(p, op + 8);

        int afterHits = op + 12 + (int)field8 * 16;
        if (field8 >= 1 && field8 <= 20 && len >= 20 + field8 * 16 && afterHits + 4 <= p.Length)
        {
            uint attacker = isSelf ? SELF_TOKEN : BitConverter.ToUInt32(p, afterHits);
            for (int h = 0; h < field8; h++)
            {
                int hit = op + 12 + h * 16;
                Emit(attacker, "Skill", BitConverter.ToUInt32(p, hit + 4), isSelf, label,
                     BitConverter.ToUInt32(p, hit), BitConverter.ToUInt32(p, hit + 8));
            }
            return;
        }

        if (isSelf)
            Emit(SELF_TOKEN, "Skill", BitConverter.ToUInt32(p, op + 12), isSelf: true, label,
                 field8, BitConverter.ToUInt32(p, op + 16));
        else
            Emit(field8, "Skill", BitConverter.ToUInt32(p, op + 16), isSelf: false, label,
                 BitConverter.ToUInt32(p, op + 12), BitConverter.ToUInt32(p, op + 20));
    }

    // Sisa HP terakhir yang kelihatan per target, buat validasi F360.
    readonly Dictionary<uint, double> _lastHp = new();

    // F360 (skill sendiri, varian): layout = CEA4 single [skill][target][damage][sisa HP].
    // Dari rantai sisa HP (capture_login_20260926_135653): damage > 0 = hit asli
    // (sering jauh lebih kecil dari biasa, mis. Soul Breaker 4,4jt), damage 0 = miss.
    // Pengecualian: kadang field damage berisi HP saat itu (damage == sisa HP dan HP
    // nggak turun) -> BUKAN damage. Jadi cuma dihitung kalau HP beneran turun.
    void ParseSkillVariant(byte[] p, int op)
    {
        uint skillId = BitConverter.ToUInt32(p, op + 4);
        uint target = BitConverter.ToUInt32(p, op + 8);
        uint damage = BitConverter.ToUInt32(p, op + 12);
        uint hp = BitConverter.ToUInt32(p, op + 16);
        if (damage == 0 || damage == hp) return;
        if (_lastHp.TryGetValue(target, out double prev) && hp >= prev) return;
        Emit(SELF_TOKEN, "Skill", damage, isSelf: true, SkillNames.Get(skillId), target, hp);
    }

    void Emit(uint token, string kind, double damage, bool isSelf, string? label = null, uint target = 0, double targetHp = 0)
    {
        if (target != 0) _lastHp[target] = targetHp;
        if (damage <= 0 || damage > 1_000_000_000) return;
        // Diri sendiri SELALU lolos. Orang lain (auto-attack maupun skill) cuma
        // lolos kalau namanya kekonfirmasi anggota party SAAT INI - filter beneran
        // ketat, karena kalau nggak, percuma ada info nama party (semua orang
        // ikut kehitung). UI sekarang set FilterToPartyOnly = false dan menyaring
        // sendiri pakai IsInParty (biar tombol "di luar party" bisa on/off mundur).
        if (!isSelf && FilterToPartyOnly && !IsInParty(token)) return;
        if (IsDuplicate(kind, damage, token, isSelf)) return;
        HitDetected?.Invoke(new HitEvent(token, kind, damage, isSelf, label ?? kind, target, targetHp));
    }

    // CC1B = [len][opcode][count] + count record x 732 byte mulai opcode+8. Dulu cuma
    // record pertama yang dibaca, padahal 1 pesan bisa berisi sampai ~11 karakter ->
    // sisanya jadi "Player N" di mode "di luar party". Record berikutnya cuma dibaca
    // kalau header pesan valid (07 00 + len == 12 + count*732).
    const int ENTITY_BROADCAST_RECORD_SIZE = 732;

    void TryParseEntityBroadcast(byte[] payload, int opcodePos, TcpPacket? tcp)
    {
        TryParseEntityBroadcastRecord(payload, opcodePos, opcodePos + 4 <= payload.Length &&
            payload[opcodePos + 2] == 0x07 && payload[opcodePos + 3] == 0x00);

        if (opcodePos < 4 || opcodePos + 8 > payload.Length) return;
        if (payload[opcodePos + 2] != 0x07 || payload[opcodePos + 3] != 0x00) return;
        uint len = BitConverter.ToUInt32(payload, opcodePos - 4);
        uint count = BitConverter.ToUInt32(payload, opcodePos + 4);
        if (count == 0 || count > 50 || len != 12 + count * ENTITY_BROADCAST_RECORD_SIZE) return;

        // Pesan kepotong -> simpan, record sisanya dibaca setelah tersambung.
        int msgStart = opcodePos - 4;
        if (tcp != null && msgStart + len > payload.Length)
            _cc1bSplit.Start(tcp, payload, msgStart, len);
        for (int r = 1; r < count; r++)
        {
            // Record ke-r diperlakukan seperti record pertama milik "opcode" virtual
            // yang bergeser r x 732 byte (offset id/nama/job relatif ke opcode sama).
            int virtualOp = opcodePos + r * ENTITY_BROADCAST_RECORD_SIZE;
            if (virtualOp + ENTITY_BROADCAST_NAME_OFFSET + 3 > payload.Length) break; // sisa pesan di segmen TCP berikutnya
            TryParseEntityBroadcastRecord(payload, virtualOp, headerValid: true);
        }
    }

    void TryParseEntityBroadcastRecord(byte[] payload, int opcodePos, bool headerValid)
    {
        int idOffset = opcodePos + ENTITY_BROADCAST_ID_OFFSET;
        int nameOffset = opcodePos + ENTITY_BROADCAST_NAME_OFFSET;
        if (nameOffset + 3 > payload.Length || idOffset + 4 > payload.Length) return;

        uint entityId = BitConverter.ToUInt32(payload, idOffset);

        int nameLen = 0;
        int maxLen = Math.Min(20, payload.Length - nameOffset);
        while (nameLen < maxLen && payload[nameOffset + nameLen] != 0)
        {
            byte b = payload[nameOffset + nameLen];
            if (b < 0x20 || b >= 0x7F) return; // bukan nama ASCII valid, bukan paket yang kita cari
            nameLen++;
        }
        if (nameLen < 3) return;

        string name = System.Text.Encoding.ASCII.GetString(payload, nameOffset, nameLen);

        // CC1B = [opcode][count] + record 732 byte mulai +8: [?][entity ID][nama]...
        // [job ID di record +36]. Dicek 2026-09-27: 35/35 cocok dengan job dari skill
        // yang dipakai karakter itu (CEA5 skill ID -> kolom job_id skill.edt).
        int jobOffset = opcodePos + ENTITY_BROADCAST_JOB_OFFSET;
        if (jobOffset + 4 <= payload.Length && headerValid)
            RememberJob(name, BitConverter.ToUInt32(payload, jobOffset));
        if (headerValid)
            RememberEquipment(name, payload, opcodePos + 8);

        if (!_liveEntityNames.TryGetValue(entityId, out var existing) || existing != name)
        {
            _liveEntityNames[entityId] = name;
            if (_partyMemberNames.Contains(name)) PartyUpdated?.Invoke();
        }
    }

    // Equip di record CC1B: mulai record +104, slot 16 byte = [ID item][refine][opsi?][?].
    // Urutan slot dicek 2026-09-27 dari 5 karakter (capture_login_20260926_135653):
    // PemainC slot 4 = Dragon Inferno Apostle Mace.XG, slot 16-19 = kostum H/T/B/S, dst.
    // Refine senjata/tangan kiri dikemas bareng nilai lain (0x6000C = +12) -> 16 bit bawah.
    // Slot 13-14 bukan item (teks/angka lain), slot 20 = bentuk transmutasi senjata.
    const int EQUIP_OFFSET_IN_RECORD = 104;
    const int EQUIP_SLOT_SIZE = 16;
    static readonly (int Slot, string Label)[] EquipSlots =
    {
        (0, "Kepala"), (1, "Baju atas"), (2, "Baju bawah"), (3, "Sepatu"),
        (4, "Senjata"), (5, "Tangan kiri"), (6, "Sayap"), (7, "Tunggangan"),
        (9, "Kalung"), (8, "Aksesori"), (10, "Aksesori"), (11, "Aksesori"), (12, "Spirit"),
        (15, "Kostum"), (16, "Kostum (H)"), (17, "Kostum (T)"), (18, "Kostum (B)"), (19, "Kostum (S)"),
        (20, "Transmutasi"), (22, "Aura"),
    };
    // Slot yang field kedua-nya bukan refine (tunggangan/transmutasi berisi angka lain).
    static readonly HashSet<int> SlotsWithoutRefine = new() { 7, 20 };

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<EquipItem>> _equipByName = new();

    /// Equip karakter (dari broadcast CC1B terakhir), null kalau belum pernah lewat
    /// area pandang sejak meter nyala. Equip diri sendiri nggak ada di CC1B.
    public IReadOnlyList<EquipItem>? EquipmentOf(string name) =>
        _equipByName.TryGetValue(name, out var eq) ? eq : null;

    void RememberEquipment(string name, byte[] payload, int recordStart)
    {
        if (recordStart + ENTITY_BROADCAST_RECORD_SIZE > payload.Length) return; // record belum lengkap
        var items = new List<EquipItem>();
        foreach (var (slot, label) in EquipSlots)
        {
            int at = recordStart + EQUIP_OFFSET_IN_RECORD + slot * EQUIP_SLOT_SIZE;
            uint id = BitConverter.ToUInt32(payload, at);
            if (id == 0) continue;
            uint raw = BitConverter.ToUInt32(payload, at + 4);
            int refine = SlotsWithoutRefine.Contains(slot) ? 0 : (int)(raw & 0xFFFF);
            if (refine > 20) refine = 0; // bukan angka refine
            items.Add(new EquipItem(label, id, ItemNames.Get(id), refine));
        }
        if (items.Count > 0) _equipByName[name] = items;
    }

    void TryParsePartyList(byte[] payload)
    {
        var found = new Dictionary<uint, string>(); // id di sini cuma dipakai transisional, nama yang penting

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
            if (!restIsNull) { i += nameLen; continue; }

            string name = System.Text.Encoding.ASCII.GetString(payload, i, nameLen);

            // ID selalu ada di offset+49 dari AWAL nama (bukan dihitung dari batas
            // blok berikutnya) - terverifikasi konsisten walau panjang nama beda
            // (9 huruf vs 12 huruf, sama-sama +49 dari byte pertama nama).
            const int ID_OFFSET_FROM_NAME_START = 49;
            int idPos = i + ID_OFFSET_FROM_NAME_START;
            if (idPos + 4 <= payload.Length)
            {
                uint id = BitConverter.ToUInt32(payload, idPos);
                found[id] = name;
            }

            // Entri roster = [nama 17 byte][level][job ID]... -> job di nama+21.
            // (Vanbonang: level 310, job 13 = Assassin; PemainB: job 16.)
            const int JOB_OFFSET_FROM_NAME_START = 21;
            if (i + JOB_OFFSET_FROM_NAME_START + 4 <= payload.Length)
                RememberJob(name, BitConverter.ToUInt32(payload, i + JOB_OFFSET_FROM_NAME_START));

            i += 15;
        }

        if (found.Count == 0) return;

        // Paket roster ini selalu berisi daftar LENGKAP party saat ini (bukan
        // incremental), jadi GANTI TOTAL daftar lama - bukan numpuk terus.
        // Kalau numpuk, nama dari party LAMA (yang udah nggak relevan/orangnya
        // udah nggak di party lagi) tetap ketinggalan dan bikin salah kira
        // "masih anggota party" pas ID-nya kebetulan kepakai ulang.
        var newNames = new HashSet<string>(found.Values);
        bool changed = !newNames.SetEquals(_partyMemberNames);

        _partyMemberNames.Clear();
        foreach (var name in newNames) _partyMemberNames.Add(name);

        foreach (var kv in found)
            _liveEntityNames[kv.Key] = kv.Value;

        if (changed) PartyUpdated?.Invoke();
    }
}
