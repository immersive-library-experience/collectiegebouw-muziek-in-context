using System.Globalization;
using System.Text.Json;
using CollectionKit.Core;
using CollectionKit.Processing;

// Beeld & Geluid MVP bundle builder (roadmap phase 1: curated albums + historical news context).
//
// Sources (all in --source dir):
//   albums.json           international canon (jazz demo set)
//   albums-nl.json        Dutch music history canon (levenslied → nederhop → dance)
//     Every album carries `nr` (the editorial number: unique across both files, never reused; the
//     other source files refer to an album by it) and ONE external key that becomes the bundle id:
//     `mbid` = MusicBrainz release-group → album:mb:<uuid>, else `muziekweb` = Muziekweb album id
//     (EBX0085) → album:mw:<id>, else `discogs` = Discogs master (m181126) or release (r8591)
//     → album:dc:<id>, else `key` = editorial slug → album:x:<slug>. `release` (optional) names
//     the MusicBrainz edition whose sleeve and year we show. See docs/stabiele-ids.md.
//   themalijnen.json      the ten theme lines of the editorial framework; albums carry "themalijn": key
//   timeline-events.json  parsed from the "Nederlandse Muziekgeschiedenis Tijdlijn" page:
//                         real news events (news_nl / news_intl) and music movements
//                         (music_nl / music_intl) with genre + key artists, 1951–2024.
//
// Item graph: albums link to their movement (artist match, relation "part-of"); news links to
// same-year albums and movements ("context-of"). The timeline layout pins x to the date, so the
// result reads as the visual time-journey of the concept page.
//
// usage: --source <dir> --out <bundle dir> [--no-fetch]

var source = Arg("--source") ?? throw new ArgumentException("--source <dir> is required");
var output = Arg("--out") ?? throw new ArgumentException("--out <bundle dir> is required");
var fetchCovers = !args.Contains("--no-fetch");
// --albums-only: bundel met uitsluitend albums (geen nieuws/stromingen) — voor de juridische
// weergave richting rechtenorganisaties en als rechten-schone demovariant.
var albumsOnly = args.Contains("--albums-only");

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var albums = JsonSerializer.Deserialize<List<Album>>(File.ReadAllText(Path.Combine(source, "albums.json")), json)!;

// the album's fields; "belang" (0..1, the source's own weight: an iconic album) only when the source says so —
// the Explorer's guide and Density ranking read it
Dictionary<string, string> AlbumFields(Album album)
{
    var fields = new Dictionary<string, string> { ["themalijn"] = ThemeLabel(album), ["genre"] = album.Subgenre, ["jaar"] = album.Year.ToString() };
    if (album.Belang > 0f) fields["belang"] = album.Belang.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    return fields;
}
var albumsNlPath = Path.Combine(source, "albums-nl.json");
if (File.Exists(albumsNlPath))
    albums.AddRange(JsonSerializer.Deserialize<List<Album>>(File.ReadAllText(albumsNlPath), json)!);

// Identity before position (docs/stabiele-ids.md): the number is for people and for the other source
// files, the key is the id. Both must be unique, and every album needs a key — an album without one
// stops the build with the full list, it never falls back to something silent.
{
    var badNr = albums.Where(a => a.Nr <= 0).Select(a => $"{a.Artist} - {a.Title}").ToList();
    var dupNr = albums.GroupBy(a => a.Nr).Where(g => g.Count() > 1).Select(g => $"nr {g.Key}: {string.Join(" / ", g.Select(a => a.Title))}").ToList();
    var noKey = albums.Where(a => a.Mbid.Length == 0 && a.Muziekweb.Length == 0 && a.Discogs.Length == 0 && a.Key.Length == 0).Select(a => $"nr {a.Nr}: {a.Artist} - {a.Title}").ToList();
    var dupKey = albums.Where(a => a.Mbid.Length > 0 || a.Muziekweb.Length > 0 || a.Discogs.Length > 0 || a.Key.Length > 0)
        .GroupBy(AlbumKey).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(" / ", g.Select(a => $"nr {a.Nr}"))}").ToList();
    var problems = badNr.Select(p => "geen nr: " + p).Concat(dupNr.Select(p => "dubbel " + p))
        .Concat(noKey.Select(p => "geen sleutel (mbid, muziekweb, discogs of key): " + p)).Concat(dupKey.Select(p => "dubbele sleutel " + p)).ToList();
    if (problems.Count > 0)
        throw new InvalidDataException($"{problems.Count} albums zonder geldige identiteit:\n  " + string.Join("\n  ", problems));
    var editorial = albums.Where(a => a.Mbid.Length == 0 && a.Muziekweb.Length == 0 && a.Discogs.Length == 0).ToList();
    if (editorial.Count > 0)
        Console.WriteLine($"  let op: {editorial.Count} albums op een redactionele sleutel (geen MusicBrainz/Muziekweb/Discogs): "
            + string.Join(", ", editorial.Select(a => $"nr {a.Nr} {a.Title}")));
}
// Theme lines of the editorial framework (themalijnen.json): every album names one by key; the
// short label becomes the "themalijn" field, which the Explorer shows as its facet panel.
var themeLinesPath = Path.Combine(source, "themalijnen.json");
var themeLines = File.Exists(themeLinesPath)
    ? JsonSerializer.Deserialize<List<ThemeLine>>(File.ReadAllText(themeLinesPath), json)!.ToDictionary(t => t.Key)
    : [];
