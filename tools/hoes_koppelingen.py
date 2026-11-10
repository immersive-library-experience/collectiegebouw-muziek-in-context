# -*- coding: utf-8 -*-
"""Everything the bundle links to each album sleeve, as a workbook for a rights assessment.

    python tools/hoes_koppelingen.py profiles/beeldengeluid <out.xlsx>

Reads <profile>/data (items.json, manifest.json) and <profile>/source (albums*.json for the editorial
number, covers/ for the image size). Three sheets: Leeswijzer, Hoezen (one row per sleeve),
Koppelingen (one row per sleeve x linked item). The counts on Hoezen are formulas over Koppelingen.
"""
import json, os, sys
from collections import Counter
from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter
from PIL import Image

profile, out = sys.argv[1], sys.argv[2]
items = json.load(open(os.path.join(profile, "data", "items.json"), encoding="utf-8"))
manifest = json.load(open(os.path.join(profile, "data", "manifest.json"), encoding="utf-8"))
by_id = {i["id"]: i for i in items}
albums = [i for i in items if i["kind"] == "album"]

# the editorial number of every album (source/albums*.json), by artist + title
numbers = {}
for name in ("albums.json", "albums-nl.json"):
    path = os.path.join(profile, "source", name)
    if os.path.exists(path):
        for a in json.load(open(path, encoding="utf-8")):
            numbers[(a["artist"].strip().lower(), a["title"].strip().lower())] = a["nr"]
def nr(a):
    return numbers.get((a["creator"].strip().lower(), a["title"].strip().lower()), "")

def kind_of(item):
    """What a linked item is, in the words a lawyer needs: a real quotation or an editorial text."""
    bron = item["fields"].get("bron", "")
    if item["kind"] == "movement":
        return "Stroming (redactionele tekst)"
    if item["id"].startswith("krant:kb:"):
        return "Krantenartikel (Delpher, KB)"
    if bron.startswith("Redactionele samenvatting in krantenstijl"):
        return "Redactionele nieuwskaart (geen citaat)"
    if bron.startswith("Publieke gebeurtenis"):
        return "Tijdlijn-gebeurtenis (redactionele samenvatting)"
    return "Nieuws (overig)"

def cover_source(a):
    """Where the sleeve image came from. The importer does not record it; it tries the Cover Art Archive
    (always 250 px wide), then Muziekweb (400 px), then Discogs, so the id type and the width tell."""
    path = os.path.join(profile, "source", "covers", a["mediaFile"])
    w, h = Image.open(path).size if os.path.exists(path) else (0, 0)
    key = a["id"].split(":")[1]
    if key == "mw": src = "Muziekweb"
    elif key == "dc": src = "Discogs"
    elif w == 250: src = "Cover Art Archive (MusicBrainz)"
    elif w == 400: src = "Muziekweb"
    else: src = "Discogs of handmatig (afgeleid)"
    return src, f"{w} × {h}"

def key_of(a):
    kind, _, rest = a["id"].partition(":")[2].partition(":")
    return {"mb": "MusicBrainz release-group ", "mw": "Muziekweb ", "dc": "Discogs ", "x": "eigen sleutel "}.get(kind, "") + rest

# links: album -> movement (on the album), news -> album (on the news item)
linked = {a["id"]: [] for a in albums}
for a in albums:
    for l in a.get("links") or []:
        if l["targetId"] in by_id:
            linked[a["id"]].append(by_id[l["targetId"]])
for i in items:
    for l in i.get("links") or []:
        if l["targetId"] in linked and i["kind"] != "album":
            linked[l["targetId"]].append(i)

albums.sort(key=lambda a: (a["date"], a["creator"], a["title"]))
ARIAL = Font(name="Arial", size=10); BOLD = Font(name="Arial", size=10, bold=True); HEAD = Font(name="Arial", size=10, bold=True, color="FFFFFF")
FILL = PatternFill("solid", fgColor="2433F0"); WRAP = Alignment(wrap_text=True, vertical="top")

def sheet(wb, title, headers, widths):
    ws = wb.create_sheet(title)
    ws.append(headers)
    for c, w in enumerate(widths, 1):
        ws.column_dimensions[get_column_letter(c)].width = w
        cell = ws.cell(1, c); cell.font = HEAD; cell.fill = FILL; cell.alignment = WRAP
    ws.freeze_panes = "A2"
    return ws

