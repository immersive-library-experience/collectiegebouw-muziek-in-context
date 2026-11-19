using System.Text.Json;
using System.Xml.Linq;

// usage: CollectionKit.Import.Delpher --source <dir with albums.json[/albums-nl.json]>
//        [--per-album 2] [--events-only] [--force] [--kb-online] [--pages [--delpher-zones]]
//
// FROM THE CACHE ONLY unless --kb-online. The KB's servers forbid automated visitors in their
// robots.txt (www.delpher.nl, resolver.kb.nl and jsru.kb.nl: "User-agent: * / Disallow: /",
// checked 12-11-2026), and in the meeting of 12-11-2026 the KB said crawlers bring Delpher down.
// Until the KB has agreed a route (KB Dataservices, API key), every request to a KB server needs
// that flag on purpose; without it the importer reads what is cached and says what is missing.
//
// Queries the KB's public SRU endpoint (jsru.kb.nl, collection DDD_artikel — digitised Dutch
// newspapers; broad up to 1995, and from 1996 to the end of 2005 the Algemeen Dagblad only,
// counted 01-10-2026) for each album's artist within the release year, and writes
// news-delpher.json next to the album sources. Incremental: albums already present are skipped
// unless --force. --events-only skips the album pass.
//
// When events-delpher.json exists (curated queries for iconic moments: Kent State, de autoloze
// zondag, de Watersnood, de val van de Muur…), each event is searched within its date window,
// restricted to real articles (type=artikel), and the candidates are RANKED on the prose their
// OCR actually contains — the top hits get their real headline + opening sentence, so the
// wall's newspaper cards carry the words that appeared in print. Output: news-delpher-events.json.
// A query's `replaces` substrings tell the bundle importer which editorial cards to retire.

var source = Arg("--source") ?? throw new ArgumentException("--source <dir> is required");
var perAlbum = int.Parse(Arg("--per-album") ?? "2");
var force = args.Contains("--force");
var eventsOnly = args.Contains("--events-only");

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var albums = JsonSerializer.Deserialize<List<Album>>(File.ReadAllText(Path.Combine(source, "albums.json")), json)!;
var albumsNlPath = Path.Combine(source, "albums-nl.json");
if (File.Exists(albumsNlPath))
    albums.AddRange(JsonSerializer.Deserialize<List<Album>>(File.ReadAllText(albumsNlPath), json)!);

var online = args.Contains("--kb-online");
using var http = new HttpClient(new KbGate(online)) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CollectionKit-demo/1.0 (collection disclosure prototype)");
if (!online)
    Console.WriteLine("Alleen de cache: verzoeken aan de KB (delpher.nl, resolver.kb.nl, jsru.kb.nl) alleen met --kb-online " +
                      "(robots.txt: Disallow: /; eerst afstemmen met KB Dataservices).");
XNamespace srw = "http://www.loc.gov/zing/srw/";
XNamespace dc = "http://purl.org/dc/elements/1.1/";

// --pages: the printed page behind every article and the article's place on it, cached in
// source/pages for the indoor demo (Pages.cs; rights: see there)
if (args.Contains("--pages"))
    return await Pages.Run(source, force, http, zones: args.Contains("--delpher-zones"));

