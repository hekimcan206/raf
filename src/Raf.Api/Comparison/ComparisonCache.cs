using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Raf.Core.Comparison;
using StackExchange.Redis;

namespace Raf.Api.Comparison;

/// <summary>
/// Redis cache for basket comparisons with explicit, targeted invalidation (docs/adr/0003):
/// <list type="bullet">
/// <item>key = hash of the normalised basket content, so equal baskets share one entry;</item>
/// <item>for every product the result depends on (requested items and their substitution-group members) a set
/// <c>raf:cmp:idx:{productId}</c> lists the entries to drop when that product's price changes;</item>
/// <item>a global price epoch guards the race "computed from old prices, written after the invalidation": the write
/// only happens if the epoch read before loading prices is still current (atomic Lua check-and-set).</item>
/// </list>
/// Redis is an optimisation, never a dependency: every failure degrades to computing the comparison directly.
/// </summary>
public sealed partial class ComparisonCache(IConnectionMultiplexer redis, ILogger<ComparisonCache> logger)
{
    public const string EpochKey = "raf:price-epoch";
    private const string EntryPrefix = "raf:cmp:v1:";
    private const string IndexPrefix = "raf:cmp:idx:";
    private static readonly TimeSpan IndexTtl = TimeSpan.FromDays(1);

    // Atomic check-and-set: store the entry and its index memberships only if no price changed since the expected epoch was read.
    private static readonly LuaScript SetIfEpoch = LuaScript.Prepare("""
        local current = redis.call('GET', @epochKey) or '0'
        if current ~= @expected then return 0 end
        redis.call('SET', @entryKey, @value, 'PX', @ttl)
        for _, idx in ipairs(cjson.decode(@indexKeys)) do
          redis.call('SADD', idx, @entryKey)
          redis.call('PEXPIRE', idx, @indexTtl)
        end
        return 1
        """);

    // Bump the epoch first, then drop every entry indexed under the changed products.
    private static readonly LuaScript Invalidate = LuaScript.Prepare("""
        redis.call('INCR', @epochKey)
        local dropped = 0
        for _, idx in ipairs(cjson.decode(@indexKeys)) do
          local entries = redis.call('SMEMBERS', idx)
          for _, entry in ipairs(entries) do dropped = dropped + redis.call('DEL', entry) end
          redis.call('DEL', idx)
        end
        return dropped
        """);

    public static string KeyFor(IReadOnlyCollection<BasketLine> lines)
    {
        // Same normalisation as the comparer: merged by product, sorted, so order and duplicates do not matter.
        var canonical = string.Join(';', lines
            .GroupBy(l => l.ProductId)
            .Select(g => (Id: g.Key, Qty: g.Sum(l => l.Quantity), Sub: g.All(l => l.AllowSubstitutes)))
            .OrderBy(l => l.Id)
            .Select(l => $"{l.Id:N}:{l.Qty}:{(l.Sub ? 1 : 0)}"));
        return EntryPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    public async Task<BasketComparison?> GetAsync(string key)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<BasketComparison>((string)value!, JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            LogCacheError(ex, "get");
            return null;
        }
    }

    /// <summary>Reads the epoch before loading prices; null when Redis is unavailable (then nothing is cached).</summary>
    public async Task<string?> CurrentEpochAsync()
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(EpochKey);
            return value.IsNullOrEmpty ? "0" : (string)value!;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogCacheError(ex, "epoch");
            return null;
        }
    }

    /// <returns>True when stored; false when prices changed meanwhile (the epoch moved) or Redis failed.</returns>
    public async Task<bool> TrySetAsync(string key, BasketComparison comparison, IEnumerable<Guid> dependsOn, string epoch, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            var indexKeys = JsonSerializer.Serialize(dependsOn.Select(id => IndexPrefix + id.ToString("N")));
            var result = await redis.GetDatabase().ScriptEvaluateAsync(SetIfEpoch, new
            {
                epochKey = (RedisKey)EpochKey,
                entryKey = (RedisKey)key,
                expected = epoch,
                value = JsonSerializer.Serialize(comparison, JsonSerializerOptions.Web),
                ttl = (long)ttl.TotalMilliseconds,
                indexTtl = (long)IndexTtl.TotalMilliseconds,
                indexKeys,
            });
            return (long)result == 1;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogCacheError(ex, "set");
            return false;
        }
    }

    /// <summary>Drops every cached comparison that depends on one of the products. Returns how many entries went.</summary>
    public async Task<long> InvalidateProductsAsync(IReadOnlyCollection<Guid> productIds)
    {
        if (productIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var indexKeys = JsonSerializer.Serialize(productIds.Select(id => IndexPrefix + id.ToString("N")));
            var result = await redis.GetDatabase().ScriptEvaluateAsync(Invalidate, new { epochKey = (RedisKey)EpochKey, indexKeys });
            return (long)result;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Entries expire on their own (TTL ≤ first price expiry); log loudly, a stale cache is a correctness issue.
            LogInvalidationFailed(ex, productIds.Count);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comparison cache {Operation} failed; serving uncached")]
    private partial void LogCacheError(Exception ex, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache invalidation for {Count} products failed; entries live until their TTL")]
    private partial void LogInvalidationFailed(Exception ex, int count);
}
