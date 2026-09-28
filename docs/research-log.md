# Log Riset (kronologis)

> **Ini catatan harian riset, bukan referensi.** Banyak kesimpulan awal di sini
> kemudian **dibatalkan** oleh bagian "KOREKSI" di bawahnya. Untuk format paket
> yang berlaku sekarang, baca [protocol.md](protocol.md). File ini disimpan untuk
> konteks: kenapa suatu teori salah, dan data pembanding dari layar game.
>
> Nama file `.pcap` yang disebut di sini tidak ada di repo (berisi traffic akun).

Damage yang tercatat dari layar game (patokan verifikasi):

| Skill | Level | % | Damage tercatat | Tanggal |
|---|---|---|---|---|
| Soul Breaker (Assassin) | 18 | 3172% | 30.105.755 / 31.606.713 / ~30.37jt / ~30.40jt | 2026-09-25 |
| Deathly Slash (Assassin) | 21 | 5003% | 48.825.908 / 50.265.372 / 50.349.032 (+ bleed ~20rb, field belum terverifikasi) | 2026-09-25 |
| Sticky Bomb (Demolitionist) | - | - | 439.212 | 2026-09-25 |
| Throw Bomb (Demolitionist) | - | - | ~300rb | 2026-09-25 |
| Auto-attack Demolitionist | - | - | 12.229 | 2026-09-25 |
| Auto-attack Assassin | - | - | 77.198 - 81.845 (kisaran) | 2026-09-25 |

## Update 2026-09-25: teori "skill ID" DIBATALKAN, attacker ID KETEMU

Breakdown lengkap paket damage (opcode 0xCEA4, magic bytes A4 CE 07 00), field
per 4-byte dari posisi magic (F0=magic sendiri):

| Field | Offset dari magic | Isi | Status |
|---|---|---|---|
| F1 | +4 | Target/entity ID (dummy) | berubah tiap dummy baru |
| F2 | +8 | **Bukan skill ID** - ini sequence counter global, naik terus (5314→5315 di 2 hit Sticky Bomb yang sama) | teori lama dibatalkan |
| F3 | +12 | **Damage** | terkonfirmasi lama |
| F4 | +16 | Kemungkinan timestamp/nonce, nilainya besar & beda2 | belum dipakai |
| F6 | +24 | Angka 3-4 digit, kemungkinan sisa HP target | belum pasti |
| **F7** | **+28** | **Attacker token** - konstan per karakter | **TERKONFIRMASI** |
| F8 | +32 | Selalu 0 | - |

Bukti F7 = attacker ID: `2628301726` muncul di SEMUA hit Vanbonang (Soul Breaker
ATAU Deathly Slash - 2 skill beda, karakter sama). `660993` muncul di semua hit
PemainB (karakter lain). Field ini sekarang dipakai di DpsMeter buat
breakdown "siapa damage terbesar".

Skill ID kemungkinan besar TIDAK dikirim di paket damage ini sama sekali -
mungkin cuma dikirim sekali pas skill di-cast (paket terpisah, belum ketemu
pola pastinya) dan client yang nampilin nama skill dari state lokal, bukan dari
paket damage. Belum lanjut cari ini - prioritas geser ke attacker ID dulu (sudah selesai).

### Daftar attacker token yang sudah diketahui
| Token (decimal) | Karakter | Job |
|---|---|---|
| 2628301726 | Vanbonang | Assassin |
| 660993 | PemainB | Demolitionist |

### Update 2026-09-25: auto-attack orang lain pakai opcode BEDA, dan token BEDA TIPE

Auto-attack diri sendiri (opcode 0xCC5D) bawa token panjang (F7-style,
offset+32 dari awal paket) yang konsisten per karakter.

Auto-attack ORANG LAIN (party member, dkk) ternyata dikirim lewat opcode
**0xCC5E berdiri sendiri** (bukan dibungkus 0xCC5D), dan cuma bawa **entity ID
LOKAL pendek** (offset+8, contoh: 181) - BUKAN token panjang yang sama. Jadi
attacker key buat auto-attack sendiri vs auto-attack orang lain beda "jenis
ID"-nya (long token vs short local id), sudah dihandle terpisah di DpsMeter.

