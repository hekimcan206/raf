using Raf.Core.Pricing;

namespace Raf.Core.Comparison;

/// <param name="MaxOverbuyRatio">
/// A substitute may cover the requested amount with at most this much extra (0.5 = up to 50 % more). Without a cap a
/// 5 kg sack could "substitute" a 500 g bag, which is cheaper per kg but not what the shopper asked for.
/// </param>
public sealed record ComparisonOptions(decimal MaxOverbuyRatio = 0.5m)
{
    public static ComparisonOptions Default { get; } = new();
}

/// <summary>
/// Prices the same basket in every market. Pure: no I/O and no clock other than <c>now</c>, so it is unit- and
/// property-testable and its output can be cached (see <see cref="BasketComparison.ValidUntil"/>).
/// Rules, in order, per market and line:
/// <list type="number">
/// <item>the requested product with a usable (not expired) price → <see cref="LineStatus.Exact"/>;</item>
/// <item>else, if allowed, the cheapest product of the same substitution group and dimension that covers the
/// requested amount within the overbuy cap → <see cref="LineStatus.Substituted"/>;</item>
/// <item>else <see cref="LineStatus.Missing"/>: the market total is incomplete and ranked after complete ones.</item>
/// </list>
/// </summary>
public static class BasketComparer
{
    public const int MaxQuantity = 99;
    public const int MaxLines = 100;

    public static BasketComparison Compare(
        PriceSnapshot snapshot,
        IReadOnlyCollection<BasketLine> basket,
        DateTimeOffset now,
        FreshnessPolicy policy,
        ComparisonOptions? options = null)
    {
        options ??= ComparisonOptions.Default;
        var lines = Normalise(basket);

        var unknown = lines.Where(l => !snapshot.Products.ContainsKey(l.ProductId)).Select(l => l.ProductId).ToList();
        if (unknown.Count > 0)
        {
            throw new UnknownProductException(unknown);
        }

        var validUntil = DateTimeOffset.MaxValue;
        var quotes = new List<MarketQuote>(snapshot.Markets.Count);
        foreach (var market in snapshot.Markets)
        {
            var quoteLines = new List<QuoteLine>(lines.Count);
            foreach (var line in lines)
            {
                var quoted = PriceLine(snapshot, market, line, now, policy, options);
                if (quoted.ObservedAt is { } observedAt)
                {
                    var expires = policy.ExpiresAt(observedAt);
                    if (expires < validUntil)
                    {
                        validUntil = expires;
                    }
                }

                quoteLines.Add(quoted);
            }

            var missing = quoteLines.Count(l => l.Status == LineStatus.Missing);
            quotes.Add(new MarketQuote(
                market.Code,
                market.Name,
                quoteLines.Sum(l => l.LineTotalKurus),
                missing == 0,
                missing,
                quoteLines.Count(l => l.Status == LineStatus.Substituted),
                quoteLines));
        }

        // Complete baskets first (cheapest first); an incomplete total is not comparable, so those follow,
        // fewest gaps first. Market code breaks ties so the order is deterministic.
        var ranked = quotes
            .OrderByDescending(q => q.Complete)
            .ThenBy(q => q.MissingLines)
            .ThenBy(q => q.TotalKurus)
            .ThenBy(q => q.MarketCode, StringComparer.Ordinal)
            .ToList();

        return new BasketComparison(ranked, now, validUntil);
    }

    private static List<BasketLine> Normalise(IReadOnlyCollection<BasketLine> basket)
    {
        if (basket.Count == 0)
        {
            throw new ArgumentException("A basket needs at least one line.", nameof(basket));
        }

        // The same product added twice is one line; it may be substitutable only if every entry allows it.
        var merged = basket
            .GroupBy(l => l.ProductId)
            .Select(g => new BasketLine(g.Key, g.Sum(l => l.Quantity), g.All(l => l.AllowSubstitutes)))
            .ToList();

        if (merged.Count > MaxLines)
        {
            throw new ArgumentException($"A basket can hold at most {MaxLines} different products.", nameof(basket));
        }

        if (merged.Any(l => l.Quantity is < 1 or > MaxQuantity))
        {
            throw new ArgumentException($"Quantities must be between 1 and {MaxQuantity}.", nameof(basket));
        }

        return merged;
    }

    private static QuoteLine PriceLine(
        PriceSnapshot snapshot, Market market, BasketLine line, DateTimeOffset now, FreshnessPolicy policy, ComparisonOptions options)
    {
        var requested = snapshot.Products[line.ProductId];
        var exact = snapshot.PriceOf(market.Code, requested.Id);
        if (exact is not null && policy.IsUsable(exact.ObservedAt, now))
        {
            return new QuoteLine(
                requested.Id, requested.Name, line.Quantity, LineStatus.Exact,
                requested.Id, requested.Name, requested.Size.ToString(),
                line.Quantity, exact.PriceKurus, exact.PriceKurus * line.Quantity,
                requested.Size.UnitPriceKurus(exact.PriceKurus), requested.Size.UnitPriceBasis, exact.ObservedAt, null);
        }

        var reason = exact is null ? "not listed" : "price expired";
        if (line.AllowSubstitutes && requested.SubstitutionGroup is { } group)
        {
            var needed = requested.Size.BaseAmount * line.Quantity;
            var best = snapshot.GroupMembers(group)
                .Where(c => c.Id != requested.Id && c.Size.Dimension == requested.Size.Dimension)
                .Select(c => (Product: c, Price: snapshot.PriceOf(market.Code, c.Id)))
                .Where(c => c.Price is not null && policy.IsUsable(c.Price.ObservedAt, now))
                .Select(c =>
                {
                    var packs = (int)Math.Ceiling(needed / c.Product.Size.BaseAmount);
                    var overbuy = (packs * c.Product.Size.BaseAmount) - needed;
                    return (c.Product, Price: c.Price!, Packs: packs, Overbuy: overbuy, Total: packs * c.Price!.PriceKurus);
                })
                .Where(c => c.Overbuy <= needed * options.MaxOverbuyRatio)
                // Cheapest line total; then least waste; then GTIN for a deterministic pick.
                .OrderBy(c => c.Total)
                .ThenBy(c => c.Overbuy)
                .ThenBy(c => c.Product.Gtin, StringComparer.Ordinal)
                .FirstOrDefault();

            if (best.Product is not null)
            {
                return new QuoteLine(
                    requested.Id, requested.Name, line.Quantity, LineStatus.Substituted,
                    best.Product.Id, best.Product.Name, best.Product.Size.ToString(),
                    best.Packs, best.Price.PriceKurus, best.Total,
                    best.Product.Size.UnitPriceKurus(best.Price.PriceKurus), best.Product.Size.UnitPriceBasis, best.Price.ObservedAt,
                    $"requested item {reason}; {best.Packs} × {best.Product.Size} covers {requested.Size} × {line.Quantity}");
            }
        }

        return new QuoteLine(
            requested.Id, requested.Name, line.Quantity, LineStatus.Missing,
            null, null, null, 0, 0, 0, null, null, null,
            line.AllowSubstitutes && requested.SubstitutionGroup is not null
                ? $"requested item {reason}; no acceptable substitute"
                : $"requested item {reason}");
    }
}
