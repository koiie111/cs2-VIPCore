using Dapper;
using MySqlConnector;

namespace VIP_Test;

public sealed class Trial
{
    public ulong SteamId64 { get; set; }
    public long AccountId { get; set; }
    public string VipGroup { get; set; } = "";
    public int BudgetSeconds { get; set; }
    public int UsedSeconds { get; set; }
    public string Source { get; set; } = "";
    public bool Warned { get; set; }
    public bool Ended { get; set; }
    public string? EndReason { get; set; }
    public int Remaining => TrialClock.Remaining(BudgetSeconds, UsedSeconds, 0);
}

public enum ClaimOutcome { Granted, Existing, BlockedPaid, BlockedLegacy }

public sealed record ClaimResult(ClaimOutcome Outcome, Trial? Trial);

public sealed record TickResult(Trial? Trial, bool Paid);

/// <summary>
/// Trial storage in the shared VIP database: one row per SteamID in vip_test_trials is both the «once per
/// SteamID» lock and the log used to measure trial → purchase conversion. Never grants on a database failure.
/// </summary>
public sealed class TrialStore(string connectionString)
{
    private const string Columns = @"steam_id64 AS SteamId64, account_id AS AccountId, vip_group AS VipGroup,
        budget_seconds AS BudgetSeconds, used_seconds AS UsedSeconds, source AS Source,
        warned_10m_at IS NOT NULL AS Warned, ended_at IS NOT NULL AS Ended, end_reason AS EndReason";

