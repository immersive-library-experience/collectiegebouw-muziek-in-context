"""Give every album in profiles/<p>/source/albums*.json a MusicBrainz release-group id: check the ids
that are there, turn release ids into their release-group, search for the rest.

    python tools/musicbrainz_keys.py profiles/beeldengeluid/source              # check + search, write mb-review.tsv
    python tools/musicbrainz_keys.py profiles/beeldengeluid/source --apply      # patch the sources for the rows marked ok

Statuses in the TSV (column `status`):
    rg-ok        the stored mbid is a release-group and its title matches
    release->rg  the stored mbid was a release; proposal = its release-group (release kept in `release`)
    gevonden     no usable mbid; the search found a match that passes all checks (artist, title, year)
    twijfel      the search found something, but one check is weak: look at it
    geen         nothing found: the album needs a Muziekweb id or an editorial `key`

`--apply` patches only rows whose `ok` column says `ja` (the check/search fills that in for rg-ok,
release->rg and gevonden; edit the file to accept or reject others) and writes an editorial `key`
for every album that still has no mbid and no muziekweb id, so the importer can build. The source
files are patched textually, line by line, so their hand formatting survives.

MusicBrainz asks for one request per second and a User-Agent with contact details.
"""
from __future__ import annotations

import argparse
import csv
import difflib
import json
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # Windows console is cp1252

UA = "CollectionKit/1.0 ( https://github.com/immersive-library-experience )"
MB = "https://musicbrainz.org/ws/2"
PAUSE = 1.1
FILES = ("albums.json", "albums-nl.json")


def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.status, json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 503 and attempt < 2:
                time.sleep(10.0); continue         # the server asks for a breather
            return e.code, None
        except Exception:  # noqa: BLE001
            if attempt < 2:
                time.sleep(5.0); continue
            return -1, None
    return -1, None


def norm(s: str) -> str:
    s = unicodedata.normalize("NFKD", s or "").encode("ascii", "ignore").decode()
    s = s.lower().replace("&", "and")
    return re.sub(r"[^a-z0-9]+", " ", s).strip()


def ratio(a: str, b: str) -> float:
    return difflib.SequenceMatcher(None, norm(a), norm(b)).ratio()


