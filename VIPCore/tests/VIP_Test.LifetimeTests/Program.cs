using Dapper;
using MySqlConnector;
using VIP_Test;

// Requires an empty, disposable MySQL database; never use a production VIP database.
var connectionString = Environment.GetEnvironmentVariable("VIP_TEST_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set VIP_TEST_TEST_CONNECTION to an empty test database.");
await using var db = new MySqlConnection(connectionString);
await db.OpenAsync();
if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()") != 0)
    throw new InvalidOperationException("Refusing to run against a nonempty database.");
var store = new LifetimeTrialStore(connectionString);
const long now = 1000000;
Task<TrialSession?> Claim(string id, bool cookie = false, long at = now, int account = 1) =>
    store.GetOrClaimAsync(id, cookie, 3600, "PREMIUM", account, 0, at);
try
{
    Check(TrialTime.ToCoreUnits(3600, 0) == 3600 && TrialTime.ToCoreUnits(3600, 1) == 60 &&
        TrialTime.ToCoreUnits(3600, 2) == 1 && TrialTime.ToCoreUnits(3600, 3) == 1, "core time modes");
    await db.ExecuteAsync("CREATE TABLE vip_users (account_id INT, sid INT, expires BIGINT)");
    await store.InitializeAsync();
    Check((await Claim("new"))?.Expires == now + 3600, "first redemption");
    Check((await Claim("new", at: now + 60))?.Expires == now + 3600, "reconnect keeps original expiry");
    Check(await Claim("new", at: now + 86400) == null, "no redemption after cooldown or cookie deletion");
    await db.ExecuteAsync("INSERT INTO vipcore_test VALUES ('old', 1)");
    Check(await Claim("old") == null, "expired legacy redemption");
    Check(await Claim("cookie", true) == null, "legacy cookie blocks grant");
    Check(await Claim("cookie") == null, "cookie migrated permanently");
    var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Claim("race", at: now + i)));
    Check(claims.All(x => x != null) && claims.Select(x => x!.Expires).Distinct().Count() == 1 &&
        await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vipcore_test_sessions WHERE steamid='race'") == 1,
        "simultaneous requests share exactly one session and expiry");
    var restarted = new LifetimeTrialStore(connectionString);
    await restarted.InitializeAsync();
    Check(await restarted.GetOrClaimAsync("new", false, 3600, "PREMIUM", 1, 0, now + 86400) == null,
        "restart preserves redemption");
    await db.ExecuteAsync("INSERT INTO vipcore_test_sessions VALUES ('session-only', @expiry, 'PREMIUM')", new { expiry = now + 100 });
    Check((await Claim("session-only", true))?.Expires == now + 100, "deployed session resumes despite used cookie");
    Check(await Claim("session-only", at: now + 101) == null, "deployed expired session never renews");
    await db.ExecuteAsync("INSERT INTO vip_users VALUES (2, 0, 0)");
    Check(await Claim("paid", account: 2) == null &&
        await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vipcore_test_sessions WHERE steamid='paid'") == 0,
        "paid VIP does not consume or overwrite trial");
    await db.ExecuteAsync("DROP TABLE vipcore_test_sessions");
    var failedClosed = false;
    try { await Claim("failure"); }
    catch (MySqlException) { failedClosed = true; }
    Check(failedClosed, "database failure propagates instead of granting");
}
finally { await db.ExecuteAsync("DROP TABLE IF EXISTS vipcore_test_sessions, vipcore_test, vip_users"); }
static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
