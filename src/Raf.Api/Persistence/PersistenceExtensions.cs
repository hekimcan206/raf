using Hangfire.PostgreSql;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Raf.Api.Persistence;

internal static class PersistenceExtensions
{
    public const string HangfireSchema = "hangfire";

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddSingleton(_ => CreateDataSource(connectionString));
        services.AddSingleton<MigrationRunner>();
        return services;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.Options ??= "-c lock_timeout=5000 -c idle_in_transaction_session_timeout=30000";

        // No Kerberos here; skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.Build();
    }

    /// <summary>
    /// Our migrations, then Hangfire's storage schema, both as the schema owner. The application role only ever gets
    /// DML on the Hangfire tables (see deploy/postgres/init), so the API runs with PrepareSchemaIfNecessary = false.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(cancellationToken);
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(cancellationToken);
        PostgreSqlObjectsInstaller.Install(connection, HangfireSchema);
    }
}

/// <summary>Readiness: can we open a connection and run a trivial query within the probe timeout?</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.", ex);
        }
    }
}
