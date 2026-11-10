using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// The printed page behind every Delpher article, for the indoor demo. For each article in
/// news-delpher*.json: the page it stands on (Delpher's article view links the page image), that
/// page as JPEG in two sizes (Delpher's own downloadimage API, no key), the page's ALTO and the
/// article's OCR from the KB resolver — and out of those two the article's rectangle on the page:
/// the ALTO text blocks whose words are the article's. Output: source/pages/&lt;urn&gt;.jpg (400 px
/// wide, the bundle thumb), source/pages/hires/&lt;urn&gt;.jpg (1600 px) and source/pages/pages.json.
///
/// Rights: Delpher's newspapers after 1879 are protected; viewing and own research are allowed,
/// a public installation needs the KB's and the rights holders' consent (docs/beeldengeluid-mvp.md).
/// Nothing here goes into git; the demo machine builds the bundle from this cache.
/// </summary>
static class Pages
{
    sealed record Entry(string Urn, string Page, string File, float[]? Rect, int Blocks, int Matched);
    sealed record Hit(string Url);

    /// <param name="force">redo every article (the page images are kept, the locating is done again)</param>
    public static async Task<int> Run(string source, bool force, HttpClient http)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        var dir = Path.Combine(source, "pages");
        var hires = Path.Combine(dir, "hires");
        var thumbs = Path.Combine(dir, "thumb");
        Directory.CreateDirectory(hires);
        Directory.CreateDirectory(thumbs);
        var indexPath = Path.Combine(dir, "pages.json");
        var byUrn = (File.Exists(indexPath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(indexPath), json)!
                : []).ToDictionary(e => e.Urn);

        var urls = new List<string>();
        foreach (var name in new[] { "news-delpher.json", "news-delpher-events.json" })
        {
            var path = Path.Combine(source, name);
            if (File.Exists(path))
                urls.AddRange(JsonSerializer.Deserialize<List<Hit>>(File.ReadAllText(path), json)!.Select(h => h.Url));
        }
        var urns = urls.Select(Urn).Distinct().ToList();
        Console.WriteLine($"{urns.Count} Delpher-artikelen, {byUrn.Count} al in {indexPath}");

