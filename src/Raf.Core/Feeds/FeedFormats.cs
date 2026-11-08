using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Raf.Core.Catalog;

namespace Raf.Core.Feeds;

/// <summary>
/// The four simulated markets each publish prices their own way, as real retailers do. The formats are synthetic
/// (see docs/adr/0002); what they reproduce are the real-world differences: lira decimals vs kuruş integers,
/// decimal comma vs point, offset-less local timestamps, GTIN-13 vs GTIN-14, barcodes vs internal SKUs, paging.
/// </summary>
public static class FeedFormats
{
    /// <summary>Türkiye has been on fixed UTC+3 all year since 2016; local timestamps without offset mean +03:00.</summary>
    public static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    public static IReadOnlyList<IFeedFormat> All { get; } =
        [new BereketJsonFormat(), new CarsiCsvFormat(), new KuzeyXmlFormat(), new TazelikPagedJsonFormat()];

    public static IFeedFormat For(string marketCode) =>
        All.FirstOrDefault(f => f.MarketCode == marketCode) ?? throw new ArgumentException($"No feed format for market '{marketCode}'.", nameof(marketCode));

    internal static long LiraToKurus(decimal lira) => (long)Math.Round(lira * 100m, 0, MidpointRounding.AwayFromZero);
}

/// <summary>Bereket Market: one JSON document, barcodes as EAN-13, prices as lira decimals, ISO-8601 timestamps with offset.</summary>
public sealed class BereketJsonFormat : IFeedFormat
{
    public string MarketCode => "bereket";

    public bool IsPaged => false;

    public FeedPage Parse(Stream content)
    {
        using var doc = JsonDocument.Parse(content);
        var items = new List<FeedItem>();
        var rejected = new List<string>();
        var index = 0;
        foreach (var element in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            index++;
            try
            {
                var barcode = element.GetProperty("barcode").GetString();
                if (!Gtin.TryNormalize(barcode, out var gtin))
                {
                    rejected.Add($"item {index}: invalid barcode '{barcode}'");
                    continue;
                }

                items.Add(new FeedItem(
                    gtin,
                    null,
                    element.TryGetProperty("name", out var name) ? name.GetString() : null,
                    element.TryGetProperty("size", out var size) ? size.GetString() : null,
                    FeedFormats.LiraToKurus(element.GetProperty("price_try").GetDecimal()),
                    element.GetProperty("updated").GetDateTimeOffset()));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                rejected.Add($"item {index}: {ex.Message}");
            }
        }

        return new FeedPage(items, rejected);
    }
}

/// <summary>
/// Çarşı Express: semicolon-separated CSV with a header, decimal comma ("34,95"), and local "dd.MM.yyyy HH:mm"
/// timestamps without an offset.
/// </summary>
public sealed class CarsiCsvFormat : IFeedFormat
{
    private static readonly string[] Header = ["EAN", "URUN_ADI", "FIYAT", "GRAMAJ", "GUNCELLEME"];

    public string MarketCode => "carsi";

    public bool IsPaged => false;

    public FeedPage Parse(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine()?.Split(';');
        if (header is null || !header.SequenceEqual(Header, StringComparer.OrdinalIgnoreCase))
        {
            throw new FormatException("Çarşı feed: unexpected header.");
        }

        var items = new List<FeedItem>();
        var rejected = new List<string>();
        var lineNo = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split(';');
            if (f.Length != Header.Length)
            {
                rejected.Add($"line {lineNo}: expected {Header.Length} fields, got {f.Length}");
                continue;
            }

            if (!Gtin.TryNormalize(f[0], out var gtin))
            {
                rejected.Add($"line {lineNo}: invalid EAN '{f[0]}'");
                continue;
            }

            // Turkish number format: "." groups thousands, "," is the decimal separator ("1.249,90"). Parsed by hand
            // because the service runs with invariant globalization (no ICU in the chiseled image).
            var normalised = f[2].Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
            if (!decimal.TryParse(normalised, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var lira))
            {
                rejected.Add($"line {lineNo}: invalid price '{f[2]}'");
                continue;
            }

            if (!DateTime.TryParseExact(f[4], "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                rejected.Add($"line {lineNo}: invalid timestamp '{f[4]}'");
                continue;
            }

            items.Add(new FeedItem(gtin, null, f[1], f[3], FeedFormats.LiraToKurus(lira), new DateTimeOffset(local, FeedFormats.TurkeyOffset)));
        }

        return new FeedPage(items, rejected);
    }
}

