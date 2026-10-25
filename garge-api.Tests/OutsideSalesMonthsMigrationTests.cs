using garge_api.Migrations;
using Npgsql;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Runs the data move of the OutsideSalesMonths migration on Postgres, because it reads the month in
/// Norway with Postgres SQL. Set GARGE_TEST_POSTGRES to a connection string to run it. Each run works in
/// a schema of its own and drops it afterwards.
/// </summary>
public class OutsideSalesMonthsMigrationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("GARGE_TEST_POSTGRES");

    [Theory]
    [InlineData(1_234_500L, true)]
    [InlineData(0L, false)]
    public async Task Move_PutsTheOneFigureInTheCurrentMonthInNorway_AndBack(long other, bool moved)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "GARGE_TEST_POSTGRES is not set.");
        var ct = TestContext.Current.CancellationToken;
        var schema = "outside_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, $"""
            CREATE SCHEMA {schema};
            SET search_path TO {schema};
            CREATE TABLE "AppSettings" ("Id" int PRIMARY KEY, "OtherTurnoverInOre" bigint NOT NULL);
            CREATE TABLE "OutsideSalesMonths" ("Month" date PRIMARY KEY, "AmountInOre" bigint NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL);
            INSERT INTO "AppSettings" ("Id", "OtherTurnoverInOre") VALUES (1, {other});
            """, ct);
        try
        {
            // The month as garge reads it in Norway, before and after, in case the month turns meanwhile.
            var before = MonthInNorway();
            await ExecuteAsync(connection, OutsideSalesMonths.MoveToMonthSql, ct);
            var after = MonthInNorway();

            var rows = await RowsAsync(connection, ct);
            if (moved)
            {
                var row = Assert.Single(rows);
                Assert.Equal(other, row.Amount);
                Assert.Contains(row.Month, new[] { before, after });
                Assert.Equal(1, row.Month.Day);
            }
            else
            {
                Assert.Empty(rows);
            }

            await ExecuteAsync(connection, """UPDATE "AppSettings" SET "OtherTurnoverInOre" = 0;""", ct);
            await ExecuteAsync(connection, OutsideSalesMonths.MoveBackSql, ct);
            Assert.Equal(other, await ScalarAsync<long>(connection, """SELECT "OtherTurnoverInOre" FROM "AppSettings";""", ct));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA {schema} CASCADE;", ct);
        }
    }

    private static DateOnly MonthInNorway()
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, garge_api.Services.LocalTime.Zone));
        return new DateOnly(day.Year, day.Month, 1);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetFieldValue<T>(0);
    }

    private static async Task<List<(DateOnly Month, long Amount)>> RowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var rows = new List<(DateOnly, long)>();
        await using var command = new NpgsqlCommand("""SELECT "Month", "AmountInOre" FROM "OutsideSalesMonths";""", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetFieldValue<DateOnly>(0), reader.GetInt64(1)));
        return rows;
    }
}
