# Seal DPS Meter

DPS meter overlay untuk **Seal Online** (gaya LOA Logs): damage, DPS, D%, jumlah hit per anggota party, breakdown per skill, damage per monster (tab TARGET), badge job, dan equip pemain lain.

**Pasif sepenuhnya** — meter cuma *membaca* paket jaringan server→client lewat Npcap. Tidak inject, tidak baca/tulis memori game, tidak mengirim paket apa pun.

> [!WARNING]
> **Do with your own risk.** Proyek fan-made, tidak berafiliasi dengan publisher Seal Online. Segala risiko pemakaian (termasuk terhadap akun game) tanggung jawab pemakai.
>
> **Hanya dites di server BOD.** Server/versi lain bisa punya format paket berbeda — damage, nama, atau job bisa salah atau tidak terbaca sama sekali.

## Keamanan akun — meter ini TIDAK bisa mencuri akun

- **Tidak membaca login / password.** Semua yang dikirim game ke server (termasuk login) terenkripsi dan tidak di-decode. Meter hanya membaca data pertempuran yang dikirim server ke game: damage, nama & job anggota party, monster, equip yang terlihat.
- **Tidak mengirim data ke mana pun.** Tidak ada kode yang membuka koneksi internet atau meng-upload apa pun. Satu-satunya tautan adalah halaman download Npcap, yang dibuka di browser kalau Npcap belum terpasang.
- **Tidak menyimpan traffic.** Paket dibaca langsung di memori lalu dibuang. File yang ditulis cuma pilihan folder game (`%AppData%\SealDpsMeter`).
- **Cuma melihat koneksi game.** Filter capture dibatasi ke koneksi TCP milik proses Seal Online (web/HTTPS dikecualikan) — aktivitas lain di PC tidak ikut terbaca.
- **Tidak menyentuh game.** Tidak inject, tidak baca/tulis memori, tidak mengirim paket.
- **Source terbuka** — semua di atas bisa dicek sendiri di `src/DpsMeterUI/CaptureEngine.cs`. Download exe hanya dari halaman Releases repo ini.

## Fitur yang sudah jalan

### Tab DPS — tabel party
| Kolom | Arti |
|---|---|
| **DMG** | total damage pemain (hover = angka lengkap) |
| **D%** | porsi damage dari total party |
| **DPS** | damage per detik (dari hit pertama s/d hit terakhir pemain itu) |
| **Hits** | jumlah hit |

- Header: timer sesi, **T. DMG** (total damage party), **T. DPS** (DPS party). Timer mulai di hit pertama dan berhenti sendiri kalau 5 detik nggak ada hit.
- Yang dihitung: **diri sendiri** (auto-attack + skill) dan **anggota party** (nama dari paket roster party). Pemain di luar party disembunyikan.
- Badge **job** di kiri nama (ikon + warna rumpun, hover = nama job).
- Anggota party langsung muncul (0 damage) begitu roster party terbaca, walau belum menyerang.
- 1 karakter = 1 baris, walau entity ID-nya ganti (masuk dungeon / relog).

### Breakdown skill — klik nama pemain
Rincian per skill (dan "Auto"): DMG, DPS, D% (porsi dari damage pemain itu), **Casts** (hit skill yang sama dalam 500 ms = 1 cast, mis. skill AoE kena 5 monster), Hits, **APH** (rata-rata per hit), **MaxH** (hit terbesar). Nama skill dibaca otomatis dari file game. Klik kanan = kembali.

### EQUIP — dari halaman breakdown
Equip pemain lain (senjata, armor, aksesori, kostum, sayap, tunggangan, dll + refine), dibaca dari data karakter yang dikirim server waktu pemain itu masuk area pandang. Equip sendiri belum bisa (server cuma mengirimnya saat login).

### Tab TARGET — damage per monster
Daftar monster yang diserang: nama monster, DMG, D%, DPS, Hits, dan **HP penuh** (perkiraan dari sisa HP di paket damage). Klik monster = tabel DPS party **khusus monster itu** (mis. cuma damage ke boss), bisa dibuka breakdown-nya juga.

### Tombol di header
| Tombol | Fungsi |
|---|---|
| 👥 **Di luar party** | ON = semua pemain di sekitar ikut dihitung (badge **SEMUA** muncul). Berlaku mundur — data tetap tercatat, cuma disembunyikan |
| ⟳ **Reset** | hapus semua data, mulai sesi baru |
| ⏸ **Pause** | bekukan meter; waktu pause nggak ikut dihitung ke DPS |
| ⌄ **Menu** | isi nama karakter sendiri (kalau belum ke-detect otomatis), mode **transparan**, **pilih folder game**, status capture |

