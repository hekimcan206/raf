using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raf.Core.Catalog;

namespace Raf.UnitTests.Catalog;

public sealed class GtinTests
{
    [Theory]
    [InlineData("2000000000015", "02000000000015")]   // EAN-13
    [InlineData("02000000000015", "02000000000015")]  // GTIN-14
    [InlineData("200 0000 00001 5", "02000000000015")] // printed with spaces
    [InlineData("4006381333931", "04006381333931")]   // well-known EAN-13 check-digit example
    [InlineData("96385074", "00000096385074")]        // GTIN-8
    [InlineData("036000291452", "00036000291452")]    // UPC-A
    public void Normalises_every_length_to_the_same_GTIN_14(string raw, string expected)
    {
        Gtin.TryNormalize(raw, out var gtin).Should().BeTrue();
        gtin.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2000000000016")]     // wrong check digit
    [InlineData("200000000001")]      // 12 digits but invalid as UPC-A
    [InlineData("20000000000155")]    // wrong check digit as GTIN-14
    [InlineData("12345")]             // no such length
    [InlineData("2000000O00015")]     // letter O, not zero
    [InlineData("200000000001500")]   // 15 digits
    public void Rejects_invalid_codes(string? raw) => Gtin.IsValid(raw).Should().BeFalse();

    [Fact]
    public void Display_restores_the_printed_EAN_13() => Gtin.Display("02000000000015").Should().Be("2000000000015");

    [Fact]
    public void Display_restores_GTIN_8() => Gtin.Display("00000096385074").Should().Be("96385074");

    public static Arbitrary<string> TwelveDigits() =>
        Gen.Choose(0, 999_999).Two().Select(t => t.Item1.ToString("D6", CultureInfo.InvariantCulture) + t.Item2.ToString("D6", CultureInfo.InvariantCulture))
            .ToArbitrary();

    [Property(Arbitrary = [typeof(GtinTests)], MaxTest = 500)]
    public bool Any_payload_with_computed_check_digit_is_valid(string twelveDigits) => Gtin.IsValid(Gtin.Ean13(twelveDigits));

    [Property(Arbitrary = [typeof(GtinTests)], MaxTest = 500)]
    public bool Changing_any_single_digit_is_detected(string twelveDigits, PositiveInt position, PositiveInt delta)
    {
        // The GS1 mod-10 check detects every single-digit substitution error.
        var code = Gtin.Ean13(twelveDigits).ToCharArray();
        var i = position.Get % 13;
        code[i] = (char)('0' + ((code[i] - '0' + 1 + (delta.Get % 9)) % 10));
        return !Gtin.IsValid(new string(code));
    }
}