def slug(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", norm(s)).strip("-")


def artist_names(rg) -> list[str]:
    credit = rg.get("artist-credit") or []
    joined = "".join((c.get("name") or "") + (c.get("joinphrase") or "") for c in credit if isinstance(c, dict))
    names = [joined] + [c.get("artist", {}).get("name", "") for c in credit if isinstance(c, dict)]
    return [n for n in names if n]


def year_of(rg) -> int | None:
    d = rg.get("first-release-date") or ""
    return int(d[:4]) if len(d) >= 4 and d[:4].isdigit() else None


def judge(album, rg) -> tuple[str, str]:
    """(verdict, reason) for one candidate release-group against the album."""
    t = ratio(album["title"], rg.get("title", ""))
    a = max((ratio(album["artist"], n) for n in artist_names(rg)), default=0.0)
    y = year_of(rg)
    dy = None if y is None else abs(y - int(album["year"]))
    reasons = []
    if t < 0.85: reasons.append(f"titel {t:.2f}")
    if a < 0.80: reasons.append(f"artiest {a:.2f}")
    if dy is None: reasons.append("jaar onbekend")
    elif dy > 1: reasons.append(f"jaar {y} vs {album['year']}")
    if not reasons:
        return "ok", f"titel {t:.2f} artiest {a:.2f} jaar {y}"
    return "twijfel", "; ".join(reasons)


def load_albums(src: Path):
    out = []
    for name in FILES:
        p = src / name
        if not p.exists():
            continue
        for a in json.loads(p.read_text(encoding="utf-8")):
            nr = a.get("nr", a.get("id"))
            out.append({"file": name, "nr": nr, "title": a["title"], "artist": a["artist"], "year": a["year"],
                        "mbid": (a.get("mbid") or "").strip(), "release": (a.get("release") or "").strip(),
                        "muziekweb": (a.get("muziekweb") or "").strip(), "key": (a.get("key") or "").strip()})
    return out


def check(albums, verbose=True, done=None, on_row=None):
    """done = rows already checked (by file + nr) from an earlier run; on_row is called after every album so a
    run that stops halfway keeps what it had."""
    rows = list(done or [])
    seen = {(r["file"], str(r["nr"])) for r in rows}
    for i, al in enumerate(albums, 1):
        if (al["file"], str(al["nr"])) in seen:
            continue
        row = {"file": al["file"], "nr": al["nr"], "artist": al["artist"], "title": al["title"], "year": al["year"],
               "mbid_nu": al["mbid"], "status": "", "ok": "", "mbid_voorstel": "", "release": al["release"],
               "mb_titel": "", "mb_artiest": "", "mb_jaar": "", "type": "", "score": "", "reden": "", "muziekweb": al["muziekweb"], "key": al["key"]}
        if al["mbid"]:
            st, rg = get(f"{MB}/release-group/{al['mbid']}?inc=artist-credits&fmt=json"); time.sleep(PAUSE)
            if st == 200:
                verdict, reason = judge(al, rg)
                row.update(status="rg-ok" if verdict == "ok" else "twijfel", ok="ja" if verdict == "ok" else "",
                           mbid_voorstel=al["mbid"], mb_titel=rg.get("title", ""), mb_artiest=" / ".join(artist_names(rg)[:1]),
                           mb_jaar=year_of(rg) or "", type=rg.get("primary-type") or "", reden=("mbid is een release-group; " if verdict == "ok" else "release-group met afwijking: ") + reason)
            else:
                st, rel = get(f"{MB}/release/{al['mbid']}?inc=release-groups+artist-credits&fmt=json"); time.sleep(PAUSE)
                if st == 200 and rel.get("release-group"):
                    rg = rel["release-group"]; rg.setdefault("artist-credit", rel.get("artist-credit"))
                    verdict, reason = judge(al, rg)
                    row.update(status="release->rg", ok="ja" if verdict == "ok" else "", mbid_voorstel=rg["id"], release=al["mbid"],
                               mb_titel=rg.get("title", ""), mb_artiest=" / ".join(artist_names(rg)[:1]), mb_jaar=year_of(rg) or "",
                               type=rg.get("primary-type") or "", reden="mbid was een release; " + reason)
                else:
                    row.update(reden=f"mbid onbekend in MusicBrainz (release-group en release: {st}); ")
        if not row["status"]:
            # strict first; then the title over all fields (that includes MusicBrainz aliases: Focus'
            # "Moving Waves" is "Focus II" there); then free text. The judge keeps wrong matches out.
            queries = [f'releasegroup:"{al["title"]}" AND artist:"{al["artist"]}"',
                       f'"{al["title"]}" AND artist:"{al["artist"]}"',
                       f'{al["title"]} {al["artist"]}']
            best = None
            for q in queries:
                st, res = get(f"{MB}/release-group/?query={urllib.parse.quote(q)}&fmt=json&limit=5"); time.sleep(PAUSE)
                cands = (res or {}).get("release-groups", []) if st == 200 else []
                for rg in cands:
                    verdict, reason = judge(al, rg)
                    if best is None or (verdict == "ok" and best[0] != "ok"):
                        best = (verdict, reason, rg)
                    if verdict == "ok":
                        break
                if best is not None and best[0] == "ok":
                    break
            if best is None:
                row.update(status="geen", reden=row["reden"] + "zoeken vond niets")
            else:
                verdict, reason, rg = best
                row.update(status="gevonden" if verdict == "ok" else "twijfel", ok="ja" if verdict == "ok" else "",
                           mbid_voorstel=rg["id"], mb_titel=rg.get("title", ""), mb_artiest=" / ".join(artist_names(rg)[:1]),
                           mb_jaar=year_of(rg) or "", type=rg.get("primary-type") or "", score=rg.get("score", ""), reden=row["reden"] + "zoekresultaat: " + reason)
        if verbose:
            print(f"[{i}/{len(albums)}] nr {al['nr']:>3} {al['artist']} - {al['title']} ({al['year']}): {row['status']} {row['reden']}", flush=True)
        rows.append(row)
        if on_row:
            on_row(rows)
    return rows


def via_releases(r) -> None:
    """The release-group search ranks later editions and compilations above the original; the release
    index carries the date, so ask for a release of the album's own year and take its group."""
    al = {"title": r["title"], "artist": r["artist"], "year": r["year"]}
    year = int(r["year"])
    seen = set()
    for y in (year, year - 1, year + 1):
        q = f'release:"{r["title"]}" AND date:{y} AND artist:"{r["artist"]}"'
        st, res = get(f"{MB}/release/?query={urllib.parse.quote(q)}&fmt=json&limit=10"); time.sleep(PAUSE)
        for rel in (res or {}).get("releases", []) if st == 200 else []:
            rgid = (rel.get("release-group") or {}).get("id")
            if not rgid or rgid in seen or ratio(r["title"], rel.get("title", "")) < 0.85:
                continue
            seen.add(rgid)
            st2, rg = get(f"{MB}/release-group/{rgid}?inc=artist-credits&fmt=json"); time.sleep(PAUSE)
            if st2 != 200:
                continue
            verdict, reason = judge(al, rg)
            print(f"  nr {r['nr']} {r['artist']} - {r['title']} ({year}): {rg['title']} / {(artist_names(rg) or [''])[0]} / {year_of(rg)} -> {verdict} ({reason})", flush=True)
            if verdict == "ok":
                r.update(status="gevonden", ok="ja", mbid_voorstel=rg["id"], release=rel["id"], mb_titel=rg.get("title", ""),
                         mb_artiest=(artist_names(rg) or [""])[0], mb_jaar=year_of(rg) or "", type=rg.get("primary-type") or "",
                         reden=f"via de release van {rel.get('date', '')}: " + reason)
                return
    print(f"  nr {r['nr']} {r['artist']} - {r['title']} ({year}): geen release van {year - 1}-{year + 1} met deze titel", flush=True)


def write_tsv(rows, path: Path):
    with path.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()), delimiter="\t")
        w.writeheader(); w.writerows(rows)