def finish(ws):
    for row in ws.iter_rows(min_row=2):
        for cell in row:
            cell.font = ARIAL; cell.alignment = WRAP
    ws.auto_filter.ref = ws.dimensions

wb = Workbook(); wb.remove(wb.active)

# ---- Koppelingen first (Hoezen counts refer to it) ---------------------------------------------
rows = []
for a in albums:
    for it in sorted(linked[a["id"]], key=lambda i: (i["kind"] != "movement", i["date"])):
        f = it["fields"]
        text = it.get("summary") or ""
        if it["id"].startswith("krant:kb:") and not text:
            text = "(alleen de kop)"
        if it["kind"] == "movement" and f.get("artiesten"):
            text += f" — Artiesten: {f['artiesten']}"
        rows.append([nr(a), a["creator"], a["title"], int(a["date"][:4]), kind_of(it), it["date"], it["title"], text,
                     f.get("bron", ""), it.get("url", ""),
                     ("hele pagina" if f.get("rechten") == "vrij" else "thumbnail") if f.get("pagina") else "nee", it["id"]])

hz = sheet(wb, "Hoezen", ["Nr", "Artiest", "Album", "Jaar", "Themalijn", "Genre", "Tekst bij de hoes (op het scherm)", "Doorklik / QR",
                         "Sleutel", "Bron hoesafbeelding", "Afbeelding (px)", "Stroming waar de hoes bij hoort",
                         "Krantenartikelen (Delpher)", "Redactionele nieuwskaarten", "Tijdlijn-gebeurtenissen", "Totaal gekoppeld"],
           [6, 24, 30, 7, 22, 16, 60, 34, 34, 26, 12, 34, 14, 14, 14, 10])
last = len(rows) + 1
for r, a in enumerate(albums, 2):
    src, size = cover_source(a)
    movement = next((i["title"] for i in linked[a["id"]] if i["kind"] == "movement"), "")
    count = lambda label: f'=COUNTIFS(Koppelingen!$L$2:$L${last},$Q{r},Koppelingen!$E$2:$E${last},"{label}")'
    hz.append([nr(a), a["creator"], a["title"], int(a["date"][:4]), a["fields"].get("themalijn", ""), a["fields"].get("genre", ""),
               a.get("summary", ""), a.get("url", ""), key_of(a), src, size, movement,
               count("Krantenartikel (Delpher, KB)"), count("Redactionele nieuwskaart (geen citaat)"),
               count("Tijdlijn-gebeurtenis (redactionele samenvatting)"), f"=COUNTIF(Koppelingen!$L$2:$L${last},$Q{r})", a["id"]])
hz.cell(1, 17, "Item-id").font = HEAD; hz.cell(1, 17).fill = FILL; hz.column_dimensions["Q"].width = 46
finish(hz)

kp = sheet(wb, "Koppelingen", ["Nr", "Artiest", "Album", "Jaar", "Soort gekoppelde informatie", "Datum", "Kop / titel", "Tekst op het scherm",
                               "Bron (zoals vermeld)", "Link", "Krantenpagina als beeld", "Item-id van de hoes", "Item-id van het gekoppelde item"],
           [6, 24, 30, 7, 30, 12, 44, 70, 40, 44, 12, 46, 46])
id_of = {(nr(a), a["creator"], a["title"]): a["id"] for a in albums}
for row in rows:
    kp.append(row[:11] + [id_of[(row[0], row[1], row[2])], row[11]])
finish(kp)