string ThemeLabel(Album album)
{
    if (themeLines.Count == 0 || string.IsNullOrEmpty(album.Themalijn))
        return "";
    return themeLines.TryGetValue(album.Themalijn, out var line)
        ? line.Kort
        : throw new InvalidDataException($"album nr {album.Nr} ({album.Title}): themalijn '{album.Themalijn}' staat niet in themalijnen.json");
}
var withoutLine = themeLines.Count == 0 ? 0 : albums.Count(a => string.IsNullOrEmpty(a.Themalijn));
if (withoutLine > 0)
    Console.WriteLine($"  let op: {withoutLine} albums zonder themalijn");

var events = JsonSerializer.Deserialize<List<TimelineEvent>>(File.ReadAllText(Path.Combine(source, "timeline-events.json")), json)!;
var news = albumsOnly ? [] : events.Where(e => e.Type.StartsWith("news")).ToList();
var movements = albumsOnly ? [] : events.Where(e => e.Type.StartsWith("music")).ToList();
Console.WriteLine($"{albums.Count} albums, {movements.Count} movements, {news.Count} news events");

// Real Delpher event articles (news-delpher-events.json, written by CollectionKit.Import.Delpher):
// loaded first, because an editorial card is retired the moment the real front page of that
// moment exists — one moment, one card, the words that were actually printed.
var eventHitsPath = Path.Combine(source, "news-delpher-events.json");
var eventHits = !albumsOnly && File.Exists(eventHitsPath)
    ? JsonSerializer.Deserialize<List<EventHit>>(File.ReadAllText(eventHitsPath), json)!
    : [];
var eventUrns = eventHits.Where(h => !IsNoiseHeadline(h.Headline)).Select(h => KbUrn(h.Url)).ToHashSet();
var replacedHeadlines = eventHits
    .Where(h => h.Snippet.Length > 0 && !IsNoiseHeadline(h.Headline))
    .SelectMany(h => h.Replaces ?? [])
    .Where(r => r.Length > 0)
    .Distinct()
    .ToList();
var retired = 0;
bool Replaced(string headline)
{
    var hit = replacedHeadlines.Any(r => headline.Contains(r, StringComparison.OrdinalIgnoreCase));
    if (hit) retired++;
    return hit;
}

// ── Covers (Cover Art Archive by key, Muziekweb by album id, Discogs by master/release; cached in source/covers) ───────────
// The cache is keyed by the album key, so "exists → skip" can only ever hit the same album. No loose
// title search any more: that found someone else's sleeve 5 times in 29 — a key or no sleeve.
var coversDir = Path.Combine(source, "covers");
Directory.CreateDirectory(coversDir);

// ── The printed pages behind the Delpher articles (source/pages, written by CollectionKit.Import.Delpher
//    --pages): the 400 px page is the item's media file, the 1600 px page goes to media-hires, and the
//    article's rectangle on the page travels as field "knip" (fractions x y w h). Indoor demo only until
//    the KB and the rights holders have said yes (docs/beeldengeluid-mvp.md). ──────────────────────
var pagesDir = Path.Combine(source, "pages");
var pagesIndex = Path.Combine(pagesDir, "pages.json");
var pages = File.Exists(pagesIndex)
    ? JsonSerializer.Deserialize<List<PageEntry>>(File.ReadAllText(pagesIndex), json)!.ToDictionary(p => p.Urn)
    : new Dictionary<string, PageEntry>();
if (pages.Count > 0)
    Console.WriteLine($"{pages.Count} krantenpagina's uit {pagesIndex}");