        int done = 0, failed = 0;
        foreach (var urn in urns)
        {
            var file = urn.Replace(':', '-') + ".jpg";
            if (!force && byUrn.ContainsKey(urn) && File.Exists(Path.Combine(dir, file)) && File.Exists(Path.Combine(hires, file))
                && File.Exists(Path.Combine(thumbs, file)))
                continue;
            try
            {
                var known = byUrn.TryGetValue(urn, out var previous) ? previous.Page : null;
                var entry = await Fetch(http, urn, file, dir, hires, thumbs, known);
                byUrn[urn] = entry;
                done++;
                var where = entry.Rect is null ? "geen knip gevonden"
                    : $"knip {entry.Rect[0]:F2},{entry.Rect[1]:F2} {entry.Rect[2]:F2}x{entry.Rect[3]:F2}";
                Console.WriteLine($"  {urn}: {entry.Page[(entry.Page.LastIndexOf(':') + 1)..]}, {entry.Matched}/{entry.Blocks} blokken, {where}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  {urn} FAILED: {ex.Message}");
            }
            File.WriteAllText(indexPath, JsonSerializer.Serialize(byUrn.Values.OrderBy(e => e.Urn).ToList(), json));
        }
        Console.WriteLine($"{done} pagina's opgehaald, {failed} mislukt, {byUrn.Count} in {indexPath}");
        return failed == 0 ? 0 : 1;
    }

    static async Task<Entry> Fetch(HttpClient http, string urn, string file, string dir, string hires, string thumbs, string? knownPage)
    {
        // 1. which page: the article view links "download afbeelding van de pagina" (asked once)
        string page;
        if (knownPage is { Length: > 0 })
            page = knownPage;
        else
        {
            var html = await Get(http, $"https://www.delpher.nl/nl/kranten/view?coll=ddd&identifier={Uri.EscapeDataString(urn)}");
            var m = Regex.Match(html, @"downloadimage\?id=([^&""']+):image");
            if (!m.Success)
                throw new InvalidDataException("de artikelpagina noemt geen pagina-afbeelding");
            page = m.Groups[1].Value;   // e.g. ddd:010369895:mpeg21:p004
        }

        // 2. the page as JPEG, large for the fullscreen stages and small for the bundle's thumbs
        //    (kept once fetched: a second run only redoes the locating)
        if (!File.Exists(Path.Combine(hires, file)))
            await Download(http, $"https://www.delpher.nl/nl/api/downloadimage?id={page}:image&w=1600", Path.Combine(hires, file));
        if (!File.Exists(Path.Combine(dir, file)))
            await Download(http, $"https://www.delpher.nl/nl/api/downloadimage?id={page}:image&w=400", Path.Combine(dir, file));
        // ... and the thumbnail the KB's periodicals contract allows for a protected article: the whole
        // page within 200 x 200 px (120 wide keeps a broadsheet under 200 high)
        if (!File.Exists(Path.Combine(thumbs, file)))
            await Download(http, $"https://www.delpher.nl/nl/api/downloadimage?id={page}:image&w=120", Path.Combine(thumbs, file));

        // 3. where on the page: the ALTO blocks whose words are the article's OCR (both cached as
        //    text next to the images, so locating again costs no requests)
        var xmlDir = Path.Combine(dir, "xml");
        Directory.CreateDirectory(xmlDir);
        var alto = XDocument.Parse(await Cached(http, Path.Combine(xmlDir, page.Replace(':', '-') + ".alto.xml"), $"https://resolver.kb.nl/resolve?urn={page}:alto"));
        var ocr = XDocument.Parse(await Cached(http, Path.Combine(xmlDir, urn.Replace(':', '-') + ".ocr.xml"), $"https://resolver.kb.nl/resolve?urn={urn}:ocr"));
        var (rect, blocks, matched) = Locate(alto, ocr);
        return new Entry(urn, page, file, rect, blocks, matched);
    }

    /// <summary>The article's rectangle on the page as fractions (x y w h), from the text blocks
    /// whose normalised text occurs in the article's OCR; blocks far from the rest are dropped
    /// (a shared phrase in another column must not stretch the clip across the page).</summary>
    static (float[]? Rect, int Blocks, int Matched) Locate(XDocument alto, XDocument ocr)
    {
        var article = Normalize(string.Join(" ", ocr.Root!.Elements().Select(e => e.Value)));
        var articleWords = article.Split(' ').Where(w => w.Length >= 5).ToHashSet();
        XNamespace ns = alto.Root!.Name.Namespace;
        var pageEl = alto.Descendants(ns + "Page").FirstOrDefault() ?? throw new InvalidDataException("ALTO zonder Page");
        var pw = Attr(pageEl, "WIDTH");
        var ph = Attr(pageEl, "HEIGHT");
        var blocks = alto.Descendants(ns + "TextBlock").ToList();
        var hits = new List<(float X, float Y, float W, float H, int Weight)>();
        foreach (var block in blocks)
        {
            var words = block.Descendants(ns + "String")
                .Select(s => s.Attribute("SUBS_TYPE")?.Value == "HypPart2" ? ""
                           : (s.Attribute("SUBS_CONTENT") ?? s.Attribute("CONTENT"))?.Value ?? "")
                .Where(w => w.Length > 0).ToList();
            var text = Normalize(string.Join(" ", words));
            if (text.Length < 12)
                continue;
            // the whole block (three words at least, or any block of a short article such as an
            // advertisement); or — the two OCR derivations differ here and there — its head and
            // its tail; or most of its longer words, for a block the other OCR read differently
            var exact = article.Contains(text) && (words.Count >= 3 || article.Length < 400);
            var ends = text.Length > 90 && article.Contains(text[..60]) && article.Contains(text[^60..]);
            var longWords = text.Split(' ').Where(w => w.Length >= 5).ToList();
            var mostly = longWords.Count >= 6 && longWords.Count(articleWords.Contains) >= longWords.Count * 0.6;
            if (exact || ends || mostly)
                hits.Add((Attr(block, "HPOS"), Attr(block, "VPOS"), Attr(block, "WIDTH"), Attr(block, "HEIGHT"), text.Length));
        }
        if (hits.Count == 0)
            return (null, blocks.Count, 0);

        // the article's centre of gravity, by text; stragglers further than a third of the page
        // sideways or two fifths down are not the article
        var total = hits.Sum(h => h.Weight);
        var cx = hits.Sum(h => (h.X + h.W / 2) * h.Weight) / total;
        var cy = hits.Sum(h => (h.Y + h.H / 2) * h.Weight) / total;
        var kept = hits.Where(h => Math.Abs(h.X + h.W / 2 - cx) <= pw * 0.33f && Math.Abs(h.Y + h.H / 2 - cy) <= ph * 0.4f).ToList();
        if (kept.Count == 0)
            kept = hits;
        var x0 = kept.Min(h => h.X);
        var y0 = kept.Min(h => h.Y);
        var x1 = kept.Max(h => h.X + h.W);
        var y1 = kept.Max(h => h.Y + h.H);
        // a little air around the columns, inside the page
        float mx = pw * 0.008f, my = ph * 0.005f;
        x0 = Math.Max(0, x0 - mx); y0 = Math.Max(0, y0 - my);
        x1 = Math.Min(pw, x1 + mx); y1 = Math.Min(ph, y1 + my);
        return ([x0 / pw, y0 / ph, (x1 - x0) / pw, (y1 - y0) / ph], blocks.Count, kept.Count);
    }

    static float Attr(XElement e, string name) =>
        float.Parse(e.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);

    /// <summary>Letters and digits only, lower case, single spaces — the same words survive both OCR derivations.</summary>
    static string Normalize(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    static async Task<string> Get(HttpClient http, string url)
    {
        await Task.Delay(700); // one request at a time, at a walking pace
        for (var attempt = 1; ; attempt++)
        {
            var text = await http.GetStringAsync(url);
            if (text.Length > 0 || attempt == 3)
                return text;
            await Task.Delay(1500); // the resolver now and then answers empty; ask again
        }
    }

    static async Task<string> Cached(HttpClient http, string path, string url)
    {
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path);
        var text = await Get(http, url);
        if (text.Length > 0)
            await File.WriteAllTextAsync(path, text);
        return text;
    }

    static async Task Download(HttpClient http, string url, string path)
    {
        await Task.Delay(700);
        var bytes = await http.GetByteArrayAsync(url);
        if (bytes.Length < 1000 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new InvalidDataException($"geen JPEG van {url} ({bytes.Length} bytes)");
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>The KB's permanent article identifier out of a resolver link.</summary>
    static string Urn(string url)
    {
        var i = url.IndexOf("urn=", StringComparison.Ordinal);
        if (i < 0)
            throw new InvalidDataException($"Delpher-link zonder urn: {url}");
        var urn = url[(i + 4)..];
        var amp = urn.IndexOf('&');
        return amp < 0 ? urn : urn[..amp];
    }
}
