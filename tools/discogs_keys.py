"""Give albums in profiles/<p>/source/albums*.json a Discogs id, for the albums that MusicBrainz or Muziekweb
does not have: `m<master id>` (the Discogs "master" groups the editions, like a MusicBrainz release-group)
or, when Discogs has no master, `r<release id>`.

    python tools/discogs_keys.py profiles/beeldengeluid/source              # albums missing a MusicBrainz or Muziekweb id
    python tools/discogs_keys.py profiles/beeldengeluid/source --all        # every album
    python tools/discogs_keys.py profiles/beeldengeluid/source --apply      # patch "discogs" into the sources

The bundle id becomes album:dc:m181126 when the album has neither a MusicBrainz nor a Muziekweb id, and
the importer fetches the sleeve from Discogs when the Cover Art Archive and Muziekweb have none, so an
album that does have a MusicBrainz key still benefits from a Discogs id (the sleeve, the album page).
Judged like the other key tools: title >= 0.85, artist >= 0.80, year of a master within a year, year of
a release not earlier than the album's; compilations never automatically. The rest is `twijfel` in
dc-review.tsv (gitignored): put the right id in `discogs_voorstel`, `ok` = ja, run --apply.

Discogs API (https://www.discogs.com/developers): search and details work without a token at 25
requests per minute; a personal token in <source>/discogs.config.json ({"token": "..."}, gitignored)
raises that to 60. Discogs data is CC0; the images are not — check the API terms before a public use.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

_spec = importlib.util.spec_from_file_location("musicbrainz_keys", Path(__file__).with_name("musicbrainz_keys.py"))
mbk = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mbk)                       # norm, ratio, read_tsv, write_tsv, FILES

API = "https://api.discogs.com"
UA = "CollectionKit/1.0 +https://github.com/immersive-library-experience"


class Discogs:
    def __init__(self, config: Path):
        self.token = ""
        if config.exists():
            self.token = json.loads(config.read_text(encoding="utf-8")).get("token", "")
        self.pause = 1.1 if self.token else 2.6     # 60 or 25 requests per minute

    def get(self, path: str, **params) -> dict | None:
        url = f"{API}{path}" + (f"?{urllib.parse.urlencode(params)}" if params else "")
        headers = {"User-Agent": UA}
        if self.token:
            headers["Authorization"] = f"Discogs token={self.token}"
        for attempt in range(3):
            try:
                with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as r:
                    data = json.loads(r.read())
                time.sleep(self.pause)
                return data
            except urllib.error.HTTPError as e:
                if e.code == 429 and attempt < 2:
                    time.sleep(30.0); continue
                if e.code != 404:
                    print(f"  Discogs HTTP {e.code} voor {url}")
                return None
            except Exception as e:  # noqa: BLE001
                if attempt < 2:
                    time.sleep(5.0); continue
                print(f"  Discogs mislukt: {e}")
                return None
        return None

    def search(self, kind: str, **params) -> list[dict]:
        data = self.get("/database/search", type=kind, per_page=8, **params)
        return data.get("results", []) if data else []


def split_title(result: dict) -> tuple[str, str]:
    """Discogs writes 'Artist - Title'; artists carry a '(2)' when the name is not unique."""
    artist, _, title = result.get("title", "").partition(" - ")
    return re.sub(r"\s*\(\d+\)", "", artist).strip(), title.strip()


def judge(album: dict, hit: dict) -> tuple[str, str]:
    artist, title = split_title(hit)
    ours = album["title"]
    if ":" in ours and album.get("classical"):
        ours = ours.split(":", 1)[1].strip()
    t = max(mbk.ratio(album["title"], title), mbk.ratio(ours, title))
    a = mbk.ratio(album["artist"], artist)
    y = int(hit["year"]) if str(hit.get("year", "")).isdigit() else None
    reasons = []
    if t < 0.85: reasons.append(f"titel {t:.2f}")
    if a < 0.80: reasons.append(f"artiest {a:.2f}")
    if y is None: reasons.append("jaar onbekend")
    elif hit["type"] == "master" and abs(y - int(album["year"])) > 1: reasons.append(f"jaar {y} vs {album['year']}")
    elif y < int(album["year"]) - 1: reasons.append(f"jaar {y} ligt voor {album['year']}")
    if "Compilation" in hit.get("format", []): reasons.append("verzamelaar")
    if not reasons:
        return "ok", f"titel {t:.2f} artiest {a:.2f} jaar {y}" + (" (latere uitgave)" if y and y > int(album["year"]) + 1 else "")
    return "twijfel", "; ".join(reasons)


def proposal(hit: dict) -> str:
    if hit["type"] == "master":
        return f"m{hit['id']}"
    return f"m{hit['master_id']}" if hit.get("master_id") else f"r{hit['id']}"


def search(dc: Discogs, album: dict) -> tuple[str, str, dict | None]:
    best = None
    queries = [dict(artist=album["artist"], release_title=album["title"]), dict(q=f"{album['artist']} {album['title']}")]
    for kind in ("master", "release"):
        for params in queries:
            for hit in dc.search(kind, **params):
                verdict, reason = judge(album, hit)
                if best is None or (verdict == "ok" and best[0] != "ok"):
                    best = (verdict, reason, hit)
                if verdict == "ok":
                    return best
    return best or ("geen", "niets gevonden", None)


def check(dc: Discogs, albums: list[dict], done: list[dict], on_row) -> list[dict]:
    rows = list(done)
    seen = {(r["file"], str(r["nr"])) for r in rows}
    for i, al in enumerate(albums, 1):
        if (al["file"], str(al["nr"])) in seen:
            continue
        verdict, reason, hit = search(dc, al)
        artist, title = split_title(hit) if hit else ("", "")
        row = {"file": al["file"], "nr": al["nr"], "artist": al["artist"], "title": al["title"], "year": al["year"],
               "discogs_nu": al["discogs"], "status": "gevonden" if verdict == "ok" else verdict, "ok": "ja" if verdict == "ok" else "",
               "discogs_voorstel": proposal(hit) if hit else "", "dc_titel": title, "dc_artiest": artist,
               "dc_jaar": str(hit.get("year", "")) if hit else "", "dc_type": hit["type"] if hit else "",
               "dc_formaat": " ".join(hit.get("format", [])) if hit else "", "dc_land": hit.get("country", "") if hit else "",
               "reden": reason, "url": f"https://www.discogs.com/{hit['type']}/{hit['id']}" if hit else ""}
        if al["discogs"] and hit and proposal(hit) == al["discogs"]:
            row.update(status="al-bekend", ok="ja", reden="bron had dit id al; " + reason)
        print(f"[{i}/{len(albums)}] nr {al['nr']:>3} {al['artist']} - {al['title']} ({al['year']}): {row['status']} "
              f"{row['discogs_voorstel']} {artist} - {title} ({row['dc_jaar']}, {row['dc_formaat']}) {reason}", flush=True)
        rows.append(row)
        on_row(rows)
    return rows


def patch_source(src: Path, rows: list[dict]) -> dict:
    """Set "discogs" on the album's own lines (after "muziekweb", else "mbid", else "key"), only for rows marked ok.
    An editorial key that only served while no database had the album goes when the Discogs id takes over."""
    changed = {name: 0 for name in mbk.FILES}
    for name in mbk.FILES:
        p = src / name
        if not p.exists():
            continue
        text = p.read_text(encoding="utf-8")
        multiline = name != "albums.json"
        for r in [r for r in rows if r["file"] == name and r["ok"].strip().lower() == "ja" and r["discogs_voorstel"]]:
            nr = int(r["nr"]); value = r["discogs_voorstel"].strip()
            if not re.fullmatch(r"[mr]\d+", value):
                print(f"  nr {nr}: discogs_voorstel {value!r} is geen m<id> of r<id>"); continue
            m = re.search(rf'"nr":\s*{nr}\s*,', text)
            if not m:
                print(f"  nr {nr}: niet gevonden in {name}"); continue
            end = re.search(r'"nr":\s*\d+\s*,', text[m.end():])
            block_end = m.end() + end.start() if end else len(text)
            block = text[m.end():block_end]
            new = block
            if re.search(r'"discogs":\s*"[^"]*"', new):
                new = re.sub(r'"discogs":\s*"[^"]*"', f'"discogs": "{value}"', new, count=1)
            else:
                anchor = (re.search(r'("muziekweb":\s*"[^"]*")', new) or re.search(r'("mbid":\s*"[^"]*")', new)
                          or re.search(r'("key":\s*"[^"]*")', new))
                if anchor is None:
                    print(f"  nr {nr}: geen muziekweb-, mbid- of key-veld om achter te zetten"); continue
                insert = f'{anchor.group(1)},\n    "discogs": "{value}"' if multiline else f'{anchor.group(1)}, "discogs": "{value}"'
                new = new[:anchor.start()] + insert + new[anchor.end():]
            if not re.search(r'"mbid":\s*"[^"]+"', new) and not re.search(r'"muziekweb":\s*"[^"]+"', new):
                new = re.sub(r',\s*"key":\s*"[^"]*"', '', new, count=1)
            if new != block:
                text = text[:m.end()] + new + text[block_end:]
                changed[name] += 1
        p.write_text(text, encoding="utf-8")
        json.loads(p.read_text(encoding="utf-8"))   # must still parse
    return changed


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--all", action="store_true", help="look every album up, not only those without a MusicBrainz or Muziekweb id")
    ap.add_argument("--review", default=None, help="review TSV (default <source>/dc-review.tsv)")
    ap.add_argument("--fresh", action="store_true", help="look every selected album up again")
    a = ap.parse_args()
    src = Path(a.source)
    review = Path(a.review) if a.review else src / "dc-review.tsv"
    if a.apply:
        print("aangepast:", patch_source(src, mbk.read_tsv(review)))
        return
    dc = Discogs(src / "discogs.config.json")
    albums = []
    for name in mbk.FILES:
        p = src / name
        if p.exists():
            for x in json.loads(p.read_text(encoding="utf-8")):
                if not a.all and (x.get("mbid") or "").strip() and (x.get("muziekweb") or "").strip():
                    continue
                albums.append({"file": name, "nr": x.get("nr", x.get("id")), "title": x["title"], "artist": x["artist"], "year": x["year"],
                               "classical": x.get("subgenre", "") in ("klassiek", "classical") or x.get("themalijn", "") == "klassiek",
                               "discogs": (x.get("discogs") or "").strip()})
    done = mbk.read_tsv(review) if review.exists() and not a.fresh else []
    if done:
        print(f"{len(done)} albums al opgezocht in {review.name}; alleen de rest")
    print(f"{len(albums)} albums opzoeken bij Discogs ({'met token' if dc.token else 'zonder token, 25 per minuut'})")
    rows = check(dc, albums, done, on_row=lambda rows: mbk.write_tsv(rows, review))
    mbk.write_tsv(rows, review)
    counts: dict[str, int] = {}
    for r in rows:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    print("\nuitkomst:", counts)
    print(f"review: {review}  (kolom ok = ja wordt toegepast met --apply)")


if __name__ == "__main__":
    main()
