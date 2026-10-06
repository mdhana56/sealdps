# Protokol Seal Online (yang dipakai meter)

Referensi **terkini** untuk kontributor — isinya sama dengan yang dijalankan `src/DpsMeterUI/CaptureEngine.cs`. Riwayat riset (termasuk teori yang sudah dibatalkan) ada di [research-log.md](research-log.md).

> Hanya diverifikasi di server **BOD**. Server lain bisa beda.

## Dasar

- Yang di-parse cuma pesan **server → client** (data pertempuran). Pesan client → server (termasuk login) terenkripsi dan tidak di-decode — meter tidak punya akses ke kredensial akun.
- Semua angka **little-endian**; damage/HP = `uint32` mentah (tanpa skala).
- Format pesan umum: `[uint32 len][uint16 opcode][07 00][isi...]`. Server sering membundel beberapa pesan dalam 1 segmen TCP, jadi parser **men-scan seluruh payload** mencari `?? ?? 07 00` lalu memvalidasi `len`.
- Pesan besar (`CC35`, `CC1B`) sering terpotong ke beberapa segmen TCP → disambung pakai sequence number (`SplitMessage`).
- Kalau game lewat ExitLag, traffic muncul di adapter loopback (`127.0.0.1:<port acak>`). Adapter dipilih dari IP lokal koneksi game (`NativeTcp.cs`).

**Semua offset di bawah relatif ke posisi opcode** (bukan awal pesan).

## Damage

| Opcode | Arti | Layout |
|---|---|---|
| `CC5D` | auto-attack **sendiri** | `+4` target · `+8` damage · `+12` sisa HP target |
| `F339` | auto-attack kedua sendiri (tangan kiri / dual wield) | sama dengan `CC5D` |
| `CC5E` | auto-attack **pemain lain** | `+4` attacker · `+24` target · `+28` damage · `+32` sisa HP |
| `F33C` | auto-attack kedua pemain lain | sama dengan `CC5E` |
| `CEA4` | skill **sendiri** | lihat *Format skill* |
| `CEA6` | skill sendiri yang **membunuh** target (HP 0) | sama dengan `CEA4` |
| `CEA5` | skill **pemain lain** | lihat *Format skill* |
| `F360` | varian skill sendiri (len 40) | `+4` skill · `+8` target · `+12` damage · `+16` sisa HP. Damage 0 = miss; kalau damage == sisa HP dan HP tidak turun → bukan damage. Dihitung hanya kalau HP target turun. |

### Format skill (`CEA4` / `CEA5` / `CEA6`)

Dibedakan dari nilai `+8`: 1–20 = jumlah hit, lebih besar = entity ID.

1. **Daftar hit** (multi-target / combo): `+4` skill ID · `+8` jumlah hit · lalu tiap hit 16 byte mulai `+12` = `[target][damage][sisa HP][0]` · setelah daftar hit: `[attacker]` (hanya bermakna di `CEA5`).
2. **Single**:
   - `CEA4`: `+4` skill ID · `+8` target · `+12` damage · `+16` sisa HP
   - `CEA5`: `+4` skill ID · `+8` attacker · `+12` target · `+16` damage · `+20` sisa HP

Skill ID → nama: `skill.edt` di `etc\etc.SPAK` (lihat *Data game*).

### Anti-duplikat

1 hit kadang terkirim lewat 2 jalur (combat log sendiri + siaran umum). Hit dengan jenis + damage yang sama dalam **150 ms** dibuang. Kalau "kembaran" hit sendiri itu resolve ke nama anggota party, nama itu dipakai sebagai **nama sendiri** (auto-detect).

## Identitas pemain

| Opcode | Arti | Layout |
|---|---|---|
| `041C` | roster party (framing **2-byte** len, opcode di payload `+2`) | per anggota: nama (null-padded 16 byte) · `+17` level · `+21` job ID · `+49` entity ID (semua relatif ke awal nama). Selalu berisi daftar **lengkap** party, dikirim hanya saat anggota berubah. |
| `CC1B` | karakter masuk area pandang | `+4` count · record 732 byte mulai `+8`. Per record (relatif ke opcode record pertama): `+12` entity ID · `+16` nama · `+44` job ID · equip mulai record `+104` (slot 16 byte = `[item ID][refine di 16 bit bawah][?][?]`). Validasi: `len == 12 + count*732`. |

