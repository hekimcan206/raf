using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Raf.Core.Catalog;

/// <summary>What a pack size measures. Unit prices are only comparable within one dimension.</summary>
public enum Dimension
{
    Mass,
    Volume,
    Count,
}

/// <summary>
/// A pack size normalised to a base unit: grams, millilitres or pieces. Feeds write the same size as
/// "1 L", "1lt", "1000 ml" or "1,0 LT"; after parsing they are equal, and unit prices (per kg, per L, per piece)
/// can be compared across markets and brands.
/// </summary>
public readonly partial record struct UnitSize
{
    public UnitSize(Dimension dimension, decimal baseAmount)
    {
        if (baseAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseAmount), baseAmount, "A pack size must be positive.");
        }

        Dimension = dimension;
        BaseAmount = baseAmount;
    }

    public Dimension Dimension { get; }

    /// <summary>Amount in the base unit: grams for mass, millilitres for volume, pieces for count.</summary>
    public decimal BaseAmount { get; }

    /// <summary>The unit a shopper compares by: price per kg, per L or per piece.</summary>
    public string UnitPriceBasis => Dimension switch
    {
        Dimension.Mass => "kg",
        Dimension.Volume => "L",
        _ => "piece",
    };

    private decimal BasisFactor => Dimension == Dimension.Count ? 1m : 1000m;

    /// <summary>Price per kg / L / piece in kuruş, rounded half away from zero like a shelf label.</summary>
    public long UnitPriceKurus(long packPriceKurus) =>
        (long)Math.Round(packPriceKurus * BasisFactor / BaseAmount, 0, MidpointRounding.AwayFromZero);

    public static UnitSize Grams(decimal grams) => new(Dimension.Mass, grams);

    public static UnitSize Millilitres(decimal ml) => new(Dimension.Volume, ml);

    public static UnitSize Pieces(int pieces) => new(Dimension.Count, pieces);

    public static UnitSize Parse(string text) =>
        TryParse(text, out var size) ? size : throw new FormatException($"Unrecognised pack size '{text}'.");

    /// <summary>
    /// Accepts the spellings seen in Turkish retail data: decimal comma or point, "gr"/"g"/"kg"/"mg",
    /// "lt"/"l"/"litre"/"cl"/"ml", "adet"/"ad"/"pcs", and multipacks such as "6x200 ml" or "4 x 1 L".
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out UnitSize size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = SizePattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var multiplier = match.Groups["count"].Success
            ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 1;
        var amount = decimal.Parse(match.Groups["amount"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (multiplier <= 0 || amount <= 0)
        {
            return false;
        }

        var (dimension, factor) = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "kg" => (Dimension.Mass, 1000m),
            "g" or "gr" or "gram" => (Dimension.Mass, 1m),
            "mg" => (Dimension.Mass, 0.001m),
            "l" or "lt" or "litre" or "liter" => (Dimension.Volume, 1000m),
            "cl" => (Dimension.Volume, 10m),
            "ml" => (Dimension.Volume, 1m),
            _ => (Dimension.Count, 1m),
        };

        if (dimension == Dimension.Count && amount != decimal.Truncate(amount))
        {
            return false; // "1,5 adet" is a data error, not a size.
        }

        size = new UnitSize(dimension, multiplier * amount * factor);
        return true;
    }

    /// <summary>Canonical, human-readable form ("1 L", "500 g", "1.5 kg", "10 pcs"); parses back to an equal value.</summary>
    public override string ToString()
    {
        static string N(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
        return Dimension switch
        {
            Dimension.Mass when BaseAmount >= 1000 => $"{N(BaseAmount / 1000)} kg",
            Dimension.Mass => $"{N(BaseAmount)} g",
            Dimension.Volume when BaseAmount >= 1000 => $"{N(BaseAmount / 1000)} L",
            Dimension.Volume => $"{N(BaseAmount)} ml",
            _ => $"{N(BaseAmount)} pcs",
        };
    }

    [GeneratedRegex(@"^(?:(?<count>\d{1,3})\s*[x×*]\s*)?(?<amount>\d{1,6}(?:[.,]\d{1,3})?)\s*(?<unit>kg|gram|gr|g|mg|litre|liter|lt|l|cl|ml|adet|ad|pcs|pc)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SizePattern();
}
