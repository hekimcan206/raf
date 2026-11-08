using Dapper;
using Npgsql;
using Raf.IntegrationTests.Infrastructure;

namespace Raf.IntegrationTests;

/// <summary>The least-privilege and append-only guarantees are enforced by PostgreSQL, so they are tested there.</summary>
public sealed class SchemaTests(StackFixture stack)
{
    [Fact]
    public async Task App_role_cannot_rewrite_or_delete_price_history()
    {
        await using var ds = stack.AppDataSource();
        await using var connection = await ds.OpenConnectionAsync();

        var update = () => connection.ExecuteAsync("UPDATE raf.price_observation SET price_kurus = 1");
        var delete = () => connection.ExecuteAsync("DELETE FROM raf.price_observation");
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_role_cannot_change_reference_data_or_see_migration_history()
    {
        await using var ds = stack.AppDataSource();
        await using var connection = await ds.OpenConnectionAsync();

        var insertProduct = () => connection.ExecuteAsync(
            "INSERT INTO raf.product (id, gtin, name, brand, category, size_dimension, size_base_amount) VALUES (gen_random_uuid(), '02000000099991', 'x', 'x', 'x', 'mass', 1)");
        var readMigrations = () => connection.ExecuteScalarAsync<int>("SELECT count(*) FROM raf_admin.schema_migrations");
        var createTable = () => connection.ExecuteAsync("CREATE TABLE hangfire.evil (id int)");

        (await insertProduct.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await readMigrations.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await createTable.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Database_rejects_invalid_reference_data_even_from_the_owner()
    {
        await using var ds = stack.MigratorDataSource();
        await using var connection = await ds.OpenConnectionAsync();

        var badGtin = () => connection.ExecuteAsync(
            "INSERT INTO raf.product (id, gtin, name, brand, category, size_dimension, size_base_amount) VALUES (gen_random_uuid(), '12345', 'x', 'x', 'x', 'mass', 1)");
        var zeroPrice = () => connection.ExecuteAsync(
            "INSERT INTO raf.price_observation (market_code, product_id, price_kurus, observed_at, source) VALUES ('bereket', @id, 0, now(), 'test')",
            new { id = Demo.Id(1) });

        (await badGtin.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await zeroPrice.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Seed_is_idempotent()
    {
        // History before today is fully determined by the model, so a second seed must append nothing to it.
        const string History = "SELECT count(*) FROM raf.price_observation WHERE source = 'seed' AND observed_at < date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'";
        await using var ds = stack.MigratorDataSource();
        await using var connection = await ds.OpenConnectionAsync();
        var before = await connection.ExecuteScalarAsync<long>(History);
        var products = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM raf.product");

        await new Raf.Api.Demo.DemoSeeder(ds, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Raf.Api.Demo.DemoSeeder>.Instance)
            .RunAsync(CancellationToken.None);

        before.Should().BeGreaterThan(1000);
        (await connection.ExecuteScalarAsync<long>(History)).Should().Be(before);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM raf.product")).Should().Be(products);
    }
}
