using System.Data;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace VIPCore;

public class Database(VipCore vipCore, ILogger logger, string dbConnectionString)
{
    public async Task CreateTable()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(dbConnectionString);
            await dbConnection.OpenAsync();

            const string createVipUsersTable = """
                                               CREATE TABLE IF NOT EXISTS `vip_users` (
                                                   `account_id` BIGINT NOT NULL,
                                                   `name` VARCHAR(64) NOT NULL,
                                                   `lastvisit` BIGINT NOT NULL,
                                                   `sid` BIGINT NOT NULL,
                                                   `group` VARCHAR(64) NOT NULL,
                                                   `expires` BIGINT NOT NULL,
                                               PRIMARY KEY (`account_id`, `sid`));
                                               """;


            await dbConnection.ExecuteAsync(createVipUsersTable);

            if (vipCore.CoreConfig.UseFixedServerId) return;

            const string createVipServersTable = """
                                                 CREATE TABLE IF NOT EXISTS `vip_servers` (
                                                     `serverId` BIGINT NOT NULL,
                                                     `serverIp` VARCHAR(45) NOT NULL,
                                                     `port` INT NOT NULL,
                                                     `created_at` TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                                     `updated_at` TIMESTAMP,
                                                     PRIMARY KEY (`serverId`)
                                                 );
                                                 """;

            await dbConnection.ExecuteAsync(createVipServersTable);

            // Check if the ServerIP and ServerPort already exist
            const string checkVipServerQuery = """
                                               SELECT COUNT(*)
                                               FROM `vip_servers`
                                               WHERE `serverIp` = @ServerIP AND `port` = @ServerPort;
                                               """;

            var serverExists = await dbConnection.ExecuteScalarAsync<int>(checkVipServerQuery, new
            {
                ServerIP = vipCore.CoreConfig.ServerIp,
                ServerPort = vipCore.CoreConfig.ServerPort
            });

            if (serverExists == 0)
            {
                // Insert ServerIP and ServerPort from config into vip_servers table
                const string insertVipServerQuery = """
                                                    INSERT INTO `vip_servers` (`serverId`, `serverIp`, `port`)
                                                    VALUES (@ServerId, @ServerIP, @ServerPort);
                                                    """;

                await dbConnection.ExecuteAsync(insertVipServerQuery, new
                {
                    ServerId = vipCore.CoreConfig.ServerId,
                    ServerIP = vipCore.CoreConfig.ServerIp,
                    ServerPort = vipCore.CoreConfig.ServerPort,
                });
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task<User?> GetExistingUserFromDb(int accountId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);

            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid",
                new { AccId = accountId, sid = serverId });

