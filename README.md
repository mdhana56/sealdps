# Seal DPS Meter

DPS meter overlay untuk **Seal Online** (gaya LOA Logs): damage, DPS, D%, jumlah hit per anggota party, breakdown per skill, damage per monster (tab TARGET), badge job, dan equip pemain lain.

**Pasif sepenuhnya** — meter cuma *membaca* paket jaringan server→client lewat Npcap. Tidak inject, tidak baca/tulis memori game, tidak mengirim paket apa pun.

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
- Nama sendiri ke-detect otomatis dari data party; kalau belum, tampil sebagai "Kamu".
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

```bat
run-dps-meter-ui.bat      :: build + jalankan overlay WPF (self-elevate ke admin)
publish.bat               :: bikin dist\SealDpsMeter.exe (single file, self-contained)
```

Mode development (env var):

| Variabel | Fungsi |
|---|---|
| `DPSMETER_DEMO=1` / `breakdown` / `target` | isi data palsu untuk cek UI tanpa game |
| `DPSMETER_REPLAY=<file.pcap>` | putar ulang capture lewat parser live |
| `DPSMETER_TAB=target` | buka langsung di tab TARGET |

## Struktur

| Folder | Isi |
|---|---|
| `src/DpsMeterUI` | overlay WPF (deliverable utama). `CaptureEngine.cs` = parser paket |
| `src/DpsMeter` | versi console |
| `tools/PacketCapture` | rekam traffic game ke `.pcap` |
| `tools/PacketAnalyze` | alat reverse-engineering (decode opcode, cari field) |
| `tools/MemoryProbe` | bukti bahwa GameGuard memblokir `OpenProcess` (alasan pakai sniffing) |
| `docs/` | catatan protokol: `capture-log.md`, `skills-log.md` — **baca ini dulu** sebelum ubah parser |

## Protokol (ringkas)

- Paket server→client tidak terenkripsi; client→server terenkripsi (cuma header terbaca).
- `0xCC5D` auto-attack sendiri, `0xCEA4` skill sendiri, `0xCC5E` / `0xCEA5` auto/skill pemain lain.
- `0x041C` roster party (nama → entity ID, level, job). `0xCC1B` broadcast karakter di sekitar (nama, job, equip).
- `0xCC35` entity list (entity → jenis monster).
- Entity ID **bukan** permanen per karakter — bisa ganti saat masuk dungeon / relog. UI menggabungkan per nama.

Detail lengkap dan riwayat temuan ada di `docs/`.

## Kontribusi

Pull request dan issue dipersilakan. Tolong **jangan commit file `.pcap`** (berisi data akun/karakter) atau file data game — `.gitignore` sudah menolaknya. Untuk perubahan parser, sertakan bukti dari capture (offset + contoh byte) di deskripsi PR.

Area yang masih terbuka: lihat bagian **Belum ada** di atas.

## Kredit

- Ikon job: [game-icons.net](https://game-icons.net) (Lorc, Delapouite) — CC BY 3.0
- Format file game (`.SPAK`, `.edt`): proyek open-source [unsealed](https://github.com/feryandi/unsealed)
- Capture: [SharpPcap](https://github.com/dotpcap/sharppcap), [PacketDotNet](https://github.com/dotpcap/packetnet), [Npcap](https://npcap.com)

Proyek fan-made, tidak berafiliasi dengan publisher Seal Online. Gunakan dengan risiko sendiri.

## Lisensi

MIT — lihat [LICENSE](LICENSE).