PENTING: token panjang (long token, F7-style) untuk karakter yang SAMA
ternyata BISA BEDA antar sesi login (contoh: Vanbonang = 9EB3A89C di sesi
awal, jadi AAB3A89C di sesi setelah relog) - kemungkinan bukan ID permanen,
lebih mirip session/connection-derived value. Jadi dictionary hardcode
`attackerNames` BISA JADI BASI tiap relog. Belum ada solusi permanen untuk
ini - kalau attacker nggak kekenali namanya padahal sudah pernah, kemungkinan
besar karena token-nya berubah gara-gara relog.

Entity ID pendek `181` = **PemainA** (party member yang auto-attack bareng
Vanbonang tgl 2026-09-25). Sudah ditambahkan ke `attackerNames` di DpsMeter.
CATATAN: entity ID lokal ini kemungkinan besar berubah tiap sesi map/party
beda (beda dari long token yang lebih stabil per-karakter), jadi kemungkinan
perlu diisi ulang tiap sesi baru.

## KOREKSI 2026-09-25: field "attacker orang lain" ternyata ID TARGET, BUKAN attacker

Kesimpulan "181 = PemainA" di atas TERNYATA SALAH ALAMAT. Waktu farming solo
ke banyak monster berbeda, field yang sama (offset+8 di `CC5E` berdiri
sendiri, offset+12 di `CEA5` berdiri sendiri) berubah-ubah TIAP MONSTER,
padahal yang nyerang cuma Vanbonang sendirian. Ini membuktikan field itu
sebenarnya **ID TARGET** (monster/dummy yang kena hit), bukan ID siapa yang
mukul. Kebetulan waktu tes party kemarin nilainya konstan (181) karena
PemainA nyerang dummy yang SAMA terus-menerus - jadi "konstan" itu asalnya
dari TARGET yang konstan, bukan attacker yang konstan.

**Dampak**: deteksi "auto-attack/skill orang lain" (opcode CC5E dan CEA5
berdiri sendiri) SEMENTARA DIMATIKAN total di DpsMeter (console & UI) per
tanggal ini, karena kalau dipaksa jalan, hit sendiri malah ke-split jadi
puluhan "Player" palsu tiap ganti target. DpsMeter sekarang cuma track
damage kamu sendiri (lewat CC5D + CEA4, yang formatnya beda dan sudah
terbukti akurat pakai long token asli).

**Buat next research**: field attacker ID yang valid buat "orang lain" masih
belum ketemu. Perlu cari lagi dengan skenario yang lebih ketat: 2 karakter
beda MENYERANG TARGET YANG SAMA secara bersamaan (biar target id konstan di
kedua sisi), lalu cari field LAIN (bukan offset+8/+12) yang beda antara
kedua karakter tapi konstan per karakter.

## KOREKSI LAGI 2026-09-25: field offset+8/+12 DINYALAKAN ULANG, terbukti valid dengan syarat

Test lanjutan: Vanbonang + 1 party member SAMA-SAMA nyerang 1 target yang
IDENTIK terus-menerus (tidak gonta-ganti). Hasilnya field offset+8 (CC5E)
konsisten cuma 2 nilai (177 dan 181), masing-masing damage-nya cocok
sempurna dengan karakter yang beda (177 = damage 77-81K match persis sama
CC5D-nya Vanbonang; 181 = damage 12-12,7K, karakter lain).

Kesimpulan final: field ini **memang bisa dipakai sebagai attacker
discriminator**, TAPI kemungkinan besar bukan "ID karakter permanen" -
lebih mirip **"slot target-lock"** yang di-assign per kombinasi
(attacker, target aktif). Selama attacker tidak ganti-ganti target,
nilainya stabil dan valid dipakai sebagai attacker ID. Begitu attacker
ganti target (terutama sering & cepat, kayak farming solo banyak monster),
nilainya ikut berubah dan bisa salah kebaca sebagai "player baru".