            return existingUser ?? null;
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
            return null;
        }
    }

    public async Task AddUserToDb(User user)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);

            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = user.account_id,
                    sid = serverId
                });

            if (existingUser != null)
            {
                vipCore.PrintLogWarning("User already exists");
                return;
            }

            await connection.ExecuteAsync(@"
                INSERT INTO vip_users (account_id, name, lastvisit, sid, `group`, expires)
                VALUES (@account_id, @name, @lastvisit, @sid, @group, @expires);", user);

            vipCore.PrintLogInfo("Player '{name} [{accId}]' has been successfully added", user.name, user.account_id);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task UpdateUserInDb(User user)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = user.account_id,
                    sid = serverId
                });

            if (existingUser == null)
            {
                vipCore.PrintLogWarning("User does not exist");
                return;
            }

            await connection.ExecuteAsync(@"
            UPDATE 
                vip_users
            SET 
                name = @name,
                lastvisit = @lastvisit,
                `group` = @group,
                expires = @expires
            WHERE account_id = @account_id AND sid = @sid;", user);

            vipCore.PrintLogInfo("Player '{name} [{accId}]' has been successfully updated", user.name,
                user.account_id);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task UpdateUserVip(int accountId, string name = "", string group = "", int time = -1)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = accountId,
                    sid = serverId
                });

            if (existingUser == null)
            {
                vipCore.PrintLogWarning($"User with account ID '{accountId}' does not exist");
                return;
            }

            if (!string.IsNullOrEmpty(name))
                existingUser.name = name;

            if (!string.IsNullOrEmpty(group))
                existingUser.group = group;

            if (time > -1)
                existingUser.expires = time == 0 ? 0 : vipCore.CalculateEndTimeInSeconds(time);

            await connection.ExecuteAsync(@"
            UPDATE 
                vip_users
            SET 
                name = @name,
                `group` = @group,
                expires = @expires
            WHERE account_id = @account_id AND sid = @sid;", existingUser);

            vipCore.PrintLogInfo(
                $"Player '{existingUser.name} [{accountId}]' VIP information has been successfully updated");
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task RemoveUserFromDb(int accId)
    {
        try
        {
            var existingUser = await GetExistingUserFromDb(accId);

            if (existingUser == null)
                return;

            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            await connection.ExecuteAsync(@"
            DELETE FROM vip_users
        WHERE account_id = @AccId AND sid = @sid;", new { AccId = accId, sid = serverId });

            vipCore.PrintLogInfo("Player {name}[{accId}] has been successfully removed", existingUser.name, accId);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task<List<User?>?> GetUserFromDb(int accId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var user = await connection.QueryAsync<User?>(
                "SELECT * FROM `vip_users` WHERE `account_id` = @AccId AND sid = @sid AND (expires > @CurrTime OR expires = 0)",
                new { AccId = accId, sid = serverId, CurrTime = DateTime.UtcNow.GetUnixEpoch() }
            );

            return user.ToList();
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }

        return null;
    }

    private bool _fallbackTableExists;
    private DateTime _fallbackTableCheckedAt = DateTime.MinValue;

    // A positive answer is final; a negative one is rechecked every 10 minutes (the migration may land while running).
    private async Task<bool> FallbackTableExists(MySqlConnection connection)
    {
        if (_fallbackTableExists) return true;
        if (DateTime.UtcNow - _fallbackTableCheckedAt < TimeSpan.FromMinutes(10)) return false;

        _fallbackTableExists = await ExpiredVipProcessor.FallbackTableExistsAsync(connection);
        _fallbackTableCheckedAt = DateTime.UtcNow;
        return _fallbackTableExists;
    }

    // Hands expired rows over to their fallback before the player is loaded, so a returning player does not wait for
    // the 300 s timer. Without vip_user_fallbacks this does nothing and the timer keeps the old behaviour.
    public async Task<List<ExpiryOutcome>> ExpireOnConnect(int accountId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            if (!await FallbackTableExists(connection)) return [];

            var serverId = await GetServerId(connection);
            var outcomes = await ExpiredVipProcessor.ProcessAsync(connection, accountId, serverId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true);
            outcomes.ForEach(LogExpiry);
            return outcomes;
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
            return [];
        }
    }

    private void LogExpiry(ExpiryOutcome outcome)
    {
        vipCore.PrintLogInfo(outcome.RestoredGroup == null
                ? "User '{name} [{accId}]' has been removed due to expired VIP status."
                : "User '{name} [{accId}]' VIP expired, fallback privilege restored.", outcome.Name,
            outcome.AccountId);
    }

    /// <returns>The number of expired rows found in the database for this player.</returns>
    public async Task<int> RemoveExpiredUsers(CCSPlayerController player, SteamID steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var outcomes = await ExpiredVipProcessor.ProcessAsync(connection, steamId.AccountId, serverId, now,
                await FallbackTableExists(connection));

            foreach (var outcome in outcomes)
            {
                await Server.NextFrameAsync(() =>
                {
                    var authSteamId = player.AuthorizedSteamID;
                    var isThisPlayer = authSteamId != null && authSteamId.AccountId == outcome.AccountId;

                    if (isThisPlayer)
                        vipCore.PrintToChat(player, vipCore.Localizer["vip.Expired", outcome.ExpiredGroup]);

                    vipCore.VipApi.OnPlayerRemoved(player, outcome.ExpiredGroup);

                    // The player may have left while the query ran; the next connect loads the restored row anyway.
                    if (outcome.RestoredGroup == null || !isThisPlayer || !player.IsValid) return;

                    var steamId64 = authSteamId!.SteamId64;
                    vipCore.Users[steamId64] = new User
                    {
                        account_id = outcome.AccountId,
                        name = outcome.Name,
                        lastvisit = DateTime.UtcNow.GetUnixEpoch(),
                        sid = (int)serverId,
                        group = outcome.RestoredGroup,
                        expires = (int)outcome.RestoredExpires
                    };

                    // Rebuild per-feature states for the new group, then let the modules re-apply their features.
                    vipCore.SetClientFeature(steamId64, outcome.RestoredGroup);
                    vipCore.IsClientVip[player.Slot] = true;
                    vipCore.VipApi.OnPlayerLoaded(player, outcome.RestoredGroup);
                    vipCore.PrintToChat(player, vipCore.Localizer["vip.FallbackRestored", outcome.RestoredGroup]);
                });

                LogExpiry(outcome);
            }

            return outcomes.Count;
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
            return -1;
        }
    }

    private async Task<long> GetServerId(IDbConnection connection)
    {
        if (vipCore.CoreConfig.UseFixedServerId)
            return vipCore.CoreConfig.ServerId;

        try
        {
            const string query = """
                                 SELECT `serverId`
                                 FROM `vip_servers`
                                 WHERE `serverIp` = @ServerIP AND `port` = @ServerPort;
                                 """;

            return await connection.ExecuteScalarAsync<long>(query,
                new
                {
                    ServerIP = vipCore.CoreConfig.ServerIp,
                    ServerPort = vipCore.CoreConfig.ServerPort
                });
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }

        return -1;
    }
}