// What the KB allows (its jurist, 19-10-2026): an article free of rights may be shown whole — that is
// an article older than 70 years without a named maker (140 with one; none of ours). A protected
// article may show a thumbnail of its whole page within 200 x 200 px and a snippet of at most 150
// characters, with a link through to Delpher. The assumption "no named maker" holds for the news
// items in this set (checked by hand for the handful that are old enough); the year does the rest.
string Rights(DateOnly date) => date.Year <= DateTime.Now.Year - 71 ? "vrij" : "beschermd";
string PageFile(string urn, string rights)
{
    if (!pages.TryGetValue(urn, out var p))
        return "";
    var file = rights == "vrij" ? Path.Combine(pagesDir, p.File) : Path.Combine(pagesDir, "thumb", p.File);
    return File.Exists(file) ? p.File : "";
}
Dictionary<string, string> WithPage(Dictionary<string, string> fields, string urn, string rights)
{
    fields["rechten"] = rights;
    if (pages.TryGetValue(urn, out var p))
    {
        fields["pagina"] = p.Page;
        if (rights == "vrij" && p.Rect is { Length: 4 })
            fields["knip"] = string.Join(' ', p.Rect.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)));
    }
    return fields;
}
// the KB's snippet: at most 150 characters, cut at a word
static string Snippet(string text)
{
    if (text.Length <= 150)
        return text;
    var cut = text.LastIndexOf(' ', 149);
    return text[..(cut > 100 ? cut : 149)].TrimEnd(',', ';', ':', ' ') + "\u2026";
}
foreach (var album in albums)
{
    // Transition (September 2026): sleeves cached under the old running number move to the key once
    var legacy = Path.Combine(coversDir, $"{album.Nr}.jpg");
    var target = Path.Combine(coversDir, CoverFile(album));
    if (File.Exists(legacy) && !File.Exists(target))
    {
        File.Move(legacy, target);
        Console.WriteLine($"  cover-cache: {album.Nr}.jpg → {CoverFile(album)}");
    }
}
if (fetchCovers)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CollectionKit-demo/1.0 (collection disclosure prototype)");
    List<byte[]>? muziekwebPlaceholders = null;
    foreach (var album in albums)
    {
        var cachePath = Path.Combine(coversDir, CoverFile(album));
        if (File.Exists(cachePath))
            continue;
        // "cover": false in the source: the Cover Art Archive has no sleeve of this album or only a wrong
        // one, or the sleeve does not belong in a shop window. Muziekweb (below) is still tried.
        var fetched = album.Cover && album.Mbid.Length > 0
            && ((album.Release.Length > 0 && await TryFetch(http, cachePath, "release", album.Release))
                || await TryFetch(http, cachePath, "release-group", album.Mbid));
        var via = fetched ? "Cover Art Archive" : "";
        if (!fetched && album.Muziekweb.Length > 0)
        {
            // Muziekweb serves the front sleeve on the album id alone; an unknown id gets a placeholder
            // image, recognised by comparing with the placeholder of an id that cannot exist.
            // Two placeholders exist: a cd (any unknown id, ZZZ0000) and a record for lp-type ids
            // (DA00000): an lp without a sleeve in the catalogue comes back as a drawing of a record.
            if (muziekwebPlaceholders is null)
            {
                muziekwebPlaceholders = [];
                foreach (var id in new[] { "ZZZ0000", "DA00000" })
                    if (await TryGetBytes(http, MuziekwebCoverUrl(id)) is { Length: > 0 } ph)
                        muziekwebPlaceholders.Add(ph);
            }
            var bytes = await TryGetBytes(http, MuziekwebCoverUrl(album.Muziekweb));
            if (bytes is { Length: > 0 } && !muziekwebPlaceholders.Any(ph => bytes.AsSpan().SequenceEqual(ph)))
            {
                await File.WriteAllBytesAsync(cachePath, bytes);
                fetched = true; via = "Muziekweb";
            }
        }
        if (!fetched && album.Discogs.Length > 0)
        {
            // Discogs: the primary image of the master or release, through its API (no token needed,
            // 25 requests a minute; the images are not CC0, see docs/stabiele-ids.md)
            var bytes = await TryFetchDiscogs(http, album.Discogs);
            if (bytes is { Length: > 0 })
            {
                await File.WriteAllBytesAsync(cachePath, bytes);
                fetched = true; via = "Discogs";
            }
        }
        Console.WriteLine(fetched ? $"  cover nr {album.Nr} ({album.Title}) via {via}" : $"  cover nr {album.Nr} ({album.Title}): geen hoes — placeholder");
    }
}

// ── Items ────────────────────────────────────────────────────────────────────────────────────────
var items = new List<CollectionItem>();
var seenUrns = new HashSet<string>();   // one KB article, one item
var vectors = new List<float[]>();
var dutchCulture = CultureInfo.GetCultureInfo("nl-NL");

string MovementId(TimelineEvent movement) => $"movement:{movement.Year}:{Slug(movement.Title)}";

// Albums first (canonical order): they carry the real feature vectors
foreach (var album in albums)
{
    var movementLinks = movements
        .Where(m => MatchesArtist(m, album.Artist))
        .Select(m => new ItemLink { TargetId = MovementId(m), Relation = "part-of" })
        .ToList();

    items.Add(new CollectionItem
    {
        Id = AlbumKey(album),
        Title = album.Title,
        Creator = album.Artist,
        Date = new DateOnly(album.Year, 1, 1),
        Kind = "album",
        Summary = album.Description,
        // Albums link out to Muziekweb: the album page when the id is known; else the Discogs page;
        // else the Muziekweb search for artist + title
        Url = album.Muziekweb.Length > 0
            ? $"https://www.muziekweb.nl/Link/{album.Muziekweb}/"
            : album.Discogs.Length > 0
                ? DiscogsUrl(album.Discogs)
                : $"https://www.muziekweb.nl/Muziekweb/Search?search={Uri.EscapeDataString(album.Artist + " " + album.Title)}",
        MediaFile = File.Exists(Path.Combine(coversDir, CoverFile(album))) ? CoverFile(album) : "",
        Fields = AlbumFields(album),
        Links = movementLinks,
    });
    vectors.Add(album.Features);
}

