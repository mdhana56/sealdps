"""Bangun halaman web pencarian item (exports/item-codex.html) dari data game.

Pakai decoder yang sama dengan export_items.py (etc.SPAK -> item.edt, ItemString.edt,
set_opt.edt), lalu tanam datanya sebagai JSON ringkas ke template HTML
(tools/item_codex_template.html, placeholder __DATA__).

Usage: python tools/build_item_web.py
"""
import json
import re
import struct
import zipfile
from pathlib import Path

import export_items as ex

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = Path(__file__).resolve().parent / "item_codex_template.html"
OUT = ROOT / "exports" / "item-codex.html"

# Kolom item.edt yang ditampilkan: (kunci JSON, indeks kolom).
# Terverifikasi 2026-09-26 dengan tooltip in-game Einhorn's Mythril Normal helmet XG +12:
# c5 fame, c6/c12/c17 damage/MPW/defense (+ refine), c9/c20 DI/DD %, c23 attack speed,
# c25 accuracy, c27 critical, c31 moving speed (BUKAN luck), c37/c39 HP/AP increase,
# c74 mask job. c3 = level dasar? (tooltip XG 261 vs c3 252 - belum pasti).
STATS = [
    ("type", 1), ("lvl", 3), ("fame", 5), ("dmg", 6), ("mpw", 12), ("def", 17),
    ("di", 9), ("dd", 20), ("crit", 27), ("acc", 25), ("eva", 29), ("hp", 37), ("ap", 39),
    ("aspd", 23), ("move", 31), ("set", 33), ("price", 35), ("up", 51), ("jobs", 74),
    ("str", 61), ("agi", 63), ("int", 65), ("sta", 15), ("wis", 69),
]

# Kode tipe item (kolom c1) -> (kelompok, label). Disusun dari contoh nama item per
# kode (2026-09-26), BUKAN dari dokumentasi resmi. Kode yang tidak ada di sini = Misc.
TYPE_MAP = {
    4: ("Senjata", "Pedang"), 6: ("Senjata", "Senjata 2 tangan"), 7: ("Senjata", "Belati"),
    8: ("Senjata", "Palu / Scythe"), 9: ("Senjata", "Mace"), 11: ("Senjata", "Tongkat"),
    29: ("Senjata", "Senjata Berserker"), 30: ("Senjata", "Senjata Swordmaster"),
    31: ("Senjata", "Senjata Renegade"), 32: ("Senjata", "Senjata Defender"),
    33: ("Senjata", "Senjata Assassin (kanan/kiri)"), 34: ("Senjata", "Senjata Gambler"),
    35: ("Senjata", "Senjata Ice Wizard"), 36: ("Senjata", "Senjata Fire Wizard"),
    37: ("Senjata", "Senjata Templar"), 38: ("Senjata", "Senjata Apostle"),
    39: ("Senjata", "Senjata Demolitionist"), 40: ("Senjata", "Senjata Artisan"),
    46: ("Senjata", "Ketapel"), 47: ("Senjata", "Senjata Hunter"), 48: ("Senjata", "Senjata Gunner"),
    51: ("Senjata", "Senjata (tipe 51)"), 52: ("Senjata", "Senjata (tipe 52)"),
    53: ("Senjata", "Senjata tangan kanan/kiri (tipe 53)"),
    14: ("Armor", "Perisai"), 15: ("Armor", "Baju atas"), 16: ("Armor", "Baju atas"),
    17: ("Armor", "Helm"), 18: ("Armor", "Helm / ikat kepala"), 19: ("Armor", "Sepatu"),
    20: ("Armor", "Sepatu"), 25: ("Armor", "Baju bawah"), 26: ("Armor", "Baju bawah"),
    12: ("Kostum", "Kostum"), 13: ("Kostum", "Kostum"), 64: ("Kostum", "Kostum clone"), 81: ("Kostum", "Aura"),
    21: ("Aksesoris", "Sayap / sarung tangan"), 44: ("Aksesoris", "Cincin / kalung"),
    50: ("Aksesoris", "Spirit stone"), 22: ("Pet", "Pet"),
    1: ("Misc", "Konsumsi"), 3: ("Misc", "Ramuan / tiket"), 27: ("Misc", "Manual"), 41: ("Misc", "Kotak"),
    42: ("Misc", "Kunci"), 24: ("Misc", "Item quest / event"),
}
KEEP_GROUPS = {"Senjata", "Armor", "Aksesoris"}