### Otomatis / plug & play
- Adapter jaringan dipilih otomatis dari koneksi game (langsung, ExitLag, VPN), dicek ulang terus — meter boleh dibuka sebelum atau sesudah game.
- Folder game dicari otomatis (registry / lokasi umum), bisa dipilih manual.
- Nama sendiri ke-detect otomatis (dari hit kamu yang juga tersiar ke party); kalau belum, tampil sebagai "Kamu" — bisa diisi manual di Menu.
- Kalau Npcap belum terpasang, muncul petunjuk + link download.

### Belum ada
Crit rate, damage bleed/DoT (sebagian mungkin nyasar ke baris lain), tampilan boss-only otomatis, penyimpanan log/history (semua data cuma di memori — hilang kalau meter ditutup), equip sendiri.

## Pakai (user biasa)

1. Install [Npcap](https://npcap.com/#download) (pilihan default).
2. Download `SealDpsMeter.exe` dari halaman Releases, jalankan (minta hak Administrator — dibutuhkan Npcap).
3. Kalau folder game nggak ketemu otomatis, pilih folder Seal Online (yang berisi `etc\etc.SPAK`) — dipakai untuk nama skill, monster, dan item.

Jalan dengan koneksi langsung, ExitLag (loopback), maupun VPN — adapter dipilih otomatis dari koneksi game.

## Build (kontributor)

Butuh: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Npcap.

```
dotnet build src/DpsMeterUI/DpsMeterUI.csproj
```

Exe hasil build minta hak Administrator (dibutuhkan Npcap) — jalankan dari terminal/IDE yang di-"Run as administrator".

**Rilis:** push tag `v*` (mis. `git tag v0.1.0 && git push origin v0.1.0`) → GitHub Actions otomatis build `SealDpsMeter.exe` (single file, tanpa perlu install .NET) dan melampirkannya di halaman Releases.

Mode development (env var):

| Variabel | Fungsi |
|---|---|
| `DPSMETER_DEMO=1` / `breakdown` / `target` | isi data palsu untuk cek UI tanpa game |
| `DPSMETER_REPLAY=<file.pcap>` | putar ulang capture lewat parser live |
| `DPSMETER_TAB=target` | buka langsung di tab TARGET |

## Struktur

| Folder | Isi |
|---|---|
| `src/DpsMeterUI` | overlay WPF (deliverable utama = `SealDpsMeter.exe`). `CaptureEngine.cs` = parser paket |
| `src/DpsMeter` | versi console lama — **tidak di-update lagi** (belum ada roster party, job, target, dll). Semua fitur baru masuk ke `DpsMeterUI` |
| `tools/PacketCapture` | rekam traffic game ke `.pcap` |
| `tools/PacketAnalyze` | alat reverse-engineering (decode opcode, cari field) |
| `tools/MemoryProbe` | bukti bahwa GameGuard memblokir `OpenProcess` (alasan pakai sniffing) |
| `docs/protocol.md` | format paket yang berlaku sekarang — **baca ini dulu** sebelum ubah parser |
| `docs/research-log.md` | catatan riset kronologis (termasuk teori yang sudah dibatalkan) |

## Protokol (ringkas)

- Yang di-parse cuma pesan server→client (data pertempuran). Pesan client→server terenkripsi dan diabaikan.
- `CC5D` / `F339` auto-attack sendiri, `CEA4` / `CEA6` / `F360` skill sendiri, `CC5E` / `F33C` / `CEA5` auto/skill pemain lain.
- `041C` roster party (nama → entity ID, level, job). `CC1B` karakter di sekitar (nama, job, equip).
- `CC35` / `CC36` entity monster (entity → jenis monster).
- Entity ID **bukan** permanen per karakter — bisa ganti saat masuk dungeon / relog. UI menggabungkan per nama.

Layout lengkap per opcode: [docs/protocol.md](docs/protocol.md).

## Kontribusi

Pull request dan issue dipersilakan. Tolong **jangan commit file `.pcap`** (berisi data akun/karakter) atau file data game — `.gitignore` sudah menolaknya. Untuk perubahan parser, sertakan bukti dari capture (offset + contoh byte) di deskripsi PR.

Area yang masih terbuka: lihat bagian **Belum ada** di atas.

## Kredit

- Ikon job: [game-icons.net](https://game-icons.net) (Lorc, Delapouite) — CC BY 3.0
- Format file game (`.SPAK`, `.edt`): proyek open-source [unsealed](https://github.com/feryandi/unsealed)
- Capture: [SharpPcap](https://github.com/dotpcap/sharppcap), [PacketDotNet](https://github.com/dotpcap/packetnet), [Npcap](https://npcap.com)

## Lisensi

MIT — lihat [LICENSE](LICENSE).
