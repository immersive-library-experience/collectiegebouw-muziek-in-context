"""Give the albums in profiles/<p>/source/albums*.json their Muziekweb album id (EBX0085, JK52668), through
the CDR web service of Muziekweb (api.cdr.nl, v3; account in <source>/muziekweb.config.json, IP-bound).

    python tools/muziekweb_keys.py profiles/beeldengeluid/source              # search, write mw-review.tsv
    python tools/muziekweb_keys.py profiles/beeldengeluid/source --apply      # patch "muziekweb" into the sources

Every album is looked up (popular catalogue first, classical when that finds nothing or the album is
classical): the id gives the album page (https://www.muziekweb.nl/Link/<id>) and the sleeve
(https://media.cdr.nl/COVER/LARGE/FRONT/<id>.jpg), also for albums that have a MusicBrainz key.
A hit is accepted when title, performer and year agree (same rules as tools/musicbrainz_keys.py);
compilations ("beste van") and box sets never automatically. The rest is `twijfel` in the review
file; set `ok` to `ja` there and run --apply again. Sources are patched textually, so their hand
formatting survives. Pacing: two requests per second at most, as the importer did.

API documentation: https://www.muziekweb.nl/Muziekweb/Webservice/WebserviceAPI.php
"""
from __future__ import annotations

import argparse
import base64
import importlib.util
import json
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

_spec = importlib.util.spec_from_file_location("musicbrainz_keys", Path(__file__).with_name("musicbrainz_keys.py"))
mbk = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mbk)                       # norm, ratio, load_albums, read_tsv, write_tsv, FILES

API = "https://api.cdr.nl"
UA = "CollectionKit/1.0 ( https://github.com/immersive-library-experience )"
PAUSE = 0.5


class Cdr:
    def __init__(self, config: Path):
        if not config.exists():
            sys.exit(f"geen {config}: vraag een webservice-account aan via https://www.muziekweb.nl/Muziekweb/Webservice/Aanvragen "
                     "en zet username/password in dat bestand (het staat in .gitignore)")
        cfg = json.loads(config.read_text(encoding="utf-8"))
        self.auth = "Basic " + base64.b64encode(f"{cfg['username']}:{cfg['password']}".encode()).decode()

    def get(self, path: str, **params) -> ET.Element | None:
        url = f"{API}{path}?{urllib.parse.urlencode(params)}"
        req = urllib.request.Request(url, headers={"User-Agent": UA, "Authorization": self.auth})
        for attempt in range(3):
            try:
                with urllib.request.urlopen(req, timeout=30) as r:
                    root = ET.fromstring(r.read())
                if root.get("ErrorCode", "0") != "0":
                    print(f"  CDR: {root.get('ErrorCode')} {root.get('Description')} voor {url}")
                    return None
                return root
            except urllib.error.HTTPError as e:
                if e.code in (429, 503) and attempt < 2:
                    time.sleep(10.0); continue
                print(f"  CDR HTTP {e.code} voor {url}")
                return None
            except Exception as e:  # noqa: BLE001
                if attempt < 2:
                    time.sleep(5.0); continue
                print(f"  CDR mislukt: {e}")
                return None
        return None

    def albums(self, q: str, catalogue: str, count: int = 5) -> list[dict]:
        root = self.get("/Search/v3/SingleSearch/SingleSearch-Paging.xml", q=q, searchFilter=f"{catalogue}album", rangeStart=1, rangeEnd=count)
        time.sleep(PAUSE)
        if root is None:
            return []
        out = []
        for al in root.iter("Album"):
            performers = [p.findtext("PresentationName") or "" for p in al.iter("Performer")]
            out.append({
                "id": al.findtext("AlbumID") or "", "title": al.findtext("AlbumTitle") or "", "performers": performers,
                "date": al.findtext("ReleaseDate") or "", "catalogue": al.findtext("Catalogue") or catalogue.upper(),
                "bestof": al.findtext("IsBestOf") == "1", "collection": al.findtext("IsCollection") == "1",
                "cover": (al.find("Cover") is not None and al.find("Cover").get("HasCover") == "1"),
                "score": al.get("Score", ""),
            })
        return out


def judge(album: dict, hit: dict) -> tuple[str, str]:
    """Muziekweb lists EDITIONS: the date is that of the CD or LP in the catalogue, so a later year is a
    reissue (fine for page and sleeve), an earlier one cannot be this album. A subtitle after " : "
    ("Deluxe edition") does not count against the title."""
    title = re.sub(r"\s*[\[(][^\])]*[\])]", "", hit["title"].split(" : ")[0])        # "[+ extra tracks]", "(remastered)"
    ours = album["title"]
    if ":" in ours and hit["catalogue"].startswith("CLASSICAL"):
        ours = ours.split(":", 1)[1].strip()                                       # "Vivaldi: The Four Seasons" -> the work
    t = max(mbk.ratio(album["title"], title), mbk.ratio(ours, title))
    performers = [re.sub(r"\s*\[[^\]]*\]", "", p) for p in hit["performers"]]      # "Ali B [NL]"
    names = list(performers)
    if len(performers) >= 2:
        names += [f"{performers[0]} & {performers[1]}", f"{performers[0]} and {performers[1]}", f"{performers[1]} & {performers[0]}"]
    a = max((mbk.ratio(album["artist"], p) for p in names), default=0.0)
    y = int(hit["date"][:4]) if hit["date"][:4].isdigit() else None
    reasons = []
    if t < 0.85: reasons.append(f"titel {t:.2f}")
    if a < 0.80: reasons.append(f"artiest {a:.2f}")
    if y is None: reasons.append("jaar onbekend")
    elif y < int(album["year"]) - 1: reasons.append(f"jaar {y} ligt voor {album['year']}")
    if hit["bestof"] or hit["collection"]: reasons.append("verzamelaar")
    if not reasons:
        return "ok", f"titel {t:.2f} artiest {a:.2f} uitgave {y}" + (" (heruitgave)" if y and y > int(album["year"]) + 1 else "")
    return "twijfel", "; ".join(reasons)


