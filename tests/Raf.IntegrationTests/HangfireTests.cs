using Dapper;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Raf.Api.Ingestion;
using Raf.IntegrationTests.Infrastructure;

namespace Raf.IntegrationTests;

/// <summary>Hangfire on PostgreSQL with the least-privileged application role: registration and real execution.</summary>
public sealed class HangfireTests(StackFixture stack)
{
    [Fact]
    public async Task Recurring_jobs_are_registered_per_market_with_their_schedules()
    {
        await using var api = stack.CreateApi(b => b.UseSetting("Jobs:RegisterRecurringJobs", "true"));
        _ = api.Server; // boot the host

        using var connection = api.Services.GetRequiredService<JobStorage>().GetConnection();
        var jobs = connection.GetRecurringJobs().ToDictionary(j => j.Id);

        jobs.Keys.Should().Contain(["ingest-bereket", "ingest-carsi", "ingest-kuzey", "ingest-tazelik", IngestionJobs.WatchdogJobId, IngestionJobs.PruneJobId]);
        jobs["ingest-carsi"].Cron.Should().Be("*/2 * * * *");
        jobs["ingest-bereket"].Job.Queue.Should().Be(IngestionJobs.IngestQueue);
        jobs[IngestionJobs.PruneJobId].Job.Queue.Should().Be(IngestionJobs.MaintenanceQueue);
    }

    [Fact]
    public async Task A_triggered_job_runs_on_the_Hangfire_server_and_records_a_feed_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = stack.CreateApi(b =>
        {
            b.UseSetting("Jobs:ServerEnabled", "true");
            b.UseSetting("Jobs:RegisterRecurringJobs", "true");
        });
        _ = api.Server;

        await using var ds = stack.AppDataSource();
        await using var db = await ds.OpenConnectionAsync(ct);
        var before = await db.ExecuteScalarAsync<long>("SELECT coalesce(max(id), 0) FROM raf.feed_run WHERE market_code = 'kuzey'");

        api.Services.GetRequiredService<IRecurringJobManager>().Trigger("ingest-kuzey");

        var deadline = DateTime.UtcNow.AddSeconds(45);
        string? status = null;
        while (DateTime.UtcNow < deadline && status is null)
        {
            status = await db.ExecuteScalarAsync<string?>(
                "SELECT status FROM raf.feed_run WHERE market_code = 'kuzey' AND id > @before ORDER BY id DESC LIMIT 1", new { before });
            if (status is null)
            {
                await Task.Delay(500, ct);
            }
        }

        status.Should().Be("succeeded");
    }
}