// ── Album pass: the artist in the papers of the release year (headline + deep link) ────────────
if (!eventsOnly)
{
    var outputPath = Path.Combine(source, "news-delpher.json");
    var existing = File.Exists(outputPath)
        ? JsonSerializer.Deserialize<List<DelpherHit>>(File.ReadAllText(outputPath), json)!
        : [];
    var byAlbum = existing.GroupBy(h => h.AlbumNr).ToDictionary(g => g.Key, g => g.ToList());

    foreach (var album in albums)
    {
        if (!force && byAlbum.ContainsKey(album.Nr))
            continue;

        // Artist within the release year. Artist alone (not the album title) because newspapers
        // wrote about performances and personalities far more than about record releases.
        // NB the jsru CQL parser only combines clauses when EACH is parenthesised —
        // unparenthesised forms silently return 0 records.
        var cql = $"(\"{album.Artist.Replace("\"", "")}\") AND (date within \"01-01-{album.Year} 31-12-{album.Year}\") AND (type=artikel)";
        try
        {
            await Task.Delay(600); // be polite to the KB endpoint
            var hits = new List<DelpherHit>();
            var (total, records) = await Search(cql, perAlbum);
            foreach (var r in records)
                hits.Add(new DelpherHit(album.Nr, r.Date, r.Headline, r.Paper, r.Url));
            byAlbum[album.Nr] = hits;
            Console.WriteLine($"  [{album.Nr}] {album.Artist} ({album.Year}): {hits.Count} of {total} artikelen");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{album.Nr}] {album.Artist} FAILED: {ex.Message}");
        }
    }

    var all = byAlbum.Values.SelectMany(h => h).OrderBy(h => h.AlbumNr).ToList();
    File.WriteAllText(outputPath, JsonSerializer.Serialize(all, json));
    Console.WriteLine($"{all.Count} Delpher-krantenkoppen → {outputPath}");
}

// ── Curated event queries: the iconic moments get REAL front-page words ─────────────────────────
var eventsPath = Path.Combine(source, "events-delpher.json");
if (File.Exists(eventsPath))
{
    var events = JsonSerializer.Deserialize<List<EventQuery>>(File.ReadAllText(eventsPath), json)!;
    var eventsOutPath = Path.Combine(source, "news-delpher-events.json");
    var existingEvents = File.Exists(eventsOutPath)
        ? JsonSerializer.Deserialize<List<EventHit>>(File.ReadAllText(eventsOutPath), json)!
        : [];
    var byKey = existingEvents.GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.ToList());

    foreach (var query in events)
    {
        if (!force && byKey.ContainsKey(query.Key))
            continue;
        var terms = query.Query.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var phrases = terms.Select(q => $"(\"{q.Replace("\"", "")}\")");
        var cql = $"{string.Join(" AND ", phrases)} AND (date within \"{query.From} {query.To}\") AND (type=artikel)";
        try
        {
            await Task.Delay(600);
            // Relevance order from the SRU is not editorial quality: fetch a wider net and rank
            // on what the OCR holds — a card needs a headline AND an opening sentence
            var (total, records) = await Search(cql, Math.Max(20, query.Max * 8));
            var candidates = new List<(int Score, EventHit Hit)>();
            foreach (var r in records.Where(r => !IsNoiseHeadline(r.Headline)).Take(16))
            {
                await Task.Delay(400);
                var (snippet, proseLength) = await OpeningSentence(r.Url);
                var headlineOk = r.Headline.Length is >= 12 and <= 110 && r.Headline.Any(char.IsLower);
                // A headline that names the moment beats a longer article that merely mentions it
                var onTopic = terms.Any(t => r.Headline.Contains(t, StringComparison.OrdinalIgnoreCase));
                var score = Math.Min(proseLength, 600) + (headlineOk ? 150 : 0) + (snippet.Length > 0 ? 200 : 0) + (onTopic ? 250 : 0);
                candidates.Add((score, new EventHit(
                    Key: query.Key,
                    AlbumNrs: query.AlbumNrs ?? [],
                    Replaces: query.Replaces ?? [],
                    Date: r.Date,
                    Headline: r.Headline,
                    Paper: r.Paper,
                    Url: r.Url,
                    Snippet: snippet)));
            }
            var chosen = candidates.OrderByDescending(c => c.Score).Take(Math.Max(1, query.Max)).Select(c => c.Hit).ToList();
            byKey[query.Key] = chosen;
            var withSnippet = chosen.Count(h => h.Snippet.Length > 0);
            Console.WriteLine($"  [{query.Key}] \"{query.Query}\": {chosen.Count} gekozen uit {candidates.Count} kandidaten ({total} treffers), {withSnippet} met openingszin");
            foreach (var h in chosen)
                Console.WriteLine($"      {h.Date} {h.Paper}: {h.Headline[..Math.Min(70, h.Headline.Length)]}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{query.Key}] FAILED: {ex.Message}");
        }
    }

    var allEvents = byKey.Values.SelectMany(h => h).OrderBy(h => h.Date).ToList();
    File.WriteAllText(eventsOutPath, JsonSerializer.Serialize(allEvents, json));
    Console.WriteLine($"{allEvents.Count} event-artikelen ({allEvents.Count(h => h.Snippet.Length > 0)} met openingszin) → {eventsOutPath}");
}
return 0;