// Movements: era/genre cards; their vector is the average of their albums so similarity layouts
// place them amid their scene
foreach (var movement in movements)
{
    var linkedAlbums = albums.Where(a => MatchesArtist(movement, a.Artist)).ToList();
    var linkedAlbumVectors = linkedAlbums.Select(a => a.Features).ToList();
    // A movement belongs to the theme line most of its albums belong to
    var movementLine = linkedAlbums
        .Select(ThemeLabel)
        .Where(label => label.Length > 0)
        .GroupBy(label => label)
        .OrderByDescending(group => group.Count())
        .Select(group => group.Key)
        .FirstOrDefault()
        // no album of ours in it (Woodstock, Nirvana, trip-hop): the line that names its genre
        ?? themeLines.Values.FirstOrDefault(line => (line.Stromingen ?? []).Contains(movement.Genre ?? ""))?.Kort
        ?? "";

    items.Add(new CollectionItem
    {
        Id = MovementId(movement),
        Title = movement.Title,
        Date = new DateOnly(movement.Year, 7, 1),
        Kind = "movement",
        Summary = movement.Body,
        Fields = new()
        {
            ["themalijn"] = movementLine,
            ["genre"] = movement.Genre ?? "",
            ["artiesten"] = string.Join(", ", movement.Artists ?? []),
            ["bron"] = "Tijdlijn Nederlandse Muziekgeschiedenis (redactionele samenvatting)",
        },
    });
    vectors.Add(Average(linkedAlbumVectors));
}

// News: real historical events anchoring the collection in its time
foreach (var article in news)
{
    if (Replaced(article.Head ?? ""))
        continue;
    var date = ParseDutchDate(article.Date, article.Year, dutchCulture);
    var sameYearAlbums = albums.Where(a => a.Year == article.Year).Take(3).ToList();
    var links = sameYearAlbums
        .Select(a => new ItemLink { TargetId = AlbumKey(a), Relation = "context-of" })
        .Concat(movements.Where(m => m.Year == article.Year).Take(2)
            .Select(m => new ItemLink { TargetId = MovementId(m), Relation = "context-of" }))
        .ToList();

    items.Add(new CollectionItem
    {
        Id = $"news:{article.Year}:{Slug(article.Head ?? "")}",
        Title = article.Head ?? "",
        Date = date,
        Kind = "news",
        Summary = article.Body,
        Fields = new()
        {
            ["tag"] = article.Tag ?? "",
            ["bron"] = "Publieke gebeurtenis — redactionele samenvatting (tijdlijn); te vervangen door Delpher-citaten",
        },
        Links = links,
    });
    vectors.Add(Average(sameYearAlbums.Select(a => a.Features).ToList()));
}

// Period-style music journalism per album (news.json): crafted in the voice of a 1950s–80s
// Dutch newspaper — the front-page cards of the wall. Each links to its album and shares its
// feature vector, so the article always hovers beside the record it reviews. This is also
// where the iconic album↔news pairings live (Kent State ↔ CSNY's "Ohio", the 1981 vredes-
// demonstratie ↔ Doe Maar, Venus at #1 in the US ↔ Shocking Blue, …).
var albumNewsPath = Path.Combine(source, "news.json");
if (!albumsOnly && File.Exists(albumNewsPath))
{
    var albumNews = JsonSerializer.Deserialize<List<AlbumNews>>(File.ReadAllText(albumNewsPath), json)!;
    var merged = 0;
    foreach (var article in albumNews)
    {
        if (Replaced(article.Headline))
            continue;
        var album = albums.FirstOrDefault(a => a.Nr == article.AlbumNr);
        if (album is null && article.AlbumNr != 0)
            Console.WriteLine($"  let op: albumnieuws '{article.Headline}' verwijst naar onbekend album nr {article.AlbumNr}");
        var date = DateOnly.Parse(article.Date);
        // albumId 0 = no single album owns the moment (Woodstock, Live Aid, gabber…):
        // link to that year's albums and movements instead, like the timeline news
        var yearAlbums = album is null ? albums.Where(a => a.Year == date.Year).Take(3).ToList() : [album];
        if (yearAlbums.Count == 0)
            continue;
        items.Add(new CollectionItem
        {
            Id = $"albumnews:{date.Year}:{Slug(article.Headline)}",
            Title = article.Headline,
            Date = date,
            Kind = "news",
            Summary = article.Snippet,
            Fields = new()
            {
                ["tag"] = "muziek",
                ["bron"] = "Redactionele samenvatting in krantenstijl — te vervangen door Delpher-citaat",
            },
            Links = yearAlbums
                .Select(a => new ItemLink { TargetId = AlbumKey(a), Relation = "context-of" })
                .Concat(album is null
                    ? movements.Where(m => m.Year == date.Year).Take(2)
                        .Select(m => new ItemLink { TargetId = MovementId(m), Relation = "context-of" })
                    : [])
                .ToList(),
        });
        vectors.Add(Average(yearAlbums.Select(a => a.Features).ToList()));
        merged++;
    }
    Console.WriteLine($"{merged} album news articles merged");
}

