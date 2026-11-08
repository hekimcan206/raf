using System.Globalization;
using Raf.Core.Catalog;

namespace Raf.Simulation;

/// <summary>A catalogue product as the simulator knows it: identity, pack size, a reference price and a Tazelik SKU.</summary>
public sealed record SimProduct(
    int Index,
    string Gtin14,
    string Name,
    string Brand,
    UnitSize Size,
    string? SubstitutionGroup,
    string Category,
    long BasePriceKurus,
    string TazelikSku)
{
    public string Ean13 => Gtin.Display(Gtin14);
}

/// <summary>
/// The fictional product catalogue shared by the feed simulator and the API's demo seed. Brands are invented and
/// every GTIN uses the GS1 restricted-circulation prefix 20, which never identifies a real traded product, so no
/// synthetic code can collide with a real barcode.
/// </summary>
public static class SyntheticCatalog
{
    public static IReadOnlyList<SimProduct> Products { get; } = Build();

    public static SimProduct ByIndex(int index) => Products[index - 1];

    private static List<SimProduct> Build()
    {
        // (name, brand, size, group, category, reference price in kuruş)
        (string, string, string, string?, string, long)[] rows =
        [
            ("Tam Yağlı Süt", "Ovacık", "1 L", "milk-uht-whole", "dairy", 3995),
            ("Tam Yağlı Süt", "Dağ Süt", "1 L", "milk-uht-whole", "dairy", 4250),
            ("Tam Yağlı Süt", "Ovacık", "500 ml", "milk-uht-whole", "dairy", 2250),
            ("Yoğurt", "Ovacık", "1 kg", "yogurt-plain", "dairy", 8950),
            ("Yoğurt", "Dağ Süt", "1 kg", "yogurt-plain", "dairy", 8500),
            ("Yoğurt", "Dağ Süt", "500 g", "yogurt-plain", "dairy", 4795),
            ("Beyaz Peynir", "Ovacık", "500 g", "white-cheese", "dairy", 17950),
            ("Beyaz Peynir", "Dağ Süt", "600 g", "white-cheese", "dairy", 19900),
            ("Yumurta (L)", "Güneş Çiftliği", "15 adet", "eggs", "eggs", 10500),
            ("Yumurta (L)", "Güneş Çiftliği", "10 adet", "eggs", "eggs", 7450),
            ("Yumurta (M)", "Kuzey Kümes", "30 adet", "eggs", "eggs", 19900),
            ("Spagetti", "Ekin", "500 g", "pasta-spaghetti", "dry", 2450),
            ("Spagetti", "Başakça", "500 g", "pasta-spaghetti", "dry", 2295),
            ("Spagetti", "Ekin", "1 kg", "pasta-spaghetti", "dry", 4650),
            ("Baldo Pirinç", "Anadolu Sofrası", "1 kg", "rice-baldo", "dry", 8950),
            ("Baldo Pirinç", "Anadolu Sofrası", "2,5 kg", "rice-baldo", "dry", 21500),
            ("Kırmızı Mercimek", "Anadolu Sofrası", "1 kg", "lentil-red", "dry", 7450),
            ("Kırmızı Mercimek", "Ekin", "2 kg", "lentil-red", "dry", 14250),
            ("Ayçiçek Yağı", "Güneş Tarla", "1 L", "oil-sunflower", "oil", 11950),
            ("Ayçiçek Yağı", "Güneş Tarla", "2 L", "oil-sunflower", "oil", 22900),
            ("Ayçiçek Yağı", "Kızılırmak", "5 L", "oil-sunflower", "oil", 54900),
            ("Riviera Zeytinyağı", "Egeli", "1 L", "oil-olive-riviera", "oil", 39900),
            ("Riviera Zeytinyağı", "Egeli", "2 L", "oil-olive-riviera", "oil", 76900),
            ("Toz Şeker", "Tatlıova", "1 kg", "sugar", "dry", 4950),
            ("Toz Şeker", "Tatlıova", "3 kg", "sugar", "dry", 14250),
            ("Siyah Çay", "Rize Bahçe", "1 kg", "tea-black", "drinks", 24900),
            ("Siyah Çay", "Rize Bahçe", "500 g", "tea-black", "drinks", 13250),
            ("Domates Salçası", "Bahçem", "830 g", "tomato-paste", "canned", 6950),
            ("Domates Salçası", "Bahçem", "1650 g", "tomato-paste", "canned", 12900),
            ("Doğal Kaynak Suyu", "Damla", "5 L", "water", "drinks", 2950),
            ("Doğal Kaynak Suyu", "Damla", "6x1,5 L", "water", "drinks", 4495),
            ("Türk Kahvesi", "Kervan", "100 g", "coffee-turkish", "drinks", 5450),
            ("Türk Kahvesi", "Kervan", "250 g", "coffee-turkish", "drinks", 12950),
            ("Bulaşık Deterjanı", "Parıltı", "750 ml", null, "household", 6450),
            ("Tuvalet Kağıdı", "Pamukçuk", "32 adet", null, "household", 32900),
            ("Çamaşır Deterjanı", "Parıltı", "4 kg", null, "household", 39900),
        ];

        return [.. rows.Select((r, i) =>
        {
            var index = i + 1;
            var gtin = Gtin.Ean13("2000000" + index.ToString("D5", CultureInfo.InvariantCulture));
            Gtin.TryNormalize(gtin, out var gtin14);
            return new SimProduct(index, gtin14!, $"{r.Item2} {r.Item1} {r.Item3}", r.Item2, UnitSize.Parse(r.Item3), r.Item4, r.Item5, r.Item6,
                "TZ-" + (10000 + (index * 7)).ToString(CultureInfo.InvariantCulture));
        })];
    }
}
