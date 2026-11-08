using System.Diagnostics.Metrics;

namespace Raf.Api.Infrastructure;

/// <summary>Domain metrics exported through OpenTelemetry next to the ASP.NET Core and Npgsql ones.</summary>
public sealed class RafMetrics : IDisposable
{
    public const string MeterName = "Raf.Api";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _feedRuns;
    private readonly Counter<long> _priceChanges;
    private readonly Counter<long> _cacheLookups;
    private readonly Histogram<double> _comparisonMs;

    public RafMetrics()
    {
        _feedRuns = _meter.CreateCounter<long>("raf.feed.runs", description: "Feed ingestion runs by market and outcome.");
        _priceChanges = _meter.CreateCounter<long>("raf.prices.changed", description: "Current prices that changed value.");
        _cacheLookups = _meter.CreateCounter<long>("raf.comparison.cache", description: "Comparison cache lookups by result.");
        _comparisonMs = _meter.CreateHistogram<double>("raf.comparison.duration", unit: "ms", description: "Time to produce a basket comparison.");
    }

    public void FeedRun(string market, string status) =>
        _feedRuns.Add(1, new KeyValuePair<string, object?>("market", market), new KeyValuePair<string, object?>("status", status));

    public void PricesChanged(string market, int count) =>
        _priceChanges.Add(count, new KeyValuePair<string, object?>("market", market));

    public void CacheLookup(string result) => _cacheLookups.Add(1, new KeyValuePair<string, object?>("result", result));

    public void ComparisonDuration(double ms, string cache) =>
        _comparisonMs.Record(ms, new KeyValuePair<string, object?>("cache", cache));

    public void Dispose() => _meter.Dispose();
}