**Status**: DINYALAKAN ULANG di DpsMeter (console & UI). Cocok banget buat
kasus utama (party ngeroyok 1 target/boss yang sama - use-case paling umum
buat DPS meter). Kurang akurat kalau dipakai pas solo farming banyak
monster gonta-ganti cepat (breakdown per-player bisa muncul entry-entry aneh
kalau itu terjadi - abaikan aja / reset kalau nemuin ini).

Buat nambah karakter baru: pakai skill apapun, capture, cari lewat
`PacketAnalyze ... fields`, cocokkan F7 barunya, tambahkan ke dictionary
`attackerNames` di `src/DpsMeter/Program.cs`.

## Riset job ID lewat char-select (DITINGGALKAN)

Sempat dicari di paket client->server saat memilih karakter di char-select, tapi
arah ini ditinggalkan: job ternyata ada di roster party `041C` dan broadcast
`CC1B` (lihat bagian 2026-09-27 di bawah).

## KOREKSI 2026-09-26: SKILL ID KETEMU = F1 (+4 dari magic), F2 = entity ID sesi attacker

Capture terkontrol `capture_20260926_082507.pcap` (port proxy 11000), Vanbonang,
urutan: Soul Breaker 2x, Deathly Slash 2x, Soul Breaker 1x, Disorientation,
Vital Attack, Sudden Attack.

| Skill | F1 (skill ID) | Cooldown di paket 0x2236 | Damage |
|---|---|---|---|
| Sudden Attack | 182 | 6000 ms | 13,43jt |
| Vital Attack | 183 | 6000 ms | 14,25jt |
| Soul Breaker | 184 | 4000 ms | 34,5 - 35,1jt |
| Disorientation | 186 | 5000 ms | 13,64jt |
| Deathly Slash | 340 | 6000 ms | 57,3 - 57,4jt |

- F1 BUKAN target/dummy ID (kesimpulan lama salah). Soul Breaker tetap 184
  walau diselingi Deathly Slash. Paket cooldown `0x2236` = `[skillId][cooldown ms]`
  ikut tiap cast dan pakai ID yang sama.
- F2 (5314/5315/598) = entity ID sesi si attacker (sama dengan field di CC5D
  dan CD81), BUKAN sequence counter & BUKAN skill ID.
- Capture lama 173106 ("Deathly Slash") ternyata campuran beberapa skill:
  F1=340 selalu ~50jt, F1=184 selalu ~30jt.
- Demolitionist (data lama): Throw Bomb F1=83, Sticky Bomb F1=216 (belum
  dikonfirmasi terkontrol). Capture Throw Bomb pakai format bergeser 1 field
  (F2=1, F3=entity, F4=damage) - parser self-skill sekarang baca offset +12
  sebagai damage, jadi format ini perlu dicek terpisah.
- Dipakai di `src/DpsMeterUI/SkillNames.cs` untuk breakdown damage per skill.

## 2026-09-26: Nama skill OTOMATIS dari data game (etc.SPAK -> skill.edt)

Semua nama skill dibaca langsung dari file game saat meter start
(`src/DpsMeterUI/SkillTable.cs`), berdasarkan format yang didokumentasikan
project open-source "unsealed" (github.com/feryandi/unsealed):
- `.SPAK` = zip ZipCrypto; password diturunkan dari versi di komentar zip
  ("Seal Online Zip v5" di instalasi ini) - bukan brute-force/crack.
- `.edt` = XOR stream LCG (seed 0x11CFD).
- `skill.edt` = "Seal Online Data v13", 4645 baris x 975 byte, 1 baris per
  (skill, level). Offset 0 = int32 skill ID, offset 4 = nama (null-terminated).
- 362 skill unik. Verifikasi silang: 83 = Throw Bomb, 216 = Sticky Bomb (cocok
  dengan capture Demolitionist lama tanpa pernah diisi manual).
