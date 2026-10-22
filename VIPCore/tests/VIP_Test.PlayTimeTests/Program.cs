using Dapper;
using MySqlConnector;
using VIP_Test;

// Pure rules first: no database needed.
Check(TrialClock.CountsAsPlaying(2) && TrialClock.CountsAsPlaying(3), "T and CT count as playing");
Check(!TrialClock.CountsAsPlaying(0) && !TrialClock.CountsAsPlaying(1), "unassigned and spectators do not count");
Check(TrialClock.Remaining(3600, 3000, 300) == 300 && TrialClock.Remaining(3600, 3600, 100) == 0, "remaining never negative");
Check(TrialClock.ShouldWarn(600, 600, false) && TrialClock.ShouldWarn(30, 600, false), "warns at and below the threshold");
Check(!TrialClock.ShouldWarn(601, 600, false) && !TrialClock.ShouldWarn(300, 600, true) && !TrialClock.ShouldWarn(0, 600, false), "no early, repeated or final warning");
Check(TrialClock.IsOver(0) && !TrialClock.IsOver(1), "over at zero");
Check(TrialClock.Format(600) == "10 минут" && TrialClock.Format(60) == "1 минута" && TrialClock.Format(120) == "2 минуты" &&
      TrialClock.Format(45) == "45 секунд" && TrialClock.Format(21) == "21 секунда" && TrialClock.Format(3600) == "60 минут", "russian durations");
Check(TrialTime.ToCoreUnits(43200, 0) == 43200 && TrialTime.ToCoreUnits(43200, 2) == 12 && TrialTime.ToCoreUnits(43200, 3) == 1, "core time modes");

// Requires an empty, disposable MySQL database; never use a production VIP database.
var connectionString = Environment.GetEnvironmentVariable("VIP_TEST_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set VIP_TEST_TEST_CONNECTION to an empty test database.");
await using var db = new MySqlConnection(connectionString);
await db.OpenAsync();
if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()") != 0)
    throw new InvalidOperationException("Refusing to run against a nonempty database.");

const long now = 1_800_000_000;
var store = new TrialStore(connectionString);
Task<ClaimResult> Claim(ulong steam, int account, string source = "auto", long at = now, string? steam2 = null) =>
    store.ClaimAsync(steam, steam2 ?? $"STEAM_1:{steam % 2}:{account / 2}", account, "VIP", 3600, source, 7, 0, at);
