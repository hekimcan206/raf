using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Raf.Api.Ingestion;
using Raf.Api.Realtime;
using Raf.Simulation;

namespace Raf.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API in memory against the fixture's PostgreSQL and Redis. Feed requests go to the in-memory
/// simulator; Hangfire's server and job registration are off unless a test turns them on.
/// </summary>
public sealed class ApiFactory(StackFixture stack, Action<IWebHostBuilder>? configure) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", stack.AppConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", stack.RedisConnectionString);
        builder.UseSetting("Jobs:ServerEnabled", "false");
        builder.UseSetting("Jobs:RegisterRecurringJobs", "false");
        builder.UseSetting("OpenApi:Enabled", "true");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        foreach (var market in MarketProfiles.All)
        {
            builder.UseSetting($"Feeds:Markets:{market.Code}:Url", "http://simulator" + market.FeedPath);
        }

        builder.ConfigureTestServices(services => services
            .AddHttpClient(FeedIngestionService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stack.Simulator.CreateHandler()));
        configure?.Invoke(builder);
    }

    public FeedIngestionService Ingestion => Services.GetRequiredService<FeedIngestionService>();

    public HubConnection CreateHubConnection() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, PriceHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
}
