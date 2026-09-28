# Seal DPS Meter

DPS meter overlay untuk **Seal Online** (gaya LOA Logs): damage, DPS, D%, jumlah hit per anggota party, breakdown per skill, damage per monster (tab TARGET), badge job, dan equip pemain lain.

**Pasif sepenuhnya** — meter cuma *membaca* paket jaringan server→client lewat Npcap. Tidak inject, tidak baca/tulis memori game, tidak mengirim paket apa pun.

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

Area yang masih terbuka: crit rate, damage bleed/DoT, tampilan boss-only, log/history sesi.

## Kredit

- Ikon job: [game-icons.net](https://game-icons.net) (Lorc, Delapouite) — CC BY 3.0
- Format file game (`.SPAK`, `.edt`): proyek open-source [unsealed](https://github.com/feryandi/unsealed)
- Capture: [SharpPcap](https://github.com/dotpcap/sharppcap), [PacketDotNet](https://github.com/dotpcap/packetnet), [Npcap](https://npcap.com)

Proyek fan-made, tidak berafiliasi dengan publisher Seal Online. Gunakan dengan risiko sendiri.

## Lisensi

MIT — lihat [LICENSE](LICENSE).