try
{
    await db.ExecuteAsync("CREATE TABLE vip_users (account_id BIGINT, name TEXT, lastvisit BIGINT, sid BIGINT, `group` VARCHAR(64), expires BIGINT)");
    await store.InitializeAsync();
    await store.InitializeAsync(); // idempotent

    Check(await store.RegisterKnownAsync(1001, now) && !await store.RegisterKnownAsync(1001, now + 5), "first sighting is new, second is not");
    Check(await store.MarkViptestHintAsync(1001, now) && !await store.MarkViptestHintAsync(1001, now + 1), "!viptest hint is shown once");

    var first = await Claim(2001, 40);
    Check(first.Outcome == ClaimOutcome.Granted && first.Trial is { BudgetSeconds: 3600, UsedSeconds: 0, VipGroup: "VIP", Source: "auto" }, "first claim grants one hour of play");
    var again = await Claim(2001, 40, "command", now + 86400);
    Check(again.Outcome == ClaimOutcome.Existing && again.Trial!.Source == "auto", "second claim resumes, never grants again");
    Check(await db.ExecuteScalarAsync<int>("SELECT first_server_id FROM vip_test_trials WHERE steam_id64 = 2001") == 7, "place of issue is stored");

    var t1 = await store.AddUsedAsync(2001, 1800, 40, 0, now + 1);
    var t2 = await store.AddUsedAsync(2001, 1500, 40, 0, now + 2);
    Check(t1.Trial!.UsedSeconds == 1800 && t2.Trial!.UsedSeconds == 3300 && !t2.Paid, "played seconds accumulate");
    var capped = await store.AddUsedAsync(2001, 9999, 40, 0, now + 3);
    Check(capped.Trial!.UsedSeconds == 3600 && capped.Trial.Remaining == 0, "used time never exceeds the budget");

    await store.MarkWarnedAsync(2001, now + 4);
    Check((await store.GetTrialAsync(2001))!.Warned, "warning is recorded");
    await store.EndAsync(2001, "time_used", now + 5);
    Check(await store.GetTrialAsync(2001) is { Ended: true, EndReason: "time_used" }, "trial ends with a reason");
    await store.EndAsync(2001, "paid_privilege", now + 6);
    Check((await store.GetTrialAsync(2001))!.EndReason == "time_used", "an ended trial keeps its first end reason");
    var afterEnd = await store.AddUsedAsync(2001, 100, 40, 0, now + 7);
    Check(afterEnd.Trial!.UsedSeconds == 3600, "no time is added after the end");
    Check((await Claim(2001, 40, at: now + 9_000_000)).Outcome == ClaimOutcome.Existing, "an ended trial is never renewed");

    await db.ExecuteAsync("INSERT INTO vip_users VALUES (50, '', 0, 0, 'VIP', 0)");
    var paid = await Claim(2002, 50);
    Check(paid.Outcome == ClaimOutcome.BlockedPaid && await store.GetTrialAsync(2002) == null, "a paid privilege blocks and does not consume the trial");
    await db.ExecuteAsync("INSERT INTO vip_users VALUES (51, '', 0, 0, 'VIP', @e)", new { e = now - 10 });
    Check((await Claim(2003, 51)).Outcome == ClaimOutcome.Granted, "an expired privilege does not block");

    await Claim(2004, 60);
    await db.ExecuteAsync("INSERT INTO vip_users VALUES (60, '', 0, 0, 'PREMIUM', @e)", new { e = now + 86400 });
    Check((await store.AddUsedAsync(2004, 60, 60, 0, now + 10)).Paid, "a purchase during the trial is detected");
    await store.EndAsync(2004, "paid_privilege", now + 11);
    Check((await store.GetTrialAsync(2004))!.EndReason == "paid_privilege", "purchase ends the trial with its own reason");

    await db.ExecuteAsync("INSERT INTO vipcore_test VALUES ('STEAM_1:1:99', 123)");
    Check(await store.IsLegacyRedeemedAsync("STEAM_1:1:99") && !await store.IsLegacyRedeemedAsync("STEAM_1:1:98"), "legacy redemption is detected");
    var legacy = await Claim(2005, 199, steam2: "STEAM_1:1:99");
    Check(legacy.Outcome == ClaimOutcome.BlockedLegacy && await store.GetTrialAsync(2005) is { Source: "legacy", Ended: true, EndReason: "legacy" },
        "old redemption blocks and is recorded as legacy");
    Check((await Claim(2005, 199, steam2: "STEAM_1:1:99")).Outcome == ClaimOutcome.Existing, "legacy row then resumes as an ended trial");

    var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Claim(3000, 80, "auto", now + i)));
    Check(claims.Count(c => c.Outcome == ClaimOutcome.Granted) == 1 && claims.All(c => c.Trial != null) &&
          await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vip_test_trials WHERE steam_id64 = 3000") == 1,
        "simultaneous claims from several servers create exactly one trial");

    var restarted = new TrialStore(connectionString);
    await restarted.InitializeAsync();
    Check(await restarted.GetTrialAsync(2003) != null && (await restarted.ClaimAsync(2003, "STEAM_1:1:25", 51, "VIP", 3600, "auto", 7, 0, now + 99)).Outcome == ClaimOutcome.Existing,
        "restart preserves trials");

    await db.ExecuteAsync("DROP TABLE vip_test_trials");
    var failedClosed = false;
    try { await Claim(4000, 90); }
    catch (MySqlException) { failedClosed = true; }
    Check(failedClosed, "database failure propagates instead of granting");
}
finally { await db.ExecuteAsync("DROP TABLE IF EXISTS vip_test_trials, vip_test_known_players, vipcore_test, vip_users"); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
