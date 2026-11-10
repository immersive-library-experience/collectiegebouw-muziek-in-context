# -*- coding: utf-8 -*-
"""Hand curation next to CollectionKit.Import.Delpher, for when its automatic choice is off the subject
or its OCR is unreadable: look at the candidates of a query, and set chosen articles as the hits of an
event key. Same SRU endpoint, same record shape and the same opening-sentence rule as the importer.

    python tools/delpher_pick.py list "<term + term>" <dd-MM-yyyy> <dd-MM-yyyy> [n]
    python tools/delpher_pick.py ocr <urn>
    python tools/delpher_pick.py pick <key> "<term + term>" <dd-MM-yyyy> <dd-MM-yyyy> <urn> [<urn> ...]

`pick` replaces the hits of <key> in news-delpher-events.json by the named articles, which must be among
the results of the given search (that is where date, headline and paper come from). The key has to exist
in events-delpher.json (albumNrs and replaces are taken from it); write the search you used back into that
query, with a "note", so the file says what was asked. An importer run with --force redoes every query
and loses hand picks.

Source folder: profiles/beeldengeluid/source, or --source <dir> as the first two arguments.
"""
import json, sys, time, urllib.parse, urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

SRC = Path(__file__).resolve().parents[1] / "profiles" / "beeldengeluid" / "source"
UA = {"User-Agent": "CollectionKit-demo/1.0 (collection disclosure prototype)"}
SRU = "http://jsru.kb.nl/sru/sru?version=1.2&operation=searchRetrieve&x-collection=DDD_artikel&recordSchema=ddd"


def get(url):
    time.sleep(0.6)                                   # be polite to the KB endpoint, like the importer
    return urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=40).read().decode("utf-8", "replace")


def tidy(paper):
    name = paper.split(" : ", 1)[0].split(" / ", 1)[0].strip().rstrip(".,")
    return name or paper


def search(query, frm, to, n=30):
    """(total, records) for the terms (joined by ' + ', each a phrase) within the date window, articles only."""
    terms = [t.strip() for t in query.split(" + ") if t.strip()]
    cql = " AND ".join('("%s")' % t.replace('"', "") for t in terms) + f' AND (date within "{frm} {to}") AND (type=artikel)'
    root = ET.fromstring(get(f"{SRU}&maximumRecords={n}&query=" + urllib.parse.quote(cql)))
    total = next((int(e.text) for e in root.iter() if e.tag.endswith("numberOfRecords")), 0)
    out, seen = [], set()
    for rec in (e for e in root.iter() if e.tag.endswith("recordData")):
        f = {}
        for e in rec.iter():
            f.setdefault(e.tag.split("}")[-1], (e.text or "").strip())
        title, date, ident = f.get("title"), f.get("date"), f.get("identifier")
        if not (title and date and ident) or title in seen:
            continue
        seen.add(title)
        out.append({"date": date.split(" ")[0].replace("/", "-"), "headline": title,
                    "paper": tidy(f.get("papertitle") or f.get("publisher") or "onbekende krant"),
                    "url": ident[:-4] if ident.endswith(":ocr") else ident})
    return total, out


def prose_ratio(text):
    return sum(1 for c in text if c.isalpha() or c == " " or c in ",.;:'\"-()") / len(text)


def opening(url):
    """The opening sentence(s) the importer would put on the card (OpeningSentence in Program.cs)."""
    try:
        root = ET.fromstring(get(url + ":ocr"))
    except Exception:
        return ""
    para = next((p.text.strip() for p in root.iter("p")
                 if p.text and len(p.text.strip()) >= 80 and prose_ratio(p.text.strip()) >= 0.86), None)
    if not para:
        return ""
    cleaned = para.lstrip(".*•'\"„-— ")
    snippet = cleaned
    first = cleaned.find(". ")
    if first > 30:
        second = cleaned.find(". ", first + 2)
        snippet = cleaned[:second + 1] if first < second < 200 else cleaned[:first + 1]
    if len(snippet) > 220:
        snippet = snippet[:219].rstrip() + "…"
    return snippet


def resolver(urn):
    return urn if urn.startswith("http") else "http://resolver.kb.nl/resolve?urn=" + urn


def main(argv):
    global SRC
    if argv and argv[0] == "--source":
        SRC = Path(argv[1]); argv = argv[2:]
    mode = argv[0]
    if mode == "list":
        total, recs = search(argv[1], argv[2], argv[3], int(argv[4]) if len(argv) > 4 else 30)
        print(f"## {argv[1]} [{argv[2]} .. {argv[3]}]: {total} treffers")
        for r in recs:
            print(f"  {r['date']} {r['paper'][:24]:24} | {r['headline'][:90]} | {r['url'].split('urn=')[-1]}")
    elif mode == "ocr":
        print(opening(resolver(argv[1])))
    elif mode == "pick":
        key, query, frm, to, urns = argv[1], argv[2], argv[3], argv[4], argv[5:]
        q = next(q for q in json.loads((SRC / "events-delpher.json").read_text(encoding="utf-8")) if q["key"] == key)
        _, recs = search(query, frm, to, 50)
        by = {r["url"].split("urn=")[-1]: r for r in recs}
        path = SRC / "news-delpher-events.json"
        hits = [h for h in json.loads(path.read_text(encoding="utf-8")) if h["Key"] != key]
        for urn in urns:
            if urn not in by:
                sys.exit(f"{urn} is not among the first 50 results of that search")
            r = by[urn]
            snippet = opening(r["url"])
            if not snippet:
                sys.exit(f"{urn}: no readable opening sentence, and a card without one retires nothing")
            hits.append({"Key": key, "AlbumNrs": q.get("albumNrs") or [], "Replaces": q.get("replaces") or [],
                         "Date": r["date"], "Headline": r["headline"], "Paper": r["paper"], "Url": r["url"], "Snippet": snippet})
            print(f"{key}: {r['date']} {r['paper']} | {r['headline'][:80]}\n   > {snippet}")
        hits.sort(key=lambda h: h["Date"])
        path.write_text(json.dumps(hits, ensure_ascii=False, indent=2), encoding="utf-8")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
