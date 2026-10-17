using garge_api.Migrations;
using Npgsql;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Runs the address split of the SplitCompanyAddress migration on Postgres, because its regular
/// expressions are Postgres SQL. Set GARGE_TEST_POSTGRES to a connection string to run it. Each run
/// works in a schema of its own and drops it afterwards.
/// </summary>
public class SplitCompanyAddressMigrationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("GARGE_TEST_POSTGRES");

    [Fact]
    public async Task Split_TakesThePostalCodeAndPlaceFromTheLastComma_AndLeavesOtherFormsWhole()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "GARGE_TEST_POSTGRES is not set.");
        var ct = TestContext.Current.CancellationToken;
        var schema = "split_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, $"""
            CREATE SCHEMA {schema};
            SET search_path TO {schema};
            CREATE TABLE "AppSettings" ("Id" int PRIMARY KEY, "StreetAddress" varchar(500) NOT NULL,
                "PostalCode" varchar(20) NOT NULL DEFAULT '', "AddressLocality" varchar(100) NOT NULL DEFAULT '');
            INSERT INTO "AppSettings" ("Id", "StreetAddress") VALUES
                (1, 'Mårvegen 21a, 4347 Lye'),
                (2, 'Gate 1, Bygg B, 0150 Oslo'),
                (3, 'Mårvegen 21a,4347  Lye '),
                (4, 'Mårvegen 21a 4347 Lye'),
                (5, ''),
                (6, 'Postboks 12 Lye');
            """, ct);
        try
        {
            await ExecuteAsync(connection, SplitCompanyAddress.SplitAddressSql, ct);
            var split = await RowsAsync(connection, ct);

            Assert.Equal(("Mårvegen 21a", "4347", "Lye"), split[1]);
            Assert.Equal(("Gate 1, Bygg B", "0150", "Oslo"), split[2]);
            Assert.Equal(("Mårvegen 21a", "4347", "Lye"), split[3]);
            Assert.Equal(("Mårvegen 21a 4347 Lye", "", ""), split[4]);
            Assert.Equal(("", "", ""), split[5]);
            Assert.Equal(("Postboks 12 Lye", "", ""), split[6]);

            await ExecuteAsync(connection, SplitCompanyAddress.JoinAddressSql, ct);
            var joined = await RowsAsync(connection, ct);

            Assert.Equal("Mårvegen 21a, 4347 Lye", joined[1].Street);
            Assert.Equal("Gate 1, Bygg B, 0150 Oslo", joined[2].Street);
            Assert.Equal("Mårvegen 21a 4347 Lye", joined[4].Street);
            Assert.Equal("", joined[5].Street);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA {schema} CASCADE;", ct);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<int, (string Street, string PostalCode, string Locality)>> RowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var rows = new Dictionary<int, (string, string, string)>();
        await using var command = new NpgsqlCommand("""SELECT "Id", "StreetAddress", "PostalCode", "AddressLocality" FROM "AppSettings";""", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
        return rows;
    }
}
