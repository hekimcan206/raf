using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Raf.Api.Baskets;
using Raf.Api.Catalog;
using Raf.Api.Comparison;
using Raf.Api.Demo;
using Raf.Api.Features;
using Raf.Api.Infrastructure;
using Raf.Api.Ingestion;
using Raf.Api.Persistence;
using Raf.Api.Realtime;
using Scalar.AspNetCore;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var oneShot = args.Contains("--migrate-only") || args.Contains("--seed-demo");

builder.AddObservability();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<FreshnessOptions>(builder.Configuration.GetSection(FreshnessOptions.Section));
builder.Services.Configure<ComparisonSettings>(builder.Configuration.GetSection(ComparisonSettings.Section));
builder.Services.Configure<FeedOptions>(builder.Configuration.GetSection(FeedOptions.Section));
builder.Services.Configure<JobOptions>(builder.Configuration.GetSection(JobOptions.Section));

builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddSingleton<BasketStore>();
builder.Services.AddSingleton<FeedHealthStore>();
builder.Services.AddSingleton<ComparisonCache>();
builder.Services.AddSingleton<ComparisonService>();
builder.Services.AddSingleton<PriceChangeNotifier>();
builder.Services.AddSingleton<FeedIngestionService>();
builder.Services.AddTransient<IngestionJobs>();
builder.Services.AddSingleton<DemoSeeder>();

// Redis: short timeouts and no fail-fast on connect, so a Redis outage degrades the cache instead of the API.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 2000;
    options.SyncTimeout = 500;
    options.AsyncTimeout = 500;
    return ConnectionMultiplexer.Connect(options);
});

// Feed HTTP client: per-attempt timeout shorter than a hung feed, one jittered retry, and a total budget per page.
builder.Services.AddHttpClient(FeedIngestionService.HttpClientName)
    .AddResilienceHandler("feeds", (pipeline, context) =>
    {
        var feeds = context.ServiceProvider.GetRequiredService<IOptions<FeedOptions>>().Value;
        pipeline.AddTimeout(TimeSpan.FromSeconds((feeds.AttemptTimeout.TotalSeconds * 2) + 2));
        pipeline.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 1, Delay = TimeSpan.FromMilliseconds(300), UseJitter = true });
        pipeline.AddTimeout(feeds.AttemptTimeout);
    });

builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Hangfire on PostgreSQL. The API role has DML only on the hangfire schema; the migrator installs it.
var jobOptions = builder.Configuration.GetSection(JobOptions.Section).Get<JobOptions>() ?? new JobOptions();
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        options => options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("Default")),
        new PostgreSqlStorageOptions
        {
            SchemaName = PersistenceExtensions.HangfireSchema,
            PrepareSchemaIfNecessary = false,
            QueuePollInterval = TimeSpan.FromSeconds(2),
            DistributedLockTimeout = TimeSpan.FromMinutes(1),
        }));
if (jobOptions.ServerEnabled && !oneShot)
{
    builder.Services.AddHangfireServer(options =>
    {
        options.Queues = [IngestionJobs.IngestQueue, IngestionJobs.MaintenanceQueue];
        options.WorkerCount = jobOptions.WorkerCount;
        options.SchedulePollingInterval = TimeSpan.FromSeconds(5);
    });
}

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<RedisHealthCheck>("redis");

// Only for running the Expo dev server against a local API; the compose demo serves app and API from one origin.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
    .WithExposedHeaders("ETag", "X-Cache")));

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "raf API";
    document.Info.Version = "v1";
    document.Info.Description = "Compares the cost of the same grocery basket across markets, with unit-price normalisation, substitutions and per-line price freshness.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, three roles: `--migrate-only` applies migrations (and Hangfire's schema) as the schema owner;
// `--seed-demo` loads the synthetic catalogue and history; with no flag the API serves requests and runs jobs.
if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    if (!args.Contains("--seed-demo"))
    {
        return;
    }
}

if (args.Contains("--seed-demo"))
{
    await app.Services.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);
app.UseCors();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("raf API"));
}

if (jobOptions.DashboardEnabled)
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        IsReadOnlyFunc = _ => true,
        // Local demo only (port bound to loopback in compose); never enable on a reachable host.
        Authorization = [new OpenDashboardFilter()],
        DisplayStorageConnectionString = false,
    });
}

app.MapHealthEndpoints();
app.MapHub<PriceHub>(PriceHub.Path);
app.MapGroup("/api/v1")
    .WithGroupName("v1")
    .MapCatalogEndpoints()
    .MapBasketEndpoints();

if (jobOptions.RegisterRecurringJobs)
{
    var feeds = app.Services.GetRequiredService<IOptions<FeedOptions>>().Value;
    JobRegistration.RegisterRecurringJobs(app.Services.GetRequiredService<IRecurringJobManager>(), feeds, ["bereket", "carsi", "kuzey", "tazelik"]);
}

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