def read_tsv(path: Path):
    with path.open(encoding="utf-8") as fh:
        return list(csv.DictReader(fh, delimiter="\t"))


def patch_source(src: Path, rows):
    """Set mbid/release/key on the album's own lines. albums.json keeps one album per line; albums-nl.json
    one field per line: both are handled by locating the album's `"nr": N` and editing the fields that
    follow it, before the next album starts."""
    changed = {name: 0 for name in FILES}
    for name in FILES:
        p = src / name
        if not p.exists():
            continue
        text = p.read_text(encoding="utf-8")
        for r in [r for r in rows if r["file"] == name]:
            nr = int(r["nr"])
            accepted = r["ok"].strip().lower() == "ja" and r["mbid_voorstel"]
            m = re.search(rf'"(?:nr|id)":\s*{nr}\s*,', text)
            if not m:
                print(f"  nr {nr}: niet gevonden in {name}"); continue
            end = re.search(r'"(?:nr|id)":\s*\d+\s*,', text[m.end():])
            block_end = m.end() + end.start() if end else len(text)
            block = text[m.end():block_end]
            new = block
            if accepted:
                new = re.sub(r'"mbid":\s*"[^"]*"', f'"mbid": "{r["mbid_voorstel"]}"', new, count=1)
                if r["release"] and '"release"' not in new:
                    new = new.replace(f'"mbid": "{r["mbid_voorstel"]}"', f'"mbid": "{r["mbid_voorstel"]}", "release": "{r["release"]}"' if name == "albums.json"
                                      else f'"mbid": "{r["mbid_voorstel"]}",\n    "release": "{r["release"]}"', 1)
            # a stored id that IS a release-group but matches weakly (artist credited as "The Miles Davis
            # Quintet") stays: the row is in the review, the key is at least a real album
            keep_existing = r["status"] == "rg-ok" or r["reden"].startswith("release-group met afwijking")
            if not accepted and not keep_existing and r["mbid_nu"] and f'"mbid": "{r["mbid_nu"]}"' in new:
                # a stored mbid that is not a matching release-group would become a wrong key: clear it,
                # the proposal stays in the review file for the human
                new = re.sub(r'"mbid":\s*"[^"]*"', '"mbid": ""', new, count=1)
                print(f"  nr {nr}: mbid {r['mbid_nu']} gewist ({r['status']}: {r['reden'][:60]})")
            has_mbid = accepted or keep_existing
            if accepted:                                  # an editorial key that served meanwhile goes
                new = re.sub(r',\s*"key":\s*"[^"]*"', '', new, count=1)
            # the key is a stand-in only while NO database has the album: read the other ids from the
            # source itself, the review row may predate a Muziekweb or Discogs id
            if not has_mbid and not re.search(r'"(?:muziekweb|discogs)":\s*"[^"]+"', new) and '"key"' not in new:
                key = slug(f"{r['artist']} {r['title']} {r['year']}")
                new = re.sub(r'("mbid":\s*"[^"]*")', rf'\1, "key": "{key}"' if name == "albums.json" else rf'\1,\n    "key": "{key}"', new, count=1)
            if new != block:
                text = text[:m.end()] + new + text[block_end:]
                changed[name] += 1
        p.write_text(text, encoding="utf-8")
    return changed


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source")
    ap.add_argument("--apply", action="store_true", help="patch the album sources from mb-review.tsv")
    ap.add_argument("--review", default=None, help="path of the review TSV (default <source>/mb-review.tsv)")
    ap.add_argument("--fresh", action="store_true", help="check every album again instead of continuing an existing review file")
    ap.add_argument("--via-releases", action="store_true", help="for rows without ok: look for a release of the album's own year (+/-1) and take its release-group")
    a = ap.parse_args()
    src = Path(a.source)
    review = Path(a.review) if a.review else src / "mb-review.tsv"
    if a.via_releases:
        rows = read_tsv(review)
        todo = [r for r in rows if r["ok"].strip().lower() != "ja"]
        print(f"{len(todo)} rijen zonder akkoord: zoeken via de releases van het jaar zelf")
        for r in todo:
            via_releases(r)
            write_tsv(rows, review)
        counts = {}
        for r in todo:
            counts[r["status"]] = counts.get(r["status"], 0) + 1
        print("uitkomst:", counts)
        return
    if a.apply:
        rows = read_tsv(review)
        changed = patch_source(src, rows)
        print("aangepast:", changed)
        # the sources must still parse
        for name in FILES:
            if (src / name).exists():
                json.loads((src / name).read_text(encoding="utf-8"))
        print("bronbestanden lezen nog als JSON")
        return
    albums = load_albums(src)
    print(f"{len(albums)} albums; {sum(1 for x in albums if x['mbid'])} met mbid")
    done = read_tsv(review) if review.exists() and not a.fresh else []
    if done:
        print(f"{len(done)} albums al gecontroleerd in {review.name}; alleen de rest")
    rows = check(albums, done=done, on_row=lambda rows: write_tsv(rows, review))
    write_tsv(rows, review)
    counts = {}
    for r in rows:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    print("\nuitkomst:", counts)
    print(f"review: {review}  (kolom ok = ja wordt toegepast met --apply)")


if __name__ == "__main__":
    main()