- Diri sendiri **selalu** dari `CC5D`/`CEA4`/`CEA6`/`F339`/`F360` → token tetap `SELF_TOKEN` (0xFFFFFFFF), tidak perlu ID.
- **Entity ID bukan permanen**: bisa ganti saat masuk dungeon, pindah map, atau relog. Karena itu UI menggabungkan baris **per nama**, dan filter party dicek per nama.
- Job ID → nama: `JobNames.cs` (dari `usa.edt`). 0 Beginner, 1 Warrior (11 Berserker, 21 Swordmaster), 2 Knight (12 Renegade, 22 Defender), 3 Jester (13 Assassin, 23 Gambler), 4 Mage (14 Ice Wizard, 24 Fire Wizard), 5 Priest (15 Templar, 25 Apostle), 6 Craftsman (16 Demolitionist, 26 Artisan), 7 Game Master, 8 Vagabond, 9 Hunter (19 Archer, 29 Gunner), 31 Cook (131 Chef, 231 Food Fighter).
- Equip sendiri **tidak** ada di `CC1B` (hanya dikirim saat login, belum dipetakan).

## Monster

| Opcode | Arti | Layout |
|---|---|---|
| `CC35` | daftar entity di sekitar | `+4` count · record 112 byte mulai `+8` = `[entity ID][x][y][jenis monster +12]...`. Validasi: `len == 12 + count*112`. |
| `CC36` | spawn / respawn 1 monster (len 120) | `+4` entity · `+16` jenis |
| `CC37` | entity hilang / mati | (belum dipakai) |

Jenis monster → nama: `monster_us.nam`.

## Data game (`etc\etc.SPAK`)

Format dari proyek open-source [unsealed](https://github.com/feryandi/unsealed):

- `.SPAK` = zip ZipCrypto; password diturunkan dari versi di komentar zip (`SkillTable.cs`).
- `.edt` = XOR stream LCG (seed `0x11CFD`), header "Seal Online Data v13".
- `skill.edt`: 1 baris per (skill, level), 975 byte; `+0` skill ID, `+4` nama; `+259` job ID skill.
- `monster_us.nam`: tidak di-XOR; header 52 byte, record 210 byte = ID jenis (ASCII 10 byte) + nama (200 byte).
- Nama item: `item.edt` + `ItemString.edt` (`ItemNames.cs`).
- Drop list (`MonsterDrops.cs`): `monster.edt` = 36 field int64 per monster; field 0 = ID jenis, field 14 = baris drop biasa, field 15 = baris drop khusus (boss/event). `drop_1/2/3.edt` = satu tabel 72 kolom dipecah 3 file (24 int32 per file, baris sejajar), isinya ID item saja. `drop_4.edt` = drop khusus (24 int32). Peluang drop tidak ada di client. `drop_5`, `quest_drop`, `Rod_Drop`, `f_drop` belum dipakai.

## Belum terpecahkan

- **Crit**: tidak ada outlier damage; field setelah sisa HP di `CC5D`/`CC5E` (nilai 1–6 di ~10–20% hit) tidak terbukti crit. Perlu capture + rekaman layar sebagai patokan.
- **Bleed / DoT**: field-nya belum ketemu.
- **Equip sendiri**: paket login belum dipetakan.

## Cara riset / verifikasi

1. Rekam: `dotnet run --project tools/PacketCapture -- <detik>` (sebagai admin) sambil melakukan 1 aksi yang terkontrol. Catat angka yang tampil di layar.
2. Analisa: `tools/PacketAnalyze` (decode opcode, cari angka damage/offset). Hitung offset pakai tool, jangan manual.
3. Uji parser: `DPSMETER_REPLAY=<file.pcap>` memutar capture lewat parser yang sama dengan mode live.
4. **Jangan commit `.pcap`** — isinya traffic akun kamu. Di PR, tempel potongan hex + offset saja.