- `skill.edt` = nama English; `uskill.edt` / `skill01..23.edt` varian lain
  (belum dipakai).

## KOREKSI 2026-09-26: F2 = ID TARGET (bukan entity attacker), F4 = sisa HP target

Capture `capture_20260926_090520.pcap` (dummy, jalan menjauh lalu kembali):
- Paket entity list `CC35` (magic 35 CC 07 00): [len][magic][count] + count x
  record 112 byte = [entity ID][x][y][ID jenis monster].... Isinya entity 5314
  -> jenis 2237 dan 5315 -> jenis 2239, keduanya "[Look A Like]Giant R. Rabbit".
- Jadi layout damage skill sendiri (magic A4 CE 07 00):
  `[magic][skill ID][TARGET entity][damage][sisa HP target setelah hit]...`
  (bukti HP: 964.838.100 - 34.663.907 = 930.174.193 = F4 hit berikutnya).
  "5314 lalu 5315" di Sticky Bomb = 2 dummy bersebelahan.
- Auto sendiri `CC5D` = [target +8][damage +12][sisa HP +16].
- `CC5E` = [attacker +8][?][x][y][1][target +28][damage +32][sisa HP +36].
  Parser party (attacker di +8) sudah benar.
- 125 (`7D`) = kemungkinan entity sesi Vanbonang (muncul di CC56 +12 dan CC5E +8).
- Nama monster: `etc.SPAK` -> `monster_us.nam` ("SealOnline String Data v1",
  TIDAK di-XOR): header 52 byte (count ASCII di offset 32), record 210 byte =
  ID jenis (ASCII 10 byte) + nama (200 byte). 8102 entri.
- Dipakai di tab TARGET DpsMeterUI (damage per monster). Target skill anggota
  party (CEA5) belum diverifikasi -> masuk "Target ?".

## 2026-09-26: Format CEA5 (skill orang lain) + semua pesan damage bisa ter-bundle

Capture `capture_login_20260926_134048_loopback.pcap` (PemainA, Judgement Strike ~600rb):
- CEA5 bawa SKILL ID di field pertama, sama seperti CEA4. 2 format (offset dari opcode):
  1) Daftar hit: [op][skill +4][jumlah hit +8] + tiap hit 16 byte dari +12 =
     [target][damage][sisa HP][0], lalu [attacker] setelah daftar hit.
     PemainA: [326 Judgement Strike][1][5315][601.179][HP][0][405]...
  2) Single: [op][skill +4][attacker +8][target +12][damage +16][sisa HP +20].
- Format "daftar hit" JUGA dipakai CEA4 (skill sendiri): ini format "bergeser"
  Throw Bomb dulu ([83][1][598][385.902]...). Parser lama baca ID target (598)
  sebagai damage. Sekarang CEA4 & CEA5 lewat ParseSkill yang sama.
