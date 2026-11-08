using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;
using Raf.Core.Feeds;

namespace Raf.Simulation;

/// <summary>Serialises a price list in each market's own wire format (the producer side of <see cref="FeedFormats"/>).</summary>
public static class FeedWriters
{
    public const int TazelikPageSize = 15;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Feeds are UTF-8; keep Turkish letters readable instead of escaping them.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Bereket(IReadOnlyList<SimPrice> prices, DateTimeOffset generatedAt)
    {
        var payload = new
        {
            generated_at = generatedAt.ToOffset(FeedFormats.TurkeyOffset),
            items = prices.Select(p => new
            {
                barcode = p.Product.Ean13,
                name = p.Product.Name,
                size = p.Product.Size.ToString(),
                price_try = p.PriceKurus / 100m,
                updated = p.ObservedAt.ToOffset(FeedFormats.TurkeyOffset),
            }),
        };
        return JsonSerializer.Serialize(payload, Json);
    }

    public static string Carsi(IReadOnlyList<SimPrice> prices)
    {
        var sb = new StringBuilder("EAN;URUN_ADI;FIYAT;GRAMAJ;GUNCELLEME\n");
        foreach (var p in prices)
        {
            // Turkish formatting by hand: decimal comma, thousands dot, local time without offset.
            var lira = (p.PriceKurus / 100m).ToString("#,0.00", Inv).Replace(",", "_", StringComparison.Ordinal)
                .Replace('.', ',').Replace('_', '.');
            var local = p.ObservedAt.ToOffset(FeedFormats.TurkeyOffset).ToString("dd.MM.yyyy HH:mm", Inv);
            var size = p.Product.Size.ToString().Replace(" L", " lt", StringComparison.Ordinal).Replace(" g", " gr", StringComparison.Ordinal);
            sb.Append(CultureInfo.InvariantCulture, $"{p.Product.Ean13};{p.Product.Name};{lira};{size};{local}\n");
        }

        return sb.ToString();
    }

    public static string Kuzey(IReadOnlyList<SimPrice> prices, DateTimeOffset generatedAt)
    {
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            xml.WriteStartElement("fiyatListesi");
            xml.WriteAttributeString("uretim", generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", Inv));
            foreach (var p in prices)
            {
                xml.WriteStartElement("urun");
                xml.WriteAttributeString("gtin", p.Product.Gtin14);
                xml.WriteAttributeString("ad", p.Product.Name);
                xml.WriteAttributeString("miktar", p.Product.Size.ToString());
                xml.WriteAttributeString("kurus", p.PriceKurus.ToString(Inv));
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string TazelikPage(IReadOnlyList<SimPrice> prices, int page)
    {
        var totalPages = Math.Max(1, (prices.Count + TazelikPageSize - 1) / TazelikPageSize);
        var slice = prices.Skip((page - 1) * TazelikPageSize).Take(TazelikPageSize);
        var payload = new
        {
            page,
            totalPages,
            products = slice.Select(p => new
            {
                sku = p.Product.TazelikSku,
                title = p.Product.Name,
                size = p.Product.Size.ToString().Replace(" L", " lt", StringComparison.Ordinal).Replace(" g", " gr", StringComparison.Ordinal),
                price = (p.PriceKurus / 100m).ToString("0.00", Inv),
                lastChanged = p.ObservedAt.ToUnixTimeSeconds(),
            }),
        };
        return JsonSerializer.Serialize(payload, Json);
    }
}
