using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Raf.Api.Demo;
using Raf.Api.Persistence;
using Raf.Simulation;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Raf.IntegrationTests.Infrastructure.StackFixture))]

namespace Raf.IntegrationTests.Infrastructure;

/// <summary>
/// The whole backend once per test assembly: PostgreSQL 17 initialised with the compose role script, migrated and
/// seeded as <c>raf_migrator</c>; Redis 7; and the feed simulator in memory (chaos off, so runs are deterministic).
/// The API under test connects as <c>raf_app</c>.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    // Test-only credentials for throwaway containers.
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("raf")
        .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
            "/docker-entrypoint-initdb.d/01-roles.sh")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private WebApplication? _simulator;

    public string MigratorConnectionString => WithCredentials("raf_migrator", MigratorPassword);

    public string AppConnectionString => WithCredentials("raf_app", AppPassword);

    public string RedisConnectionString => _redis.GetConnectionString();

    public TestServer Simulator => _simulator!.GetTestServer();

    public MarketControl SimulatorControl => _simulator!.Services.GetRequiredService<MarketControl>();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await using (var dataSource = PersistenceExtensions.CreateDataSource(MigratorConnectionString))
        {
            await new MigrationRunner(dataSource, NullLogger<MigrationRunner>.Instance).MigrateAsync();
            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                PostgreSqlObjectsInstaller.Install(connection, PersistenceExtensions.HangfireSchema);
            }

            await new DemoSeeder(dataSource, TimeProvider.System, NullLogger<DemoSeeder>.Instance).RunAsync(CancellationToken.None);
        }

        _simulator = Raf.FeedSimulator.Program.BuildApp(["--Simulator:Chaos=false"], b => b.WebHost.UseTestServer());
        await _simulator.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_simulator is not null)
        {
            await _simulator.DisposeAsync();
        }

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    public NpgsqlDataSource AppDataSource() => PersistenceExtensions.CreateDataSource(AppConnectionString);

    public NpgsqlDataSource MigratorDataSource() => PersistenceExtensions.CreateDataSource(MigratorConnectionString);

    public ApiFactory CreateApi(Action<IWebHostBuilder>? configure = null) => new(this, configure);

    private string WithCredentials(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = username,
            Password = password,
            // Generous: the machine running these tests may be busy with many other containers.
            Timeout = 60,
        }.ConnectionString;
}