/// <summary>
/// Kuzey Gıda: a nightly XML price list. GTIN-14 attributes, integer kuruş, and one generation timestamp for the
/// whole list, which is therefore every item's observed_at.
/// </summary>
public sealed class KuzeyXmlFormat : IFeedFormat
{
    public string MarketCode => "kuzey";

    public bool IsPaged => false;

    public FeedPage Parse(Stream content)
    {
        // DTDs are prohibited: an external feed must not be able to trigger entity expansion or external fetches (XXE).
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(content, settings);
        var items = new List<FeedItem>();
        var rejected = new List<string>();
        DateTimeOffset? generatedAt = null;
        var index = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "fiyatListesi")
            {
                generatedAt = DateTimeOffset.Parse(reader.GetAttribute("uretim") ?? throw new FormatException("Kuzey feed: missing 'uretim'."), CultureInfo.InvariantCulture);
            }
            else if (reader.LocalName == "urun")
            {
                index++;
                if (generatedAt is null)
                {
                    throw new FormatException("Kuzey feed: <urun> before <fiyatListesi>.");
                }

                var gtinRaw = reader.GetAttribute("gtin");
                if (!Gtin.TryNormalize(gtinRaw, out var gtin))
                {
                    rejected.Add($"urun {index}: invalid gtin '{gtinRaw}'");
                    continue;
                }

                if (!long.TryParse(reader.GetAttribute("kurus"), NumberStyles.None, CultureInfo.InvariantCulture, out var kurus))
                {
                    rejected.Add($"urun {index}: invalid kurus '{reader.GetAttribute("kurus")}'");
                    continue;
                }

                items.Add(new FeedItem(gtin, null, reader.GetAttribute("ad"), reader.GetAttribute("miktar"), kurus, generatedAt.Value));
            }
        }

        return new FeedPage(items, rejected);
    }
}

/// <summary>
/// Tazelik: a paged JSON API keyed by the market's own SKUs (no barcodes), prices as strings with a decimal point,
/// sizes as free text ("1 lt", "500 gr") and Unix-seconds change timestamps. Matching goes through a curated
/// SKU mapping table.
/// </summary>
public sealed class TazelikPagedJsonFormat : IFeedFormat
{
    public string MarketCode => "tazelik";

    public bool IsPaged => true;

    public FeedPage Parse(Stream content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var items = new List<FeedItem>();
        var rejected = new List<string>();
        foreach (var p in root.GetProperty("products").EnumerateArray())
        {
            var sku = p.TryGetProperty("sku", out var s) ? s.GetString() : null;
            try
            {
                if (string.IsNullOrWhiteSpace(sku))
                {
                    rejected.Add("product without sku");
                    continue;
                }

                if (!decimal.TryParse(p.GetProperty("price").GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var lira))
                {
                    rejected.Add($"{sku}: invalid price");
                    continue;
                }

                items.Add(new FeedItem(
                    null,
                    sku,
                    p.TryGetProperty("title", out var t) ? t.GetString() : null,
                    p.TryGetProperty("size", out var z) ? z.GetString() : null,
                    FeedFormats.LiraToKurus(lira),
                    DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("lastChanged").GetInt64())));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                rejected.Add($"{sku}: {ex.Message}");
            }
        }

        return new FeedPage(items, rejected, root.GetProperty("totalPages").GetInt32());
    }
}