def search(cdr: Cdr, album: dict) -> tuple[str, str, dict | None]:
    classical = album.get("subgenre", "") in ("klassiek", "classical") or album.get("themalijn", "") == "klassiek"
    order = ["classical", "popular"] if classical else ["popular", "classical"]
    best = None
    for catalogue in order:
        for q in (f"{album['artist']} {album['title']}", album["title"]):
            for hit in cdr.albums(q, catalogue):
                verdict, reason = judge(album, hit)
                if best is None or (verdict == "ok" and best[0] != "ok"):
                    best = (verdict, reason, hit)
                if verdict == "ok":
                    return best
    return best or ("geen", "niets gevonden", None)


def check(cdr: Cdr, albums: list[dict], done: list[dict], on_row) -> list[dict]:
    rows = list(done)
    seen = {(r["file"], str(r["nr"])) for r in rows}
    for i, al in enumerate(albums, 1):
        if (al["file"], str(al["nr"])) in seen:
            continue
        verdict, reason, hit = search(cdr, al)
        row = {"file": al["file"], "nr": al["nr"], "artist": al["artist"], "title": al["title"], "year": al["year"],
               "muziekweb_nu": al["muziekweb"], "status": "gevonden" if verdict == "ok" else verdict, "ok": "ja" if verdict == "ok" else "",
               "muziekweb_voorstel": hit["id"] if hit else "", "mw_titel": hit["title"] if hit else "",
               "mw_artiest": " / ".join(hit["performers"][:2]) if hit else "", "mw_jaar": hit["date"][:4] if hit else "",
               "catalogus": hit["catalogue"] if hit else "", "hoes": ("ja" if hit and hit["cover"] else "nee") if hit else "", "reden": reason}
        if al["muziekweb"] and hit and hit["id"] == al["muziekweb"]:
            row.update(status="al-bekend", ok="ja", reden="bron had dit id al; " + reason)
        print(f"[{i}/{len(albums)}] nr {al['nr']:>3} {al['artist']} - {al['title']} ({al['year']}): {row['status']} "
              f"{row['muziekweb_voorstel']} {row['mw_artiest']} - {row['mw_titel']} ({row['mw_jaar']}) {reason}", flush=True)
        rows.append(row)
        on_row(rows)
    return rows


def patch_source(src: Path, rows: list[dict]) -> dict:
    """Set "muziekweb" on the album's own lines (after "mbid", else after "key"), only for rows marked ok."""
    changed = {name: 0 for name in mbk.FILES}
    for name in mbk.FILES:
        p = src / name
        if not p.exists():
            continue
        text = p.read_text(encoding="utf-8")
        multiline = name != "albums.json"
        for r in [r for r in rows if r["file"] == name and r["ok"].strip().lower() == "ja" and r["muziekweb_voorstel"]]:
            nr = int(r["nr"])
            m = re.search(rf'"nr":\s*{nr}\s*,', text)
            if not m:
                print(f"  nr {nr}: niet gevonden in {name}"); continue
            end = re.search(r'"nr":\s*\d+\s*,', text[m.end():])
            block_end = m.end() + end.start() if end else len(text)
            block = text[m.end():block_end]
            new = block
            if re.search(r'"muziekweb":\s*"[^"]*"', new):
                new = re.sub(r'"muziekweb":\s*"[^"]*"', f'"muziekweb": "{r["muziekweb_voorstel"]}"', new, count=1)
            else:
                anchor = re.search(r'("mbid":\s*"[^"]*")', new) or re.search(r'("key":\s*"[^"]*")', new)
                if anchor is None:
                    print(f"  nr {nr}: geen mbid- of key-veld om achter te zetten"); continue
                insert = f'{anchor.group(1)},\n    "muziekweb": "{r["muziekweb_voorstel"]}"' if multiline else f'{anchor.group(1)}, "muziekweb": "{r["muziekweb_voorstel"]}"'
                new = new[:anchor.start()] + insert + new[anchor.end():]
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
    ap.add_argument("--review", default=None, help="review TSV (default <source>/mw-review.tsv)")
    ap.add_argument("--fresh", action="store_true", help="look every album up again")
    a = ap.parse_args()
    src = Path(a.source)
    review = Path(a.review) if a.review else src / "mw-review.tsv"
    if a.apply:
        print("aangepast:", patch_source(src, mbk.read_tsv(review)))
        return
    cdr = Cdr(src / "muziekweb.config.json")
    albums = []
    for name in mbk.FILES:
        p = src / name
        if p.exists():
            for x in json.loads(p.read_text(encoding="utf-8")):
                albums.append({"file": name, "nr": x.get("nr", x.get("id")), "title": x["title"], "artist": x["artist"], "year": x["year"],
                               "subgenre": x.get("subgenre", ""), "themalijn": x.get("themalijn", ""), "muziekweb": (x.get("muziekweb") or "").strip()})
    done = mbk.read_tsv(review) if review.exists() and not a.fresh else []
    if done:
        print(f"{len(done)} albums al opgezocht in {review.name}; alleen de rest")
    rows = check(cdr, albums, done, on_row=lambda rows: mbk.write_tsv(rows, review))
    mbk.write_tsv(rows, review)
    counts: dict[str, int] = {}
    for r in rows:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    print("\nuitkomst:", counts)
    print(f"review: {review}  (kolom ok = ja wordt toegepast met --apply)")


if __name__ == "__main__":
    main()