// One SRU search: total count + the usable records (title, date, paper, human-facing URL)
async Task<(int Total, List<SruRecord> Records)> Search(string cql, int maximumRecords)
{
    // recordSchema=ddd carries the newspaper's own title (the masthead) and edition/page;
    // the default schema only names the publisher ("Van der Loeff", "s.n.")
    var url = "http://jsru.kb.nl/sru/sru?version=1.2&operation=searchRetrieve&x-collection=DDD_artikel" +
              $"&recordSchema=ddd&maximumRecords={maximumRecords}&query={Uri.EscapeDataString(cql)}";
    var doc = XDocument.Parse(await http.GetStringAsync(url));
    var total = (int?)doc.Descendants(srw + "numberOfRecords").FirstOrDefault() ?? 0;
    var records = new List<SruRecord>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var record in doc.Descendants(srw + "recordData"))
    {
        string? Field(string localName) =>
            record.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        var title = Field("title");
        var date = Field("date");
        var identifier = Field("identifier");
        if (title is null || date is null || identifier is null)
            continue;
        // the record does not always name the paper; Delpher's own article view does (its page
        // title reads "<krant> » 27 aug 1959 - Art. 116 | Delpher") — the publisher is the last resort
        var paper = Field("papertitle") ?? await PaperTitleFromView(identifier) ?? Field("publisher");
        // The same article is often indexed once per edition: keep the first
        if (!seen.Add(title.Trim()))
            continue;
        records.Add(new SruRecord(
            Date: DateOnly.FromDateTime(DateTime.Parse(date.Split(' ')[0].Replace('/', '-'))).ToString("yyyy-MM-dd"),
            Headline: title.Trim(),
            Paper: TidyPaper(paper ?? "onbekende krant"),
            // Human-facing Delpher view = the resolver link without the :ocr suffix
            Url: identifier.EndsWith(":ocr") ? identifier[..^4] : identifier));
    }
    return (total, records);
}

// The article's OCR (identifier + :ocr → <text><title/><p/>…</text>, public metadata). Returns
// the opening sentence(s) of the first paragraph that reads like prose, capped for a card, and
// the length of that paragraph as a quality signal. One sentence stays well within citaatrecht.
async Task<(string Snippet, int ProseLength)> OpeningSentence(string url)
{
    try
    {
        var ocr = XDocument.Parse(await http.GetStringAsync(url + ":ocr"));
        var paragraph = ocr.Descendants("p").Select(p => p.Value.Trim())
            .FirstOrDefault(p => p.Length >= 80 && ProseRatio(p) >= 0.86f);
        if (paragraph is null)
            return ("", 0);
        var cleaned = paragraph.TrimStart('.', '*', '•', '\'', '"', '„', '-', '—', ' ');
        // Up to two sentences, never more than ~220 characters
        var snippet = cleaned;
        var firstEnd = cleaned.IndexOf(". ", StringComparison.Ordinal);
        if (firstEnd > 30)
        {
            var secondEnd = cleaned.IndexOf(". ", firstEnd + 2, StringComparison.Ordinal);
            snippet = secondEnd > firstEnd && secondEnd < 200 ? cleaned[..(secondEnd + 1)] : cleaned[..(firstEnd + 1)];
        }
        if (snippet.Length > 220)
            snippet = snippet[..219].TrimEnd() + "…";
        return (snippet, paragraph.Length);
    }
    catch
    {
        return ("", 0); // OCR is best-effort — headline + paper + date is already a card
    }
}

