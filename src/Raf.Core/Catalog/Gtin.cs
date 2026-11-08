using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Raf.Core.Catalog;

/// <summary>
/// GS1 Global Trade Item Number. Retail feeds print the same item as GTIN-8, UPC-A (12), EAN-13 or GTIN-14;
/// the canonical form is the 14-digit, zero-padded value, so "2000000000015" and "02000000000015" are one product.
/// </summary>
public static class Gtin
{
    /// <summary>Returns the canonical 14-digit GTIN, or <c>false</c> for wrong length, non-digits or a bad check digit.</summary>
    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? gtin14)
    {
        gtin14 = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Feeds pad with spaces or dashes ("200 0000 00001 5"); anything else is rejected rather than guessed.
        Span<char> digits = stackalloc char[14];
        var count = 0;
        foreach (var c in raw)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(c) || count == 14)
            {
                return false;
            }

            digits[count++] = c;
        }

        if (count is not (8 or 12 or 13 or 14))
        {
            return false;
        }

        var value = new string(digits[..count]).PadLeft(14, '0');
        if (ComputeCheckDigit(value.AsSpan(0, 13)) != value[13] - '0')
        {
            return false;
        }

        gtin14 = value;
        return true;
    }

    public static bool IsValid(string? raw) => TryNormalize(raw, out _);

    /// <summary>GS1 mod-10: weights 3 and 1 alternate, starting with 3 at the rightmost data digit.</summary>
    public static int ComputeCheckDigit(ReadOnlySpan<char> dataDigits)
    {
        var sum = 0;
        for (var i = 0; i < dataDigits.Length; i++)
        {
            var digit = dataDigits[dataDigits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - (sum % 10)) % 10;
    }

    /// <summary>Appends the check digit to 12 data digits, producing an EAN-13.</summary>
    public static string Ean13(string twelveDigits)
    {
        if (twelveDigits.Length != 12 || !twelveDigits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Exactly 12 digits are required.", nameof(twelveDigits));
        }

        return twelveDigits + ComputeCheckDigit(twelveDigits).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The human-facing form of a canonical GTIN-14: EAN-13 for 13-digit items, GTIN-8 for short codes.</summary>
    public static string Display(string gtin14) =>
        gtin14.StartsWith("000000", StringComparison.Ordinal) ? gtin14[6..] : gtin14[0] == '0' ? gtin14[1..] : gtin14;
}