- BUG lama: CEA5/CC5D/CC5E cuma dibaca kalau ada di AWAL segmen TCP. Server sering
  membundel pesan (pkt#485: CEA5 di offset 32 setelah CC56) -> hit hilang. Sekarang
  semua pesan damage discan di seluruh payload, divalidasi pakai field panjang pesan.
- Entity 405 = PemainA, 273 = pemain lain (mage) di dekat dummy.
## 2026-09-26: Opcode damage tambahan (kill, auto kedua, varian skill) + spawn CC36

Capture `capture_login_20260926_135653_loopback.pcap` (farming Wild Beetle / Blue-Green
Shaman, banyak one-hit kill). Diverifikasi lewat rantai sisa HP: total damage per
monster yang dibunuh = HP penuhnya PERSIS (Wild Beetle 31.050.000, Shaman 27.000.000).
- `CC36` (len 120) = spawn/RESPAWN 1 monster: [op][entity +4][x][y][jenis +16] (= 1
  record CC35 tanpa count). Monster respawn cuma lewat sini -> dulu tanpa nama.
- `CEA6` (len 44) = skill sendiri yang MEMBUNUH target (sisa HP 0), layout = CEA4.
  Dulu nggak kebaca -> one-hit kill hilang.
- `F339` (len 40) = auto-attack kedua (kemungkinan tangan kiri Assassin), layout = CC5D,
  damage ~setengah auto biasa. `F33C` (len 52) = siarannya, layout = CC5E.
- `F360` (len 40) = varian skill sendiri, layout = CEA4. Damage 0 = miss; damage > 0 =
  hit asli tapi kecil (Soul Breaker 4,4jt vs normal ~35jt). Kadang field damage berisi
  HP saat itu (damage == sisa HP, HP nggak turun) -> dibuang. Len 36 = buff (Blitzkrieg
  ke diri sendiri) -> dibuang. Dihitung cuma kalau HP target beneran turun.
- `CC37` (len 12) = entity hilang/mati. `CC9B`/`F33F` = monster menyerang pemain.
- BELUM JELAS: entity 405 = penyerang Judgement Strike (PemainA) di capture 13:40, tapi di
  capture 13:55 jadi penyerang siaran auto-attack Vanbonang (F33C/CC5E). ID entity per sesi;
  penyebab (relog / 2 client game) belum dicek.
## 2026-09-27: Job karakter SOLVED (roster party + CC1B), crit belum ketemu

- Tabel job: `skill.edt` baris +259 = job_id tiap skill; nama job dari `usa.edt`
  (baris 408-434). ID: 0 Beginner, 1 Warrior (11 Berserker, 21 Swordmaster),
  2 Knight (12 Renegade, 22 Defender), 3 Jester (13 Assassin, 23 Gambler),
  4 Mage (14 Ice Wizard, 24 Fire Wizard), 5 Priest (15 Templar, 25 Apostle),
  6 Craftsman (16 Demolitionist, 26 Artisan), 7 Game Master, 8 Vagabond,
  9 Hunter (19 Archer, 29 Gunner), 31 Cook (131 Chef, 231 Food Fighter).
  -> `src/DpsMeterUI/JobNames.cs`.
- Roster party `041C`: entri = [nama 17 byte][level int32 @+17][job int32 @+21]
  [HP/AP...][entity ID @+49]. Vanbonang 310/13, PemainB 16, PemainA 25.
- `CC1B` = [op][count] + record 732 byte mulai op+8: [?][entity ID +4][nama +8]
  ... [job +36]. Diverifikasi 35/35 karakter vs job dari skill CEA5 yang mereka pakai.
- Crit: tidak ada damage outlier di ~400 auto + ratusan skill (variasi cuma ~±3%).
  Field setelah sisa HP di CC5D/CC5E (0, kadang 1-6 di ~10-20% hit) damage-nya
  tidak beda -> belum bisa disebut crit. Butuh capture + rekaman layar buat patokan.

## 2026-09-28: Skill support Apostle (dari skill.edt, belum dari paket)

Semua job 25 (Apostle). Deskripsi di baris skill.edt +463, nama internal +271.

| ID | Skill | Level | Efek (deskripsi game) |
|---|---|---|---|
| 208 | Holy Force (`SkillHolyforce`) | 21 | Damage diri + party naik, makin kuat sesuai stat magic caster. 20-35 detik. |
| 209 | Holy Magic (`SkillHolymagic`) | 21 | Magic diri + party naik (tidak berlaku ke rumpun Priest). 20-35 detik. |
| 206 | Bulwark (`SkillShield`) | 18 | Shield ke diri + party di dekat, menyerap 1.625 (lv1) s/d 3.850 (lv18) damage. |
| 211 | Holy Speed | 18 | (buff Apostle lain, belum dicek) |

- Besar buff Holy Force/Magic bergantung stat magic caster -> TIDAK bisa dihitung dari file game saja.
- Belum diketahui: paket cast buff (apakah CEA5 dengan damage 0 / opcode lain), paket buff aktif/habis
  di anggota party, dan apakah server mengirim sisa shield Bulwark. Perlu capture: Apostle cast
  Holy Force / Holy Magic / Bulwark di party, dicatat jam cast-nya.