    public async Task InitializeAsync()
    {
        await using var db = await OpenAsync();
        // Same DDL as php/migrations/sql/20261021_0001_create_vip_global_storage.sql.
        await db.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS `vip_test_trials` (
              `steam_id64` BIGINT UNSIGNED NOT NULL,
              `account_id` BIGINT UNSIGNED NOT NULL,
              `vip_group` VARCHAR(64) NOT NULL,
              `budget_seconds` INT UNSIGNED NOT NULL,
              `used_seconds` INT UNSIGNED NOT NULL DEFAULT 0,
              `source` ENUM('auto','command','legacy') NOT NULL,
              `first_server_id` INT NOT NULL DEFAULT 0,
              `granted_at` DATETIME NOT NULL,
              `warned_10m_at` DATETIME NULL,
              `ended_at` DATETIME NULL,
              `end_reason` ENUM('time_used','paid_privilege','legacy','admin') NULL,
              `updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
              PRIMARY KEY (`steam_id64`),
              KEY `idx_vip_test_trials_granted` (`granted_at`),
              KEY `idx_vip_test_trials_account` (`account_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS `vip_test_known_players` (
              `steam_id64` BIGINT UNSIGNED NOT NULL,
              `first_seen_at` DATETIME NOT NULL,
              `source` ENUM('seed','live') NOT NULL,
              `viptest_hint_shown_at` DATETIME NULL,
              PRIMARY KEY (`steam_id64`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS `vipcore_test` (
              `steamid` VARCHAR(255) NOT NULL, `end_time` BIGINT NOT NULL, PRIMARY KEY (`steamid`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;");
    }

    /// <summary>True when this call is the first time the player is seen anywhere: a new player.</summary>
    public async Task<bool> RegisterKnownAsync(ulong steamId64, long now)
    {
        await using var db = await OpenAsync();
        return await db.ExecuteAsync(
            "INSERT IGNORE INTO vip_test_known_players (steam_id64, first_seen_at, source) VALUES (@steamId64, FROM_UNIXTIME(@now), 'live')",
            new { steamId64, now }) == 1;
    }

    /// <summary>True only for the first call per player: the «!viptest is available» hint is shown once.</summary>
    public async Task<bool> MarkViptestHintAsync(ulong steamId64, long now)
    {
        await using var db = await OpenAsync();
        return await db.ExecuteAsync(
            "UPDATE vip_test_known_players SET viptest_hint_shown_at = FROM_UNIXTIME(@now) WHERE steam_id64 = @steamId64 AND viptest_hint_shown_at IS NULL",
            new { steamId64, now }) == 1;
    }

    /// <summary>Redeemed under VIP Test 2.x (vipcore_test): such a player must not be offered a trial again.</summary>
    public async Task<bool> IsLegacyRedeemedAsync(string steamId2)
    {
        await using var db = await OpenAsync();
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vipcore_test WHERE steamid = @steamId2", new { steamId2 }) > 0;
    }

    public async Task<Trial?> GetTrialAsync(ulong steamId64)
    {
        await using var db = await OpenAsync();
        return await GetAsync(db, steamId64);
    }

    /// <param name="steamId2">STEAM_x:y:z form, to honour redemptions recorded by VIP Test 2.x in vipcore_test.</param>
    public async Task<ClaimResult> ClaimAsync(ulong steamId64, string steamId2, int accountId, string group,
        int budgetSeconds, string source, int firstServerId, int sid, long now)
    {
        await using var db = await OpenAsync();
        var existing = await GetAsync(db, steamId64);
        if (existing != null) return new ClaimResult(ClaimOutcome.Existing, existing);

        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vipcore_test WHERE steamid = @steamId2", new { steamId2 }) > 0)
        {
            // Redeemed under the old per-server plugin: record it so the player is not offered a second trial.
            await db.ExecuteAsync(@"INSERT IGNORE INTO vip_test_trials
                (steam_id64, account_id, vip_group, budget_seconds, used_seconds, source, first_server_id, granted_at, ended_at, end_reason)
                VALUES (@steamId64, @accountId, @group, @budgetSeconds, @budgetSeconds, 'legacy', @firstServerId, FROM_UNIXTIME(@now), FROM_UNIXTIME(@now), 'legacy')",
                new { steamId64, accountId, group, budgetSeconds, firstServerId, now });
            return new ClaimResult(ClaimOutcome.BlockedLegacy, null);
        }

        if (await HasPaidAsync(db, accountId, sid, now)) return new ClaimResult(ClaimOutcome.BlockedPaid, null);

        var inserted = await db.ExecuteAsync(@"INSERT IGNORE INTO vip_test_trials
            (steam_id64, account_id, vip_group, budget_seconds, used_seconds, source, first_server_id, granted_at)
            VALUES (@steamId64, @accountId, @group, @budgetSeconds, 0, @source, @firstServerId, FROM_UNIXTIME(@now))",
            new { steamId64, accountId, group, budgetSeconds, source, firstServerId, now });
        var trial = await GetAsync(db, steamId64);
        // Another server won the race: the primary key arbitrates, the loser resumes the same trial.
        return new ClaimResult(inserted == 1 ? ClaimOutcome.Granted : ClaimOutcome.Existing, trial);
    }

    /// <summary>Adds played seconds (capped at the budget) and reports whether the player bought a privilege meanwhile.</summary>
    public async Task<TickResult> AddUsedAsync(ulong steamId64, int seconds, int accountId, int sid, long now)
    {
        await using var db = await OpenAsync();
        if (seconds > 0)
        {
            await db.ExecuteAsync(@"UPDATE vip_test_trials
                SET used_seconds = LEAST(budget_seconds, used_seconds + @seconds)
                WHERE steam_id64 = @steamId64 AND ended_at IS NULL", new { steamId64, seconds });
        }
        var trial = await GetAsync(db, steamId64);
        return new TickResult(trial, await HasPaidAsync(db, accountId, sid, now));
    }

    public async Task MarkWarnedAsync(ulong steamId64, long now)
    {
        await using var db = await OpenAsync();
        await db.ExecuteAsync(
            "UPDATE vip_test_trials SET warned_10m_at = FROM_UNIXTIME(@now) WHERE steam_id64 = @steamId64 AND warned_10m_at IS NULL",
            new { steamId64, now });
    }

    /// <param name="reason">time_used or paid_privilege.</param>
    public async Task EndAsync(ulong steamId64, string reason, long now)
    {
        await using var db = await OpenAsync();
        await db.ExecuteAsync(@"UPDATE vip_test_trials
            SET ended_at = FROM_UNIXTIME(@now), end_reason = @reason,
                used_seconds = IF(@reason = 'time_used', budget_seconds, used_seconds)
            WHERE steam_id64 = @steamId64 AND ended_at IS NULL", new { steamId64, reason, now });
    }

    // The temporary VIP lives only in server memory, so a vip_users row can only be a privilege the player paid for.
    private static async Task<bool> HasPaidAsync(MySqlConnection db, int accountId, int sid, long now) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM vip_users
            WHERE account_id = @accountId AND sid = @sid AND (expires = 0 OR expires > @now)",
            new { accountId, sid, now }) > 0;

    private static Task<Trial?> GetAsync(MySqlConnection db, ulong steamId64) =>
        db.QuerySingleOrDefaultAsync<Trial>($"SELECT {Columns} FROM vip_test_trials WHERE steam_id64 = @steamId64", new { steamId64 });

    private async Task<MySqlConnection> OpenAsync()
    {
        var db = new MySqlConnection(connectionString);
        await db.OpenAsync();
        return db;
    }
}