// Real Delpher newspaper headlines (produced by CollectionKit.Import.Delpher from the KB's
// public SRU endpoint): metadata + deep-link only — article text stays behind citaatrecht
// curation. Each headline links to its album and carries the paper name as provenance.
var delpherPath = Path.Combine(source, "news-delpher.json");
if (!albumsOnly && File.Exists(delpherPath))
{
    var delpherHits = JsonSerializer.Deserialize<List<DelpherHit>>(File.ReadAllText(delpherPath), json)!;
    var merged2 = 0;
    foreach (var hit in delpherHits)
    {
        var albumIndex = albums.FindIndex(a => a.Nr == hit.AlbumNr);
        if (albumIndex < 0)
        {
            Console.WriteLine($"  let op: Delpher-kop '{hit.Headline}' verwijst naar onbekend album nr {hit.AlbumNr}: overgeslagen");
            continue;
        }
        // Generic section headlines are noise on a wall of front pages
        if (IsNoiseHeadline(hit.Headline))
            continue;
        var urn = KbUrn(hit.Url);
        // The same article also chosen by a curated query: that one wins, it carries the opening
        // sentence and may be what retires an editorial card (one KB article, one item)
        if (eventUrns.Contains(urn))
            continue;
        if (!seenUrns.Add(urn))
        {
            Console.WriteLine($"  let op: Delpher-artikel {urn} zit al in de bundel: overgeslagen");
            continue;
        }
        var rights = Rights(DateOnly.Parse(hit.Date));
        items.Add(new CollectionItem
        {
            Id = $"krant:kb:{urn}",
            Title = Snippet(hit.Headline),
            Creator = hit.Paper,
            Date = DateOnly.Parse(hit.Date),
            Kind = "news",
            // No filler body ("Krantenartikel uit…" reads as noise): the card is the headline,
            // the story panel carries source + deep link
            Summary = "",
            Url = hit.Url,
            MediaFile = PageFile(urn, rights),
            Fields = WithPage(new() { ["bron"] = $"{hit.Paper} (Delpher, Koninklijke Bibliotheek)" }, urn, rights),
            Links = [new ItemLink { TargetId = AlbumKey(albums[albumIndex]), Relation = "context-of" }],
        });
        vectors.Add(albums[albumIndex].Features);
        merged2++;
    }
    Console.WriteLine($"{merged2} Delpher headlines merged ({delpherHits.Count - merged2} noise skipped)");
}

// Curated event articles (news-delpher-events.json): the REAL front page of the iconic
// moments — actual headline, paper and first OCR sentence from Delpher, deep-linked
if (eventHits.Count > 0)
{
    foreach (var hit in eventHits)
    {
        if (IsNoiseHeadline(hit.Headline))
            continue;
        var date = DateOnly.Parse(hit.Date);
        var unknownNrs = hit.AlbumNrs.Where(nr => albums.All(a => a.Nr != nr)).ToList();
        if (unknownNrs.Count > 0)
            Console.WriteLine($"  let op: gebeurtenis '{hit.Key}' verwijst naar onbekende albums nr {string.Join(", ", unknownNrs)}");
        var linkedAlbums = hit.AlbumNrs.Length > 0
            ? albums.Where(a => hit.AlbumNrs.Contains(a.Nr)).ToList()
            : albums.Where(a => a.Year == date.Year).Take(3).ToList();
        var urn = KbUrn(hit.Url);
        if (!seenUrns.Add(urn))
        {
            Console.WriteLine($"  let op: Delpher-artikel {urn} zit al in de bundel: overgeslagen");
            continue;
        }
        var rights = Rights(date);
        items.Add(new CollectionItem
        {
            Id = $"krant:kb:{urn}",
            Title = Snippet(hit.Headline),
            Creator = hit.Paper,
            Date = date,
            Kind = "news",
            Summary = Snippet(hit.Snippet),
            Url = hit.Url,
            MediaFile = PageFile(urn, rights),
            Fields = WithPage(new()
            {
                ["tag"] = hit.Key.Replace('-', ' '),
                ["bron"] = $"{hit.Paper} (Delpher, Koninklijke Bibliotheek — originele kop en openingszin)",
            }, urn, rights),
            Links = linkedAlbums.Select(a => new ItemLink { TargetId = AlbumKey(a), Relation = "context-of" }).ToList(),
        });
        vectors.Add(Average(linkedAlbums.Select(a => a.Features).ToList()));
    }
    Console.WriteLine($"{eventHits.Count} Delpher event articles merged; {retired} editorial cards retired in their favour");
}

