"""Does a bundle keep its promises about identity? (docs/stabiele-ids.md)

    python tools/check_bundle_ids.py profiles/beeldengeluid/data

Checks: every id unique; ids of the expected shape per kind (albums album:mb:|mw:|dc:|x:, Delpher news
krant:kb:<urn>); no id that carries a bundle position; every link target present; every mediaFile on
disk; manifest.itemsHash equals the hash of the ordered ids and td/items.tsv carries the same hash on
every row (schema 1.1); curated.json names only ids that exist; and it counts what is still on an
editorial key or a search URL. Exit code 1 when a hard check fails, so it can sit in a build script.
"""
from __future__ import annotations

import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path

ALBUM = re.compile(r"^album:(mb:[0-9a-f-]{36}|mw:[A-Z]{2,3}[A-Z0-9]*\d+|dc:[mr]\d+|x:[a-z0-9-]+)$")
KB = re.compile(r"^krant:kb:[A-Za-z0-9]+:\d+:mpeg21:a\d+$")
EDITORIAL = re.compile(r"^(movement|news|albumnews):\d{4}:[^\W_]+$")   # slug: letters and digits, accents included


def main() -> int:
    bundle = Path(sys.argv[1])
    items = json.loads((bundle / "items.json").read_text(encoding="utf-8"))
    ids = [i["id"] for i in items]
    problems: list[str] = []
    dup = [k for k, c in Counter(ids).items() if c > 1]
    if dup:
        problems.append(f"dubbele ids: {dup[:5]}")
    known = set(ids)
    kinds = Counter(i["kind"] for i in items)
    shapes: Counter = Counter()
    for it in items:
        i = it["id"]
        if it["kind"] == "album":
            if not ALBUM.match(i):
                problems.append(f"album-id niet van de vorm album:mb|mw|dc|x: {i}")
            else:
                shapes[i.split(":")[1]] += 1
        elif it["kind"] == "news" and it.get("url", "").startswith("http://resolver.kb.nl"):
            if not KB.match(i):
                problems.append(f"Delpher-item zonder KB-urn als id: {i}")
        elif it["kind"] in ("news", "movement") and not (KB.match(i) or EDITORIAL.match(i)):
            problems.append(f"onverwachte id-vorm: {i}")
        if re.search(r":\d+$", i) and i.startswith(("delpher:", "delpherevent:")):
            problems.append(f"positie in de id: {i}")
        for link in it.get("links", []):
            if link["targetId"] not in known:
                problems.append(f"{i} verwijst naar onbekend item {link['targetId']}")
        mf = it.get("mediaFile") or ""
        if mf and not (bundle / "media" / mf).exists():
            problems.append(f"{i}: mediaFile {mf} ontbreekt")
    # schema 1.1: the manifest and the TSV must belong to THIS items.json, not merely have as many rows
    items_hash = hashlib.sha256("\n".join(ids).encode("utf-8")).hexdigest()[:32]
    manifest = json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("itemsHash", "") != items_hash:
        problems.append(f"manifest.itemsHash {manifest.get('itemsHash', '(leeg)')} is niet de hash van items.json ({items_hash})")
    tsv = bundle / "td" / "items.tsv"
    if tsv.exists():
        lines = tsv.read_text(encoding="utf-8").splitlines()
        header = lines[0].split("\t")
        if "bundle" not in header:
            problems.append("td/items.tsv heeft geen kolom bundle")
        else:
            col = header.index("bundle"); idcol = header.index("id")
            rows = [l.split("\t") for l in lines[1:] if l]
            if len(rows) != len(items):
                problems.append(f"td/items.tsv heeft {len(rows)} rijen, items.json {len(items)}")
            elif any(r[col] != items_hash for r in rows) or [r[idcol] for r in rows] != ids:
                problems.append("td/items.tsv hoort niet bij deze items.json (hash of id-volgorde wijkt af)")
            layout_cols = [k for k, h in enumerate(header) if h.endswith(("_x", "_y"))]
            if any("," in r[k] for r in rows for k in layout_cols):
                problems.append("td/items.tsv: decimale komma in de layoutkolommen")
    curated_path = bundle / "curated.json"
    if curated_path.exists():
        curated = json.loads(curated_path.read_text(encoding="utf-8"))
        if curated and isinstance(curated[0], int):
            problems.append("curated.json bevat nog indices (schema 1.0); bouw de bundel opnieuw")
        else:
            missing = [c for c in curated if c not in known]
            if missing:
                problems.append(f"curated.json noemt onbekende ids: {missing[:5]}")
    search_urls = sum(1 for i in items if i["kind"] == "album" and "Search?search=" in (i.get("url") or ""))
    no_cover = [i["id"] for i in items if i["kind"] == "album" and not i.get("mediaFile")]
    print(f"{len(items)} items: {dict(kinds)}")
    print(f"album-sleutels: {dict(shapes)}; albums met zoek-URL: {search_urls}; albums zonder hoes: {len(no_cover)} {no_cover[:6]}")
    for p in problems[:20]:
        print("FOUT:", p)
    if len(problems) > 20:
        print(f"... en {len(problems) - 20} meer")
    print("OK" if not problems else f"{len(problems)} problemen")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
