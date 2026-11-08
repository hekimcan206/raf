using Hangfire.Dashboard;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Raf.Api.Infrastructure;

/// <summary>
/// Redis is not part of readiness: without it comparisons are computed uncached, which is slower but correct.
/// The check reports Degraded so dashboards show it without taking the instance out of rotation.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Degraded("Redis unavailable; serving uncached.", ex);
        }
    }
}

/// <summary>Allows the read-only dashboard for the local demo; enabled only through Jobs:DashboardEnabled.</summary>
internal sealed class OpenDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