def main():
    zf = zipfile.ZipFile(ex.SPAK)
    pw = ex.spak_password(int(re.search(rb"Seal Online Zip v(\d+)", zf.comment).group(1)))
    with open(ex.SPAK, "rb") as fp:
        print("decode item.edt / ItemString.edt / set_opt.edt ...")
        items = ex.edt_decode(ex.read_entry(zf, fp, pw, "item.edt"))
        strs = ex.edt_decode(ex.read_entry(zf, fp, pw, "ItemString.edt"))
        set_opt = ex.edt_decode(ex.read_entry(zf, fp, pw, "set_opt.edt"))

    n_items, n_fields = struct.unpack_from("<ii", items, 64)
    width = (len(items) - 72) // n_items
    n_str = struct.unpack_from("<i", strs, 64)[0]
    sw = (len(strs) - 72) // n_str
    names = {}
    for r in range(n_str):
        b = 72 + r * sw
        iid = struct.unpack_from("<i", strs, b)[0]
        nm = ex.decode_text(strs[b + 4:b + 264].split(b"\0")[0]).strip()
        desc = ex.clean_text(ex.decode_text(strs[b + 264:b + sw].split(b"\0")[0]))
        names[iid] = (nm, desc)

    rows = [struct.unpack_from(f"<{n_fields}i", items, 72 + r * width) for r in range(n_items)]

    type_codes = {v[1] for v in rows}
    types = {str(t): list(TYPE_MAP.get(t, ("Misc", f"Tipe {t}"))) for t in type_codes}

    descs, desc_index, out_items = [], {}, []
    for v in rows:
        nm, desc = names.get(v[0], ("", ""))
        if not nm or nm == "a":
            continue
        # Cuma perlengkapan: Senjata, Armor, Aksesoris (Misc/Kostum/Pet tidak ditampilkan).
        if TYPE_MAP.get(v[1], ("Misc",))[0] not in KEEP_GROUPS:
            continue
        # Nama Korea = item yang tidak diterjemahkan di client ini (umumnya item test/GM).
        if re.search(r"[가-힯]", nm):
            continue
        if desc not in desc_index:
            desc_index[desc] = len(descs)
            descs.append(desc)
        out_items.append([v[0], nm, desc_index[desc]] + [v[c] for _, c in STATS])

    so_count, so_fields = struct.unpack_from("<ii", set_opt, 64)
    so_w = (len(set_opt) - 72) // so_count
    sets = {}
    for r in range(so_count):
        s = struct.unpack_from(f"<{so_fields}i", set_opt, 72 + r * so_w)
        if s[0]:
            sets.setdefault(s[0], []).append(list(s[1:]))

    data = {
        "fields": ["id", "name", "desc"] + [k for k, _ in STATS],
        "items": out_items,
        "descs": descs,
        "sets": sets,
        "types": types,
        "source": f"{ex.SPAK.name}: item.edt ({n_items} baris), ItemString.edt, set_opt.edt ({so_count} baris)",
    }
    blob = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    html = TEMPLATE.read_text(encoding="utf-8").replace("__DATA__", blob.replace("</", "<\\/"))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(html, encoding="utf-8")
    print(f"{len(out_items)} item, {len(descs)} deskripsi unik, {len(sets)} set -> {OUT} ({OUT.stat().st_size / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
