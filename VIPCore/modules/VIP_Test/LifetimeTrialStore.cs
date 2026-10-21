using Dapper;
using MySqlConnector;

namespace VIP_Test;

public sealed class TrialSession
{
    public long Expires { get; set; }
    public string VipGroup { get; set; } = "";
}

/// <summary>Permanent redemption records; reconnects only resume the original expiry.</summary>
public sealed class LifetimeTrialStore(string connectionString)
{
    public async Task InitializeAsync()
    {
        await using var db = new MySqlConnection(connectionString);
        await db.OpenAsync();
        await db.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS `vipcore_test` (
                `steamid` VARCHAR(255) NOT NULL PRIMARY KEY,
                `end_time` BIGINT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS `vipcore_test_sessions` (
                `steamid` VARCHAR(255) NOT NULL PRIMARY KEY,
                `expires` BIGINT NOT NULL,
                `vip_group` VARCHAR(255) NOT NULL
            );");
    }

    public async Task<TrialSession?> GetOrClaimAsync(string steamId, bool usedCookie, int duration, string group,
        int accountId, int serverId, long now)
    {
        await using var db = new MySqlConnection(connectionString);
        await db.OpenAsync();
        // Check paid privileges in storage as well as in the game-thread cache.
        if (await db.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM vip_users WHERE account_id=@accountId AND sid=@serverId
                AND (expires=0 OR expires>@now)", new { accountId, serverId, now }) > 0) return null;

        // Existing session rows are never deleted or extended. The primary key arbitrates
        // simultaneous requests across servers sharing a database.
        try
        {
            await db.ExecuteAsync(@"
                INSERT INTO vipcore_test_sessions (steamid, expires, vip_group)
                SELECT @steamId, @expires, @group
                WHERE @usedCookie=0 AND NOT EXISTS
                    (SELECT 1 FROM vipcore_test WHERE steamid=@steamId)",
                new { steamId, expires = checked(now + duration), group, usedCookie });
        }
        catch (MySqlException ex) when (ex.Number == 1062) { /* Already reserved. */ }

        var session = await db.QuerySingleOrDefaultAsync<TrialSession>(@"
            SELECT expires, vip_group AS VipGroup FROM vipcore_test_sessions WHERE steamid=@steamId",
            new { steamId });
        if (usedCookie || session != null)
        {
            // Also import cookie-only redemptions, and protect against legacy plugin rollback.
            await db.ExecuteAsync(@"
                INSERT INTO vipcore_test (steamid, end_time) VALUES (@steamId, @endTime)
                ON DUPLICATE KEY UPDATE end_time=GREATEST(end_time, @endTime)",
                new { steamId, endTime = long.MaxValue });
        }
        return session is { Expires: > 0 } && session.Expires > now ? session : null;
    }
}
