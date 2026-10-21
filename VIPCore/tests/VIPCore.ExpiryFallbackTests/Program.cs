using Dapper;
using MySqlConnector;
using VIPCore;

// Requires an empty, disposable MySQL database; never use a production VIP database.
var connectionString = Environment.GetEnvironmentVariable("VIPCORE_EXPIRY_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set VIPCORE_EXPIRY_TEST_CONNECTION to an empty test database.");
await using var db = new MySqlConnection(connectionString);
await db.OpenAsync();
if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()") != 0)
    throw new InvalidOperationException("Refusing to run against a nonempty database.");

const long now = 1_000_000;
const long sid = 0;

async Task<List<ExpiryOutcome>> Run(int account, bool? tableOverride = null)
{
    await using var c = new MySqlConnection(connectionString);
    await c.OpenAsync();
    var exists = tableOverride ?? await ExpiredVipProcessor.FallbackTableExistsAsync(c);
    return await ExpiredVipProcessor.ProcessAsync(c, account, sid, now, exists);
}
Task Seed(int account, string group, long expires, long sidValue = sid) =>
    db.ExecuteAsync("INSERT INTO vip_users VALUES (@account, 'p', 0, @sidValue, @group, @expires)",
        new { account, group, expires, sidValue });
Task Fallback(int account, string group, long expires) =>
    db.ExecuteAsync("INSERT INTO vip_user_fallbacks (account_id, `group`, expires, source) VALUES (@account, @group, @expires, 'test')",
        new { account, group, expires });
async Task<(string group, long expires)?> Row(int account)
{
    var r = await db.QuerySingleOrDefaultAsync<(string, long)?>("SELECT `group`, expires FROM vip_users WHERE account_id=@account", new { account });
    return r;
}
Task<int> FallbackCount(int account) =>
    db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vip_user_fallbacks WHERE account_id=@account", new { account });

try
{
    await db.ExecuteAsync("""
        CREATE TABLE vip_users (account_id BIGINT NOT NULL, name VARCHAR(64) NOT NULL, lastvisit BIGINT NOT NULL,
            sid BIGINT NOT NULL, `group` VARCHAR(64) NOT NULL, expires BIGINT NOT NULL, PRIMARY KEY (account_id, sid))
        """);

    // Without the fallback table: the old behaviour (row deleted).
    Check(!await ExpiredVipProcessor.FallbackTableExistsAsync(db), "table check: absent");
    await Seed(1, "PREMIUM", now - 10);
    var o = await Run(1);
    Check(o.Count == 1 && o[0].RestoredGroup == null && o[0].ExpiredGroup == "PREMIUM" && await Row(1) == null,
        "no fallback table: expired row deleted");
    await Seed(2, "PREMIUM", now + 10);
    Check((await Run(2)).Count == 0 && await Row(2) != null, "no fallback table: active row untouched");
    await Seed(3, "VIP", 0);
    Check((await Run(3)).Count == 0 && await Row(3) != null, "no fallback table: permanent row untouched");

    await db.ExecuteAsync("""
        CREATE TABLE vip_user_fallbacks (account_id BIGINT NOT NULL, `group` VARCHAR(64) NOT NULL, expires BIGINT NOT NULL DEFAULT 0,
            source VARCHAR(64) NOT NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (account_id))
        """);
    Check(await ExpiredVipProcessor.FallbackTableExistsAsync(db), "table check: present");

    // Expired main + permanent fallback -> fallback group, fallback consumed.
    await Seed(10, "PREMIUM", now - 10);
    await Fallback(10, "VIP", 0);
    o = await Run(10);
    Check(o.Count == 1 && o[0].RestoredGroup == "VIP" && o[0].RestoredExpires == 0 && o[0].ExpiredGroup == "PREMIUM",
        "permanent fallback: outcome reports restored group");
    Check(await Row(10) is ("VIP", 0) && await FallbackCount(10) == 0, "permanent fallback: row switched, fallback consumed");
    Check((await Run(10)).Count == 0 && await Row(10) is ("VIP", 0), "repeat call changes nothing");

    // Expired main + timed fallback still valid -> restored with its expiry.
    await Seed(11, "PREMIUM", now - 10);
    await Fallback(11, "VIP", now + 500);
    o = await Run(11);
    Check(o.Count == 1 && o[0].RestoredExpires == now + 500 && await Row(11) is ("VIP", var e11) && e11 == now + 500,
        "timed active fallback restored with its own expiry");

    // Expired main + expired timed fallback -> no row, fallback cleaned.
    await Seed(12, "PREMIUM", now - 10);
    await Fallback(12, "VIP", now - 5);
    o = await Run(12);
    Check(o.Count == 1 && o[0].RestoredGroup == null && await Row(12) == null && await FallbackCount(12) == 0,
        "expired timed fallback: no row left, fallback removed");
    Check((await Run(12)).Count == 0, "expired timed fallback: repeat call is a no-op");

    // Expired main, no fallback row -> deleted.
    await Seed(13, "VIP", now - 1);
    o = await Run(13);
    Check(o.Count == 1 && o[0].RestoredGroup == null && await Row(13) == null, "table present, no fallback: row deleted");

    // Active main keeps its fallback (not consumed early).
    await Seed(14, "PREMIUM", now + 100);
    await Fallback(14, "VIP", 0);
    Check((await Run(14)).Count == 0 && await Row(14) is ("PREMIUM", var e14) && e14 == now + 100 && await FallbackCount(14) == 1,
        "active main: fallback kept for later");

    // Other sid is untouched; only the given sid is processed.
    await Seed(15, "PREMIUM", now - 10, sidValue: 7);
    await Fallback(15, "VIP", 0);
    Check((await Run(15)).Count == 0 && await FallbackCount(15) == 1, "other sid ignored");

    // Caller may pass tableExists=false even when the table exists: the old behaviour, fallback untouched.
    await Seed(16, "PREMIUM", now - 10);
    await Fallback(16, "VIP", 0);
    o = await Run(16, tableOverride: false);
    Check(o.Count == 1 && o[0].RestoredGroup == null && await Row(16) == null && await FallbackCount(16) == 1,
        "tableExists=false: legacy delete, fallback row untouched");

    // Concurrent processing restores exactly once.
    await Seed(17, "PREMIUM", now - 10);
    await Fallback(17, "VIP", 0);
    var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Run(17)));
    Check(results.Sum(r => r.Count) == 1 && await Row(17) is ("VIP", 0) && await FallbackCount(17) == 0,
        "concurrent calls restore exactly once");

    // vip_global keeps one row per account (PK account_id, name TEXT), see 20261021_0001_create_vip_global_storage.sql.
    await db.ExecuteAsync("DROP TABLE vip_users");
    await db.ExecuteAsync("""
        CREATE TABLE vip_users (account_id BIGINT NOT NULL, name TEXT NOT NULL, lastvisit BIGINT NOT NULL,
            sid BIGINT NOT NULL, `group` VARCHAR(64) NOT NULL, expires BIGINT NOT NULL, PRIMARY KEY (account_id))
        """);
    await Seed(20, "PREMIUM", now - 10);
    await Fallback(20, "VIP", now + 900);
    o = await Run(20);
    Check(o.Count == 1 && o[0].RestoredGroup == "VIP" && await Row(20) is ("VIP", var e20) && e20 == now + 900 &&
          await FallbackCount(20) == 0, "vip_global schema: timed fallback restored");
    Check((await Run(20)).Count == 0, "vip_global schema: repeat call is a no-op");
}
finally { await db.ExecuteAsync("DROP TABLE IF EXISTS vip_user_fallbacks, vip_users"); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
