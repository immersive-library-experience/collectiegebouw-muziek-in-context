#!/usr/bin/env python3
"""The newspaper's title for every Delpher article in the B&G sources, from Delpher itself.

The SRU record of an article does not always carry the paper's title (the album pass of
CollectionKit.Import.Delpher fell back to the publisher: "Stichting Algemeen Dagblad", "Nieuwenhuis",
"[s.n.]"). Delpher's own article view does: its <title> reads "<krant> » 27 aug 1959 - Art. 116 | Delpher".
This script asks that page for every hit in news-delpher.json and news-delpher-events.json and
writes the title into "Paper" — tidied like the importer does (no cataloguing tails after " : ").

    python tools/delpher_paper_titles.py profiles/beeldengeluid/source            # fix what looks like a publisher
    python tools/delpher_paper_titles.py profiles/beeldengeluid/source --all      # every hit
    python tools/delpher_paper_titles.py profiles/beeldengeluid/source --dry-run  # show, do not write

Titles are cached in <source>/pages/xml/titles.json (the pages cache: not in git). FROM THE CACHE
ONLY unless --kb-online: Delpher's robots.txt forbids automated visitors ("User-agent: * /
Disallow: /", checked 12-11-2026) and the KB said crawlers bring Delpher down; ask the KB first.
Standard library only.
"""
import argparse
import html
import json
import os
import re
import sys
import time
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

UA = "CollectionKit-demo/1.0 (collection disclosure prototype)"
# what the album pass left behind when the record had no paper title
PUBLISHERISH = re.compile(r"stichting|uitgevers|drukkerij|drukkerĳ|organisatie|n\.?v\.?\b|\[?s\.n\.\]?|en zoon|nieuwenhuis|geradtsen|van der loeff|arbeiderspers|neerlandia|maintiendrai|;", re.I)


def urn_of(url: str) -> str:
    m = re.search(r"urn=([^&]+)", url)
    if not m:
        raise ValueError(f"Delpher-link zonder urn: {url}")
    return m.group(1)


def tidy(paper: str) -> str:
    name = paper.split(" : ", 1)[0].split(" / ", 1)[0].strip().rstrip(".,")
    # one masthead, one name: Delpher catalogues some years of a paper under a variant
    name = {"Dagblad De Telegraaf": "De Telegraaf"}.get(name, name)
    return name or paper


def title_from_delpher(urn: str) -> str:
    url = f"https://www.delpher.nl/nl/kranten/view?coll=ddd&identifier={urllib.parse.quote(urn)}"
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=30) as r:
        page = r.read().decode("utf-8", "replace")
    m = re.search(r"<title>(.*?)</title>", page, re.S)
    if not m:
        raise ValueError("geen <title>")
    t = html.unescape(m.group(1)).strip()
    # "De waarheid » 15 dec 1956 - Art. 71 | Delpher"
    return tidy(t.split("»", 1)[0].strip()) if "»" in t else ""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("source")
    ap.add_argument("--all", action="store_true", help="every hit, not only the publisher-looking ones")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--kb-online", action="store_true",
                    help="ask Delpher for titles not in the cache (robots.txt forbids it: agree with the KB first)")
    args = ap.parse_args()

    cache_path = os.path.join(args.source, "pages", "xml", "titles.json")
    os.makedirs(os.path.dirname(cache_path), exist_ok=True)
    cache = json.load(open(cache_path, encoding="utf-8")) if os.path.exists(cache_path) else {}

    changed_total = 0
    for name in ("news-delpher.json", "news-delpher-events.json"):
        path = os.path.join(args.source, name)
        if not os.path.exists(path):
            continue
        hits = json.load(open(path, encoding="utf-8"))
        changed = 0
        for hit in hits:
            paper = hit.get("Paper", "")
            if not args.all and not PUBLISHERISH.search(paper):
                continue
            urn = urn_of(hit["Url"])
            if urn not in cache and not args.kb_online:
                print(f"  {urn}: niet in de cache; Delpher alleen met --kb-online")
                continue
            if urn not in cache:
                try:
                    time.sleep(0.7)
                    cache[urn] = title_from_delpher(urn)
                except Exception as ex:  # one failure must not stop the pass
                    print(f"  {urn}: FAILED {ex}")
                    cache[urn] = ""
                json.dump(cache, open(cache_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
            title = cache[urn]
            if title and title != paper:
                print(f"  {hit.get('Date','')}  {paper!r} -> {title!r}   {hit.get('Headline','')[:50]}")
                hit["Paper"] = title
                changed += 1
            elif not title:
                print(f"  {urn}: geen titel bij Delpher, blijft {paper!r}")
        print(f"{name}: {changed} van {len(hits)} krantentitels {'zouden veranderen' if args.dry_run else 'aangepast'}")
        changed_total += changed
        if changed and not args.dry_run:
            # the files are written by System.Text.Json with WriteIndented: two spaces, key order kept
            with open(path, "w", encoding="utf-8") as f:
                json.dump(hits, f, ensure_ascii=False, indent=2)
    return 0 if changed_total >= 0 else 1


if __name__ == "__main__":
    sys.exit(main())