// The newspaper's title from Delpher's article view, when the SRU record has none (see tools/delpher_paper_titles.py
// for the repair of sets harvested before this existed)
async Task<string?> PaperTitleFromView(string identifier)
{
    try
    {
        var urn = identifier.EndsWith(":ocr") ? identifier[..^4] : identifier;
        var i = urn.IndexOf("urn=", StringComparison.Ordinal);
        if (i >= 0) urn = urn[(i + 4)..];
        await Task.Delay(700);
        var page = await http.GetStringAsync($"https://www.delpher.nl/nl/kranten/view?coll=ddd&identifier={Uri.EscapeDataString(urn)}");
        var m = System.Text.RegularExpressions.Regex.Match(page, @"<title>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return null;
        var t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        var cut = t.IndexOf('»');
        return cut > 0 ? TidyPaper(t[..cut].Trim()) : null;
    }
    catch
    {
        return null;
    }
}

// Delpher's paper titles carry cataloguing tails ("De Telegraaf : nieuws- en advertentieblad",
// "Leeuwarder courant : hoofdblad van Friesland"); the masthead wants the name alone
static string TidyPaper(string paper)
{
    var name = paper.Split(" : ", 2)[0].Split(" / ", 2)[0].Trim().TrimEnd('.', ',');
    // one masthead, one name: Delpher catalogues some years of a paper under a variant
    name = name switch { "Dagblad De Telegraaf" => "De Telegraaf", _ => name };
    return name.Length > 0 ? name : paper;
}

// Share of characters that belong in running text; OCR of mastheads/datelines is symbol soup
static float ProseRatio(string text) =>
    (float)text.Count(c => char.IsLetter(c) || c == ' ' || ",.;:'\"-()".Contains(c)) / text.Length;

static bool IsNoiseHeadline(string headline)
{
    var trimmed = headline.Trim();
    string[] noise = ["Advertentie", "Familiebericht", "Weerbericht", "Radioprogramma", "Programma", "PROGRAMMA",
        "Agenda", "Officiële publicaties", "Titel", "Fournaal", "Journaal", "Tegenstelling", "Succes",
        // listings and rubric heads: no story behind them
        "TV", "Tv", "T.V.", "Radio", "radio", "Televisie", "COMMENTAAR", "Commentaar", "ANDERE OPINIES",
        "STUKGOED", "special", "sport", "Sport", "BINNENLAND", "BUITENLAND", "KORT", "Kort"];
    return trimmed.Length < 10 || noise.Any(n => trimmed.Equals(n, StringComparison.OrdinalIgnoreCase)
                                                || trimmed.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase));
}

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>Lets a request to a KB server (kb.nl, delpher.nl) through only when asked for with
/// --kb-online; everything else is answered from the caches before it gets here.</summary>
sealed class KbGate(bool online) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "";
        var kb = host == "kb.nl" || host.EndsWith(".kb.nl") || host == "delpher.nl" || host.EndsWith(".delpher.nl");
        if (kb && !online)
            throw new InvalidOperationException($"niet in de cache, en {host} alleen met --kb-online (robots.txt: Disallow: /)");
        return base.SendAsync(request, cancellationToken);
    }
}

sealed record Album(int Nr, string Title, string Artist, int Year);

sealed record SruRecord(string Date, string Headline, string Paper, string Url);

sealed record DelpherHit(int AlbumNr, string Date, string Headline, string Paper, string Url);

/// <summary>One curated event query (events-delpher.json). Dates as dd-MM-yyyy (SRU format).
/// AlbumNrs (editorial album numbers) links the resulting articles to specific albums (empty = link by year); Replaces
/// lists substrings of editorial headlines that these real articles supersede.</summary>
sealed record EventQuery(string Key, string Query, string From, string To, int Max, int[]? AlbumNrs, string[]? Replaces);

/// <summary>One event search hit with the real headline and (best-effort) opening sentence.</summary>
sealed record EventHit(string Key, int[] AlbumNrs, string[] Replaces, string Date, string Headline, string Paper,
    string Url, string Snippet);