// ── Vectors: e5 text embeddings over title/creator/genre/period/description (same pipeline as
//    the Rijksmuseum profile — cached per item id AND the hash of the embedded text, so a changed
//    description is embedded again; --compact drops superseded rows and ids that left the set),
//    with the hand-tuned 8-dim feature vectors as fallback when the model isn't on disk. e5 buurten zijn semantisch:
//    "Ohio" schuift naast álle protestmuziek, niet alleen naast folkrock. ─────────────────────
if (!args.Contains("--no-embed") && CollectionKit.Embeddings.E5Embedder.IsModelAvailable())
{
    var store = CollectionKit.Embeddings.EmbeddingStore.Open(Path.Combine(source, "embeddings-e5"));
    var texts = items.Select(item =>
    {
        var year = item.Date?.Year ?? 1970;
        var parts = new[]
        {
            item.Title, item.Creator,
            item.Fields.GetValueOrDefault("genre", ""), item.Fields.GetValueOrDefault("tag", ""),
            $"{(year - 1) / 10 * 10}s", item.Summary,
        };
        return string.Join(". ", parts.Where(p => p.Length > 0));
    }).ToList();
    var hashes = texts.Select(CollectionKit.Embeddings.EmbeddingStore.HashOf).ToList();
    vectors = new List<float[]>(items.Count);
    var missing = 0;
    CollectionKit.Embeddings.E5Embedder? embedder = null;
    try
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (!store.TryGet(items[i].Id, hashes[i], out var vector))
            {
                embedder ??= await CollectionKit.Embeddings.E5Embedder.CreateAsync(progress: new Progress<string>(Console.WriteLine));
                vector = embedder.Embed(texts[i]);
                store.Put(items[i].Id, hashes[i], vector);
                missing++;
            }
            vectors.Add(vector);
        }
    }
    finally
    {
        embedder?.Dispose();
    }
    Console.WriteLine($"e5: {items.Count} vectors ({missing} newly embedded; store {store.Distinct} ids in {store.Count} rows)");
    if (args.Contains("--compact"))
    {
        // only a full build knows the whole set: an albums-only build would drop the news vectors
        if (albumsOnly)
            Console.WriteLine("e5: --compact overgeslagen bij --albums-only (de nieuwsvectoren horen ook in de store)");
        else
            Console.WriteLine($"e5: compact — {store.Compact(items.Select(item => item.Id))} rijen weg, {store.Count} over");
    }
}

var bundle = new CollectionBundle
{
    Manifest = new BundleManifest
    {
        Name = "Muziek & context — Nederlandse muziekgeschiedenis (MVP)",
        Description = "Beeld & Geluid roadmap fase 1: gecureerde albums (NL + internationaal) verbonden met " +
                      "muziekstromingen en echt nieuws uit hun tijd, 1951–2024.",
        Source = $"musicnews importer ← {source} + Tijdlijn Nederlandse Muziekgeschiedenis",
        DataLicense = "Albummetadata via MusicBrainz; hoezen via Cover Art Archive; nieuws- en stromingsteksten " +
                      "zijn redactionele samenvattingen van publieke gebeurtenissen (bron: Synthense tijdlijn). " +
                      "Delpher-krantencitaten volgen in fase 1-curatie.",
        // which source this bundle is of: demo.ps1 -Update recomputes this over its source\ and says when they differ
        SourceHash = CollectionBundle.ComputeSourceHash(source),
    },
    Items = items,
    Vectors = vectors.ToArray(),
};

// ── Similarity + layouts via the open providers ──────────────────────────────────────────────────
var similarity = await new CosineSimilarityProvider().ComputeAsync(bundle, topK: 6, new Progress<string>(Console.WriteLine));
bundle.Neighbors = similarity.Neighbors;

var layoutInput = LayoutInput.FromBundle(bundle, similarity.Distances);
bundle.Layouts["swarm"] = await new ForceDirectedLayoutProvider().ComputeLayoutAsync(layoutInput, 2, new Progress<string>(Console.WriteLine));
// Wider time axis = breathing room between the cards (readability over density). The covers are
// the wall: they keep their place, the cards yield and fill the gaps (CardMass), and busy years
// take more of the axis than empty ones (DensityWarp).
bundle.Layouts["timeline"] = await new TimeHybridLayoutProvider
{
    TimeAxisWidth = 5600f,
    CoverRadius = 60f,
    CardRadiusShare = 0.55f,
    CardMass = 0.25f,
    DensityWarp = 0.85f,
}.ComputeLayoutAsync(layoutInput, 2, new Progress<string>(Console.WriteLine));
// 3D music space: UMAP in three dimensions over the e5 vectors; the Skia host renders it with
// a static perspective (depth = size/draw order), TouchDesigner gets the full xyz via the TSV
bundle.Layouts["ruimte3d"] = await new UmapLayoutProvider().ComputeLayoutAsync(layoutInput, 3, new Progress<string>(Console.WriteLine));