# ---- Leeswijzer ---------------------------------------------------------------------------------
kinds = Counter(r[4] for r in rows)
sources = Counter(cover_source(a)[0] for a in albums)
lw = wb.create_sheet("Leeswijzer", 0)
lw.column_dimensions["A"].width = 34; lw.column_dimensions["B"].width = 110
lines = [
    ("Interactieve installatie Fonos — wat er aan elke platenhoes is gekoppeld", ""),
    ("Stand", f"Databundel van {manifest['created'][:10]} ({manifest['itemCount']} items: {len(albums)} hoezen, "
              f"{sum(1 for i in items if i['kind']=='movement')} stromingen, {sum(1 for i in items if i['kind']=='news')} nieuwsitems). "
              "Dit is de demo-inhoud; de definitieve selectie volgt uit de curatie."),
    ("", ""),
    ("Wat een bezoeker bij een hoes ziet", "De hoes zelf op de tijdlijn, klein. Na aantikken tilt de hoes uit de wand met titel, artiest, jaar en de tekst uit de kolom 'Tekst bij de hoes'. Nog een tik geeft de "
     "volledige weergave: de hoes schermvullend, met themalijn, genre, tekst en een QR-code naar de pagina van het album bij Muziekweb of Discogs (kolom 'Doorklik / QR'). De gekoppelde items uit het tabblad Koppelingen "
     "worden niet onder de hoes opgesomd: ze staan op dezelfde tijdlijn bij hun eigen datum en openen elk een eigen paneel met hun eigen bronvermelding. "
     "Een hoes zonder koppelingen toont in de netwerkweergave de meest verwante albums."),
    ("Tabblad Hoezen", f"Eén regel per hoes ({len(albums)}). De vier telkolommen rechts zijn formules over het tabblad Koppelingen."),
    ("Tabblad Koppelingen", f"Eén regel per koppeling tussen een hoes en een ander item ({len(rows)} regels). Een hoes zonder regels heeft alleen zijn eigen tekst."),
    ("", ""),
    ("Soorten gekoppelde informatie", ""),
    ("Stroming (redactionele tekst)", f"{kinds['Stroming (redactionele tekst)']} koppelingen. Eigen samenvatting van een muziekstroming, bron: Tijdlijn Nederlandse Muziekgeschiedenis. Geen citaat."),
    ("Krantenartikel (Delpher, KB)", f"{kinds['Krantenartikel (Delpher, KB)']} koppelingen. Echte krantenkop uit Delpher, met krant en datum en een link naar de KB. "
     "Volgens het antwoord van de jurist van de KB (19-10-2026): een beschermd artikel met kop, tekst tot 150 tekens en een thumbnail van de pagina tot 200 x 200 pixels, een rechtenvrij artikel met de hele pagina. Daarvoor is een standaardcontract tussen Beeld & Geluid en de KB nodig."),
    ("Redactionele nieuwskaart (geen citaat)", f"{kinds['Redactionele nieuwskaart (geen citaat)']} koppelingen. Door ons geschreven kop en korte tekst in krantenstijl over het album. "
     "Dit is GEEN citaat uit een bestaande krant; op het scherm staat als bron 'Redactionele samenvatting in krantenstijl — te vervangen door Delpher-citaat'. Bedoeld om te vervangen door echte Delpher-artikelen."),
    ("Tijdlijn-gebeurtenis (redactionele samenvatting)", f"{kinds['Tijdlijn-gebeurtenis (redactionele samenvatting)']} koppelingen. Publieke gebeurtenis uit het jaar van het album, in eigen woorden samengevat."),
    ("", ""),
    ("Herkomst van de hoesafbeeldingen", "; ".join(f"{k}: {v}" for k, v in sources.most_common()) + ". De importer legt de bron niet per hoes vast; "
     "de kolom is afgeleid uit de sleutel en de afmeting (Cover Art Archive levert 250 px breed, Muziekweb 400 px). De bestanden zijn klein (kolom 'Afbeelding (px)'), maar in de volledige weergave wordt de hoes vergroot tot de hoogte van het scherm."),
    ("Hoe de koppelingen ontstaan", "Stroming: redactioneel toegewezen per album. Nieuws: gekoppeld aan het album waar het over gaat (Delpher-zoekactie op artiest en titel rond de "
     "releasedatum, automatisch gefilterd op ruis; redactionele kaarten per album geschreven) of aan albums uit hetzelfde jaar (tijdlijn-gebeurtenissen)."),
    ("Nog niet in dit overzicht", f"De {len(set(a['fields'].get('themalijn', '') for a in albums))} themalijnen (verhaallijnen door de collectie) en de stromingen zelf hebben geen eigen afbeelding."),
    ("Gemaakt met", "tools/hoes_koppelingen.py in de CollectionKit-repository; opnieuw te maken bij elke nieuwe bundel."),
]
for a, b in lines:
    lw.append([a, b])
for row in lw.iter_rows():
    row[0].font = BOLD; row[0].alignment = WRAP; row[1].font = ARIAL; row[1].alignment = WRAP
lw["A1"].font = Font(name="Arial", size=13, bold=True)

wb.save(out)
print("saved", out, "| hoezen", len(albums), "| koppelingen", len(rows), dict(kinds), "| zonder nr:", sum(1 for a in albums if nr(a) == ""), "| bronnen", dict(sources))
