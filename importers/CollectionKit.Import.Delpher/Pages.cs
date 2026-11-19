using System.Globalization;
using System.Net;
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
///
/// The article's own zones (overleg KB 12-11-2026: of an article free of rights ONLY the article
/// may be readable, not what stands around it): the rectangles Delpher's own viewer outlines,
/// read from its page view (/nl/pres/view/pageocr, the attribute data-zones per article, in the
/// pixels of the ALTO page). Only with --delpher-zones: not an official API, and Delpher's
/// robots.txt forbids that page explicitly ("Disallow: /*/pageocr"), so until the KB has answered
/// the zones come from the cache (source/pages/xml/*.pageocr.html, fetched once on 12-11-2026) and
/// are used for the examples the KB is asked to validate only. Out of those:
/// source/pages/zones/&lt;urn&gt;.jpg, the hi-res page black everywhere but in the article's zones.
/// </summary>
static class Pages
{
    /// <param name="Zones">the article's zones on its page as Delpher's viewer outlines them
    /// (x y w h as fractions of the page); null = not known</param>
    sealed record Entry(string Urn, string Page, string File, float[]? Rect, int Blocks, int Matched, float[][]? Zones = null);
    sealed record Hit(string Url);

    /// <param name="force">redo every article (the page images are kept, the locating is done again)</param>
    /// <param name="zones">the article zones from Delpher's page view (see above); off by default</param>
    public static async Task<int> Run(string source, bool force, HttpClient http, bool zones = false)
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
                // locating again (--force) keeps the article's zones: they come from Delpher's viewer,
                // not from the locating, and stay until the KB has said whether we may use them
                // (Titus, 16-11-2026); dropped only when the article turns out to be on another page
                if (previous?.Zones is { } kept && previous.Page == entry.Page)
                    entry = entry with { Zones = kept };
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

        if (!zones)
            return failed == 0 ? 0 : 1;
        // the article's own zones and the page masked to them (one page view per page, cached)
        var zonesDir = Path.Combine(dir, "zones");
        Directory.CreateDirectory(zonesDir);
        int withZones = 0, masked = 0;
        foreach (var entry in byUrn.Values.ToList())
        {
            try
            {
                var found = force || entry.Zones is null ? await ZonesOf(http, entry, dir) : entry.Zones;
                if (found is null || found.Length == 0)
                    continue;
                byUrn[entry.Urn] = entry with { Zones = found };
                withZones++;
                var maskPath = Path.Combine(zonesDir, entry.File);
                if ((force || !File.Exists(maskPath)) && Mask(Path.Combine(hires, entry.File), found, maskPath))
                    masked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {entry.Urn} zones FAILED: {ex.Message}");
            }
        }
        File.WriteAllText(indexPath, JsonSerializer.Serialize(byUrn.Values.OrderBy(e => e.Urn).ToList(), json));
        Console.WriteLine($"{withZones} van {byUrn.Count} artikelen met Delphers eigen zones, {masked} gemaskeerde pagina's bijgemaakt in {zonesDir}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The article's zones from Delpher's page view: every article on the page is a link
    /// with data-id (its urn) and data-zones (JSON: rectangles in ALTO pixels with the image they
    /// stand on, an empty list at the end); only the zones on this page count.</summary>
    static async Task<float[][]?> ZonesOf(HttpClient http, Entry entry, string dir)
    {
        var xmlDir = Path.Combine(dir, "xml");
        var view = await Cached(http, Path.Combine(xmlDir, entry.Page.Replace(':', '-') + ".pageocr.html"),
            $"https://www.delpher.nl/nl/pres/view/pageocr?coll=ddd&identifier={Uri.EscapeDataString(entry.Urn)}");
        var altoPath = Path.Combine(xmlDir, entry.Page.Replace(':', '-') + ".alto.xml");
        if (!File.Exists(altoPath))
            return null;
        var alto = XDocument.Load(altoPath);
        XNamespace ns = alto.Root!.Name.Namespace;
        var pageEl = alto.Descendants(ns + "Page").First();
        float pw = Attr(pageEl, "WIDTH"), ph = Attr(pageEl, "HEIGHT");
        var pageNo = Regex.Match(entry.Page, @"p0*(\d+)$");
        foreach (Match tag in Regex.Matches(view, @"<a\b[^>]*\bdata-zones=[^>]*>"))
        {
            var id = Regex.Match(tag.Value, @"data-id=""([^""]+)""");
            if (!id.Success || id.Groups[1].Value != entry.Urn)
                continue;
            var raw = Regex.Match(tag.Value, @"data-zones='([^']*)'|data-zones=""([^""]*)""");
            using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(raw.Groups[1].Success ? raw.Groups[1].Value : raw.Groups[2].Value));
            var zones = new List<float[]>();
            foreach (var z in doc.RootElement.EnumerateArray())
            {
                if (z.ValueKind != JsonValueKind.Object || !z.TryGetProperty("x", out var x))
                    continue;
                // an article that runs on over another page has zones there too: not on this one
                var image = z.TryGetProperty("image", out var im) ? im.GetString() ?? "" : "";
                var imageNo = Regex.Match(image, @"_0*(\d+)_access");
                if (pageNo.Success && imageNo.Success && pageNo.Groups[1].Value != imageNo.Groups[1].Value)
                    continue;
                float zx = x.GetSingle(), zy = z.GetProperty("y").GetSingle(), zw = z.GetProperty("w").GetSingle(), zh = z.GetProperty("h").GetSingle();
                if (zx < 0 || zy < 0 || zx + zw > pw * 1.02f || zy + zh > ph * 1.02f)
                    throw new InvalidDataException($"zone buiten de ALTO-pagina ({zx},{zy} {zw}x{zh} op {pw}x{ph})");
                zones.Add([zx / pw, zy / ph, zw / pw, zh / ph]);
            }
            return zones.ToArray();
        }
        return null; // the page view does not know the article
    }

    /// <summary>The hi-res page black everywhere but in the zones: what the bundle may carry of an
    /// article free of rights. False when the page is not cached.</summary>
    static bool Mask(string pagePath, float[][] zones, string outPath)
    {
        if (!File.Exists(pagePath))
            return false;
        using var page = SkiaSharp.SKBitmap.Decode(pagePath);
        if (page is null)
            return false;
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(page.Width, page.Height));
        surface.Canvas.Clear(SkiaSharp.SKColors.Black);
        foreach (var z in zones)
        {
            var r = new SkiaSharp.SKRect(z[0] * page.Width, z[1] * page.Height, (z[0] + z[2]) * page.Width, (z[1] + z[3]) * page.Height);
            surface.Canvas.DrawBitmap(page, r, r);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(outPath, data.ToArray());
        return true;
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