bundle.Save(output);
// the hi-res pages are rebuilt from scratch: what was allowed last time need not be allowed now
var hiresOut = Path.Combine(output, "media-hires");
if (Directory.Exists(hiresOut))
    Directory.Delete(hiresOut, recursive: true);
foreach (var item in bundle.Items.Where(i => i.MediaFile.Length > 0))
{
    // a sleeve from the covers cache, or a newspaper page from the pages cache (with its 1600 px
    // rendition beside it in media-hires, where the fullscreen stages look first)
    var cover = Path.Combine(coversDir, item.MediaFile);
    var free = item.Fields.TryGetValue("rechten", out var r) && r == "vrij";
    // a protected article travels as its 200 px page thumbnail only; a free one as the page itself
    var page = Path.Combine(pagesDir, free ? item.MediaFile : Path.Combine("thumb", item.MediaFile));
    File.Copy(File.Exists(cover) ? cover : page, Path.Combine(output, "media", item.MediaFile), overwrite: true);
    var pageHires = Path.Combine(pagesDir, "hires", item.MediaFile);
    if (!File.Exists(cover) && free && File.Exists(pageHires))
    {
        Directory.CreateDirectory(Path.Combine(output, "media-hires"));
        File.Copy(pageHires, Path.Combine(output, "media-hires", item.MediaFile), overwrite: true);
    }
}
Console.WriteLine($"TouchDesigner export → {TdExport.WriteTsv(bundle, output)}");

Console.WriteLine($"Bundle written to {output} ({bundle.Items.Count} items: {albums.Count} albums, " +
                  $"{movements.Count} movements, {news.Count} news; " +
                  $"{bundle.Items.Count(i => i.MediaFile.Length > 0)} covers; layouts: {string.Join(", ", bundle.Layouts.Keys)})");
return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Artist match between an album and a movement's artist list: forgiving on both sides so
// "Shocking Blue" matches "The Shocking Blue (vroeg)" and "Golden Earring" matches exactly.
static bool MatchesArtist(TimelineEvent movement, string albumArtist)
{
    if (movement.Artists is null)
        return false;
    var artist = Normalize(albumArtist);
    return movement.Artists.Select(Normalize).Any(m => m.Contains(artist) || artist.Contains(m));

    static string Normalize(string name)
    {
        var trimmed = name.ToLowerInvariant();
        var parenthesis = trimmed.IndexOf('(');
        if (parenthesis > 0) trimmed = trimmed[..parenthesis];
        trimmed = trimmed.Trim();
        return trimmed.StartsWith("the ") ? trimmed[4..] : trimmed;
    }
}

static DateOnly ParseDutchDate(string? text, int year, CultureInfo culture)
{
    if (!string.IsNullOrWhiteSpace(text) &&
        DateOnly.TryParse(text, culture, DateTimeStyles.None, out var parsed))
        return parsed;
    return new DateOnly(year, 6, 15); // mid-year when only the year is known
}

static float[] Average(List<float[]> vectors)
{
    if (vectors.Count == 0)
        return new float[8];
    var result = new float[vectors[0].Length];
    foreach (var vector in vectors)
        for (var i = 0; i < result.Length; i++)
            result[i] += vector[i] / vectors.Count;
    return result;
}

static string Slug(string text) =>
    new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(24).ToArray());

/// <summary>The bundle id of an album: its permanent key in the outside world, never its number.</summary>
static string AlbumKey(Album album) =>
    album.Mbid.Length > 0 ? $"album:mb:{album.Mbid}"
    : album.Muziekweb.Length > 0 ? $"album:mw:{album.Muziekweb}"
    : album.Discogs.Length > 0 ? $"album:dc:{album.Discogs}"
    : album.Key.Length > 0 ? $"album:x:{album.Key}"
    : throw new InvalidDataException($"album nr {album.Nr} ({album.Artist} - {album.Title}) heeft geen sleutel");

/// <summary>Sleeve cache file, keyed like the album so a cached sleeve can only ever belong to it.</summary>
static string CoverFile(Album album) => AlbumKey(album)["album:".Length..].Replace(':', '-') + ".jpg";

/// <summary>The KB's permanent article identifier out of a Delpher resolver link
/// (http://resolver.kb.nl/resolve?urn=MMSARO02:164906050:mpeg21:a00116 → MMSARO02:164906050:mpeg21:a00116).</summary>
static string KbUrn(string url)
{
    var i = url.IndexOf("urn=", StringComparison.Ordinal);
    if (i < 0)
        throw new InvalidDataException($"Delpher-link zonder urn: {url}");
    var urn = url[(i + 4)..];
    var amp = urn.IndexOf('&');
    return amp < 0 ? urn : urn[..amp];
}

