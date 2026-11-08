using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raf.Core.Catalog;

namespace Raf.UnitTests.Catalog;

public sealed class UnitSizeTests
{
    [Theory]
    [InlineData("1 L", Dimension.Volume, 1000)]
    [InlineData("1lt", Dimension.Volume, 1000)]
    [InlineData("1,0 LT", Dimension.Volume, 1000)]
    [InlineData("1000 ml", Dimension.Volume, 1000)]
    [InlineData("33 cl", Dimension.Volume, 330)]
    [InlineData("6x1,5 L", Dimension.Volume, 9000)]
    [InlineData("4 x 200 ml", Dimension.Volume, 800)]
    [InlineData("500 gr", Dimension.Mass, 500)]
    [InlineData("500g", Dimension.Mass, 500)]
    [InlineData("2,5 kg", Dimension.Mass, 2500)]
    [InlineData("1.5 KG", Dimension.Mass, 1500)]
    [InlineData("250 mg", Dimension.Mass, 0.25)]
    [InlineData("15 adet", Dimension.Count, 15)]
    [InlineData("32 ad.", Dimension.Count, 32)]
    [InlineData("10 pcs", Dimension.Count, 10)]
    public void Parses_Turkish_and_English_spellings(string text, Dimension dimension, decimal baseAmount)
    {
        var size = UnitSize.Parse(text);
        size.Dimension.Should().Be(dimension);
        size.BaseAmount.Should().Be(baseAmount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bir litre")]
    [InlineData("1,5 adet")]
    [InlineData("0 g")]
    [InlineData("0x500 g")]
    [InlineData("500")]
    [InlineData("500 oz")]
    public void Rejects_what_it_cannot_interpret(string text) => UnitSize.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Same_size_written_differently_is_equal() =>
        UnitSize.Parse("1 L").Should().Be(UnitSize.Parse("1000 ml"));

    [Theory]
    [InlineData("1 L", 3995, 3995, "L")]
    [InlineData("500 ml", 2250, 4500, "L")]
    [InlineData("830 g", 6950, 8373, "kg")]   // 69.50 TL / 0.83 kg = 83.7349… → 83.73 TL/kg
    [InlineData("15 adet", 10500, 700, "piece")]
    public void Unit_price_is_per_kg_L_or_piece(string size, long packPrice, long expectedUnitPrice, string basis)
    {
        var s = UnitSize.Parse(size);
        s.UnitPriceKurus(packPrice).Should().Be(expectedUnitPrice);
        s.UnitPriceBasis.Should().Be(basis);
    }

    public static Arbitrary<UnitSize> Sizes() =>
        (from dimension in Gen.Elements(Dimension.Mass, Dimension.Volume, Dimension.Count)
         from amount in Gen.Choose(1, 100_000)
         select new UnitSize(dimension, amount)).ToArbitrary();

    [Property(Arbitrary = [typeof(UnitSizeTests)], MaxTest = 500)]
    public bool Canonical_text_parses_back_to_the_same_size(UnitSize size) => UnitSize.Parse(size.ToString()) == size;

    [Property(Arbitrary = [typeof(UnitSizeTests)], MaxTest = 300)]
    public bool Doubling_the_pack_at_double_the_price_keeps_the_unit_price(UnitSize size, PositiveInt price)
    {
        var doubled = new UnitSize(size.Dimension, size.BaseAmount * 2);
        var p = (long)price.Get;
        // Exact up to the rounding of each side to whole kuruş.
        return Math.Abs(size.UnitPriceKurus(p) - doubled.UnitPriceKurus(p * 2)) <= 1;
    }
}
