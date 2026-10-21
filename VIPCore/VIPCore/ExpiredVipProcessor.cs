using Dapper;
using MySqlConnector;

namespace VIPCore;

/// <param name="RestoredGroup">Group of the restored fallback privilege; null when the user was simply removed.</param>
public sealed record ExpiryOutcome(int AccountId, string Name, string ExpiredGroup, string? RestoredGroup,
    long RestoredExpires);

/// <summary>
/// Expiry of a vip_users row, with a hand-over to vip_user_fallbacks (e.g. a permanent VIP kept under a
/// temporary PREMIUM). Free of CounterStrikeSharp types so it can run against a disposable MySQL database.
/// </summary>
public static class ExpiredVipProcessor
{
    public static async Task<bool> FallbackTableExistsAsync(MySqlConnection connection)
    {
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'vip_user_fallbacks'") > 0;
    }

    private sealed class Fallback
    {
        public string group { get; set; } = "";
        public long expires { get; set; }
    }

    private sealed class ExpiredRow
    {
        public string name { get; set; } = "";
        public string group { get; set; } = "";
    }

    public static async Task<List<ExpiryOutcome>> ProcessAsync(MySqlConnection connection, int accountId, long sid,
        long now, bool fallbackTableExists)
    {
        var outcomes = new List<ExpiryOutcome>();

        await using var tx = await connection.BeginTransactionAsync();
        try
        {
            var expired = (await connection.QueryAsync<ExpiredRow>(
                "SELECT `name`, `group` FROM vip_users WHERE account_id = @AccId AND sid = @sid AND expires < @now AND expires > 0 FOR UPDATE",
                new { AccId = accountId, sid, now }, tx)).ToList();

            if (expired.Count == 0)
            {
                await tx.RollbackAsync();
                return outcomes;
            }

            Fallback? fallback = null;
            if (fallbackTableExists)
            {
                fallback = await connection.QuerySingleOrDefaultAsync<Fallback>(
                    "SELECT `group`, `expires` FROM vip_user_fallbacks WHERE account_id = @AccId FOR UPDATE",
                    new { AccId = accountId }, tx);
            }

            var fallbackActive = fallback != null && (fallback.expires == 0 || fallback.expires > now);

            foreach (var row in expired)
            {
                if (fallbackActive)
                {
                    await connection.ExecuteAsync(
                        "UPDATE vip_users SET `group` = @group, expires = @expires WHERE account_id = @AccId AND sid = @sid",
                        new { fallback!.group, fallback.expires, AccId = accountId, sid }, tx);
                    outcomes.Add(new ExpiryOutcome(accountId, row.name, row.group, fallback.group, fallback.expires));
                }
                else
                {
                    await connection.ExecuteAsync("DELETE FROM vip_users WHERE account_id = @AccId AND sid = @sid",
                        new { AccId = accountId, sid }, tx);
                    outcomes.Add(new ExpiryOutcome(accountId, row.name, row.group, null, 0));
                }
            }

            // The fallback is consumed either way: restored into vip_users, or already expired itself.
            if (fallback != null)
                await connection.ExecuteAsync("DELETE FROM vip_user_fallbacks WHERE account_id = @AccId",
                    new { AccId = accountId }, tx);

            await tx.CommitAsync();
            return outcomes;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