static string MuziekwebCoverUrl(string albumId) => $"https://media.cdr.nl/COVER/LARGE/FRONT/{albumId}.jpg";   // 400 px, per the API documentation

/// <summary>Discogs page of a master (m181126) or release (r8591).</summary>
static string DiscogsUrl(string discogs) =>
    $"https://www.discogs.com/{(discogs[0] == 'm' ? "master" : "release")}/{discogs[1..]}";

/// <summary>The primary image of a Discogs master or release, via api.discogs.com (JSON with signed
/// image links). Paced to the anonymous limit of 25 requests a minute.</summary>
static async Task<byte[]?> TryFetchDiscogs(HttpClient http, string discogs)
{
    if (discogs.Length < 2 || (discogs[0] != 'm' && discogs[0] != 'r') || !discogs[1..].All(char.IsDigit))
        throw new InvalidDataException($"discogs-id {discogs} is geen m<master> of r<release>");
    var kind = discogs[0] == 'm' ? "masters" : "releases";
    var json = await TryGetBytes(http, $"https://api.discogs.com/{kind}/{discogs[1..]}");
    await Task.Delay(2500);
    if (json is null)
        return null;
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        return null;
    string? uri = null;
    foreach (var image in images.EnumerateArray())
    {
        var u = image.TryGetProperty("uri", out var v) ? v.GetString() : null;
        if (string.IsNullOrEmpty(u))
            continue;
        uri ??= u;
        if (image.TryGetProperty("type", out var type) && type.GetString() == "primary")
        {
            uri = u;
            break;
        }
    }
    return uri is null ? null : await TryGetBytes(http, uri);
}

static async Task<byte[]?> TryGetBytes(HttpClient http, string url)
{
    try
    {
        return await http.GetByteArrayAsync(url);
    }
    catch (HttpRequestException)
    {
        return null;
    }
}

// Rubriekskoppen zonder nieuwswaarde — ruis op een wand van voorpagina's
static bool IsNoiseHeadline(string headline)
{
    var trimmed = headline.Trim();
    string[] noise = ["Advertentie", "Familiebericht", "Weerbericht", "Radioprogramma",
        "PROGRAMMA", "Programma", "Agenda", "Officiële publicaties"];
    return noise.Any(n => trimmed.StartsWith(n, StringComparison.OrdinalIgnoreCase))
           || trimmed.Length < 8;
}

static async Task<bool> TryFetch(HttpClient http, string cachePath, string kind, string mbid)
{
    try
    {
        var bytes = await http.GetByteArrayAsync($"https://coverartarchive.org/{kind}/{mbid}/front-250");
        await File.WriteAllBytesAsync(cachePath, bytes);
        return true;
    }
    catch (HttpRequestException)
    {
        return false;
    }
}

/// <summary>One album of the editorial source. Nr = the editorial number (unique, never reused; the other
/// source files refer to it), Mbid = MusicBrainz release-group (the key), Release = the edition shown,
/// Muziekweb = Muziekweb album id, Discogs = Discogs master (m…) or release (r…), Key = editorial slug
/// when no database has the album.</summary>
sealed record Album(int Nr, string Title, string Artist, int Year, string Subgenre, string Mbid,
    float[] Features, string Description, string? Themalijn = null, bool Cover = true,
    string Release = "", string Muziekweb = "", string Discogs = "", string Key = "", float Belang = 0f);

/// <summary>One theme line of the editorial framework (themalijnen.json): the key albums refer to, the
/// framework's own name, a short label for the facet panel, its illustrative examples, and the genres of
/// movement cards that fall under it when none of our albums ties them to a line.</summary>
sealed record ThemeLine(string Key, string Naam, string Kort, string[] Voorbeelden, string[]? Stromingen = null);

/// <summary>One entry of the timeline page's events array (parsed to timeline-events.json).</summary>
sealed record TimelineEvent(int Year, string Type, string? Genre, string? Tag, string? Date,
    string? Head, string? Title, string Body, string[]? Artists);

/// <summary>One harvested newspaper headline (news-delpher.json, written by CollectionKit.Import.Delpher).</summary>
sealed record DelpherHit(int AlbumNr, string Date, string Headline, string Paper, string Url);

/// <summary>One printed page behind a Delpher article (pages/pages.json, written by CollectionKit.Import.Delpher
/// --pages): the page's KB id, its JPEG in the pages cache, and the article's rectangle on it (x y w h as fractions).</summary>
sealed record PageEntry(string Urn, string Page, string File, float[]? Rect);

/// <summary>One crafted period-style article (news.json), linked to its album (0 = link by year).</summary>
sealed record AlbumNews(string Date, int AlbumNr, string Headline, string Snippet);

/// <summary>One curated Delpher event hit (news-delpher-events.json): real headline + first
/// OCR sentence, written by CollectionKit.Import.Delpher's events mode.</summary>
sealed record EventHit(string Key, int[] AlbumNrs, string[]? Replaces, string Date, string Headline, string Paper,
    string Url, string Snippet);
