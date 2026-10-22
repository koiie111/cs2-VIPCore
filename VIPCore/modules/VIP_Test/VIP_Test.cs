using System.Text.Json;
using System.Reflection;
using System.Threading;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using VipCoreApi;

namespace VIP_Test;

/// <summary>
/// VIP Test 3.0: one free hour of PLAYED time per SteamID (global, in the shared VIP database).
/// New players get it automatically, everyone else once with !viptest. Time counts only while the player is in a
/// team; the temporary VIP lives in server memory and is ended by this module, never by wall-clock expiry.
/// </summary>
public class VipTest : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius, 4ill.ru";
    public override string ModuleName => "[VIP] Test";
    public override string ModuleVersion => "3.0.0";

    private const int WallLifetimeSeconds = 12 * 3600;
    private const int FlushSeconds = 60;

    private sealed class Session
    {
        public ulong SteamId64;
        public int UserId;
        public int AccountId;
        public string Group = "";
        public int Budget;
        public int Used;
        public int Pending;
        public bool Warned;
        public bool Ending;
    }

    private IVipCoreApi? _api;
    private Config _config = new();
    private TrialStore _store = null!;
    private Task _ready = Task.CompletedTask;
    private bool _loaded;
    private int _timeMode;
    private int _sid;
    private readonly Dictionary<ulong, Session> _sessions = new();
    private readonly HashSet<ulong> _inFlight = new();
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Keep the retryable CSS entity cache used by the deployed version.
        var field = typeof(EntitySystem).GetField("ConcreteEntityListPointer", BindingFlags.Static | BindingFlags.NonPublic);
        if (field?.FieldType == typeof(Lazy<nint>) && !field.IsInitOnly &&
            field.GetValue(null) is Lazy<nint> { IsValueCreated: false })
            field.SetValue(null, new Lazy<nint>(NativeAPI.GetConcreteEntityListPointer, LazyThreadSafetyMode.PublicationOnly));
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;
        _config = LoadConfig();
        using (var core = JsonDocument.Parse(File.ReadAllText(Path.Combine(_api.CoreConfigDirectory, "vip_core.json"))))
            _timeMode = core.RootElement.GetProperty("TimeMode").GetInt32();
        _sid = _api.GetServerId();
        _store = new TrialStore(_api.GetDatabaseConnectionString);
        _ready = _store.InitializeAsync();
        _loaded = true;
        _ = ObserveInitializationAsync();

        RegisterEventHandler<EventPlayerConnectFull>((ev, _) => { Schedule(ev.Userid); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerSpawn>((ev, _) => { Schedule(ev.Userid); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerDisconnect>((ev, _) => { OnDisconnect(ev.Userid); return HookResult.Continue; });
        AddTimer(Math.Max(5, _config.TickSeconds), Tick, TimerFlags.REPEAT);
        if (hotReload) foreach (var player in Utilities.GetPlayers()) Schedule(player);
    }

    public override void Unload(bool hotReload)
    {
        _loaded = false;
        foreach (var session in _sessions.Values) _ = FlushAsync(session);
        _sessions.Clear();
    }

    private async Task ObserveInitializationAsync()
    {
        try { await _ready; }
        catch (Exception ex) { Logger.LogError(ex, "VIP Test storage unavailable; trials are disabled until storage recovers."); }
    }

    private void Schedule(CCSPlayerController? player)
    {
        if (!_config.AutoGrantOnJoin || player is not { IsValid: true, IsBot: false, IsHLTV: false }) return;
        var userId = player.UserId;
        // Give VIPCore time to load an existing privilege first.
        AddTimer(5.0f, () =>
        {
            if (player.IsValid && player.UserId == userId) Evaluate(player, false);
        });
    }

    [ConsoleCommand("css_viptest")]
    public void OnCommandVipTest(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller != null) Evaluate(controller, true);
    }

    // ---- entry: decide what a connecting player or a !viptest caller gets -----------------------------------------

    private void Evaluate(CCSPlayerController player, bool byCommand)
    {
        if (!_loaded || _api == null || !_config.VipTestEnabled || !player.IsValid || player.IsBot || player.IsHLTV) return;
        var auth = player.AuthorizedSteamID;
        if (auth == null) return;
        var steam64 = auth.SteamId64;

        if (_sessions.TryGetValue(steam64, out var running))
        {
            // The core may have dropped the in-memory VIP (e.g. after a map change): re-apply it, keep the budget.
            if (!_api.IsClientVip(player)) TryGive(player, running);
            if (byCommand) Say(player, string.Format(_config.MessageAlreadyRunning, TrialClock.Format(RemainingOf(running))));
            return;
        }

        var isVip = _api.IsClientVip(player);
        if (isVip && byCommand)
        {
            _api.PrintToChat(player, _api.GetTranslatedText("vip.AlreadyVipPrivileges"));
            return;
        }
        if (!_inFlight.Add(steam64)) return;

        var userId = player.UserId;
        _ = EvaluateAsync(player, userId, steam64, auth.SteamId2, auth.AccountId, isVip, byCommand);
    }

    private async Task EvaluateAsync(CCSPlayerController player, int? userId, ulong steam64, string steam2, int accountId,
        bool isVip, bool byCommand)
    {
        try
        {
            await _ready;
            var now = Now();
            // Every connecting player is recorded; the first sighting anywhere makes a player «new».
            var isNew = await _store.RegisterKnownAsync(steam64, now);
            if (isVip) return;

            var trial = await _store.GetTrialAsync(steam64);
            var resumed = trial != null;
            if (trial == null)
            {
                if (!byCommand)
                {
                    if (!_config.AutoGrantOnJoin) return;
                    if (!isNew && _config.AutoGrantNewPlayersOnly)
                    {
                        if (!await _store.IsLegacyRedeemedAsync(steam2) && await _store.MarkViptestHintAsync(steam64, now))
                            Server.NextFrame(() => { if (Same(player, steam64, userId)) Say(player, _config.MessageHint); });
                        return;
                    }
                }

                var claim = await _store.ClaimAsync(steam64, steam2, accountId, _config.VipTestGroup, _config.BudgetSeconds,
                    byCommand ? "command" : "auto", _config.WebServerId, _sid, now);
                if (claim.Outcome is ClaimOutcome.BlockedPaid or ClaimOutcome.BlockedLegacy)
                {
                    if (byCommand && claim.Outcome == ClaimOutcome.BlockedLegacy)
                        Server.NextFrame(() => { if (Same(player, steam64, userId)) _api!.PrintToChat(player, _api.GetTranslatedText("viptest.YouCanNoLongerTakeTheVip")); });
                    return;
                }
                trial = claim.Trial;
                resumed = claim.Outcome == ClaimOutcome.Existing;
            }

            if (trial == null || trial.Ended || trial.Remaining <= 0)
            {
                if (byCommand)
                    Server.NextFrame(() => { if (Same(player, steam64, userId)) _api!.PrintToChat(player, _api.GetTranslatedText("viptest.YouCanNoLongerTakeTheVip")); });
                return;
            }

            var active = trial;
            Server.NextFrame(() =>
            {
                if (!Same(player, steam64, userId) || _sessions.ContainsKey(steam64) || _api!.IsClientVip(player)) return;
                Activate(player, steam64, accountId, active, resumed);
            });
        }
        catch (Exception ex)
        {
            // Never grant on database failure: a missing record must not mean a free retry.
            Logger.LogError(ex, "VIP Test check failed for {SteamId}", steam64);
        }
        finally
        {
            Server.NextFrame(() => _inFlight.Remove(steam64));
        }
    }

    // ---- game-thread session handling ----------------------------------------------------------------------------

    private void Activate(CCSPlayerController player, ulong steam64, int accountId, Trial trial, bool resumed)
    {
        if (!_api!.GetVipGroups().Contains(trial.VipGroup))
        {
            Logger.LogError("VIP Test group {Group} is missing in vip.json; trial not applied.", trial.VipGroup);
            return;
        }
        var session = new Session
        {
            SteamId64 = steam64, UserId = player.UserId ?? -1, AccountId = accountId, Group = trial.VipGroup,
            Budget = trial.BudgetSeconds, Used = trial.UsedSeconds, Warned = trial.Warned
        };
        if (!TryGive(player, session)) return;
        _sessions[steam64] = session;

        var remaining = RemainingOf(session);
        Say(player, resumed
            ? string.Format(_config.MessageResumed, TrialClock.Format(remaining))
            : _config.MessageGranted);
        CheckWarn(player, session, remaining);
    }

    private bool TryGive(CCSPlayerController player, Session session)
    {
        try
        {
            // The wall-clock lifetime is only a safety net: this module ends the VIP when the played time is used up.
            _api!.GiveClientTemporaryVip(player, session.Group, TrialTime.ToCoreUnits(WallLifetimeSeconds, _timeMode));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not give the trial VIP to {SteamId}", session.SteamId64);
            return false;
        }
    }

    private void Tick()
    {
        if (!_loaded || _sessions.Count == 0) return;
        var players = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false })
            .Where(p => p.AuthorizedSteamID != null).ToDictionary(p => p.AuthorizedSteamID!.SteamId64);

        foreach (var session in _sessions.Values.ToList())
        {
            if (!players.TryGetValue(session.SteamId64, out var player))
            {
                _sessions.Remove(session.SteamId64);
                _ = FlushAsync(session);
                continue;
            }
            if (session.Ending) continue;

            if (TrialClock.CountsAsPlaying(player.TeamNum)) session.Pending += _config.TickSeconds;
            var remaining = RemainingOf(session);
            if (TrialClock.IsOver(remaining)) { Finish(player, session); continue; }
            CheckWarn(player, session, remaining);
            if (session.Pending >= FlushSeconds) _ = FlushAsync(session);
        }
    }

    private void CheckWarn(CCSPlayerController player, Session session, int remaining)
    {
        if (!TrialClock.ShouldWarn(remaining, _config.WarnBeforeSeconds, session.Warned)) return;
        session.Warned = true;
        Say(player, string.Format(_config.MessageWarn, TrialClock.Format(remaining), _config.OfferText));
        _ = Guard(() => _store.MarkWarnedAsync(session.SteamId64, Now()));
    }

    /// <summary>Time is up: ask the database first whether the player bought something, only then end the trial VIP.</summary>
    private void Finish(CCSPlayerController player, Session session)
    {
        session.Ending = true;
        var userId = player.UserId;
        _ = Task.Run(async () =>
        {
            try
            {
                var now = Now();
                var result = await _store.AddUsedAsync(session.SteamId64, TakePending(session), session.AccountId, _sid, now);
                var paid = result.Paid;
                await _store.EndAsync(session.SteamId64, paid ? "paid_privilege" : "time_used", now);
                Server.NextFrame(() =>
                {
                    _sessions.Remove(session.SteamId64);
                    if (!Same(player, session.SteamId64, userId)) return;
                    // A purchased privilege stays: it is loaded on the next connect, as the site tells the buyer.
                    if (paid) return;
                    EndVip(player);
                    Say(player, string.Format(_config.MessageEnded, _config.OfferText));
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not finish the trial of {SteamId}", session.SteamId64);
                Server.NextFrame(() => session.Ending = false);
            }
        });
    }

    private void EndVip(CCSPlayerController player)
    {
        try { _api!.EndTemporaryVip(player); }
        catch (MissingMethodException)
        {
            Logger.LogError("VIPCore is older than VIP Test 3.0 (no EndTemporaryVip): deploy VIPCore from the global-vip branch first.");
        }
        catch (Exception ex) { Logger.LogError(ex, "Could not end the trial VIP"); }
    }

    private void OnDisconnect(CCSPlayerController? player)
    {
        if (player is not { IsValid: true, IsBot: false }) return;
        var steam64 = player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;
        if (!_sessions.Remove(steam64, out var session)) return;
        _ = FlushAsync(session);
    }

    /// <summary>Persists played seconds. A purchase detected here stops the trial without touching the privilege.</summary>
    private async Task FlushAsync(Session session)
    {
        var take = TakePending(session);
        if (take <= 0) return;
        try
        {
            var result = await _store.AddUsedAsync(session.SteamId64, take, session.AccountId, _sid, Now());
            if (result.Paid && result.Trial is { Ended: false })
            {
                await _store.EndAsync(session.SteamId64, "paid_privilege", Now());
                Server.NextFrame(() => _sessions.Remove(session.SteamId64));
            }
        }
        catch (Exception ex)
        {
            // Lost seconds favour the player and are never retried: the budget can only be undercounted.
            Logger.LogError(ex, "Could not save trial time for {SteamId}", session.SteamId64);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>Moves pending seconds into Used (the local view stays consistent) and returns them for storage.</summary>
    private static int TakePending(Session session)
    {
        var take = session.Pending;
        session.Pending = 0;
        session.Used = Math.Min(session.Budget, session.Used + take);
        return take;
    }

    private static int RemainingOf(Session session) => TrialClock.Remaining(session.Budget, session.Used, session.Pending);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Say(CCSPlayerController player, string text)
    {
        if (player.IsValid) _api?.PrintToChat(player, text);
    }

    private static bool Same(CCSPlayerController player, ulong steam64, int? userId) =>
        player.IsValid && player.SteamID == steam64 && player.UserId == userId;

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Logger.LogError(ex, "VIP Test storage error"); }
    }

    private Config LoadConfig()
    {
        var path = Path.Combine(_api!.ModulesConfigDirectory, "vip_test.json");
        var options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip };
        // Legacy VipTestDuration/VipTestCount/VipTestCooldown fields are ignored: the budget is BudgetSeconds.
        if (File.Exists(path)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options)!;
        var config = new Config();
        File.WriteAllText(path, JsonSerializer.Serialize(config, options));
        return config;
    }
}

public class Config
{
    public bool VipTestEnabled { get; init; } = true;
    public string VipTestGroup { get; init; } = "VIP";
    /// <summary>Played seconds of the trial.</summary>
    public int BudgetSeconds { get; init; } = 3600;
    public int WarnBeforeSeconds { get; init; } = 600;
    public bool AutoGrantOnJoin { get; init; } = true;
    /// <summary>Only players first seen after the release get the trial automatically; others use !viptest.</summary>
    public bool AutoGrantNewPlayersOnly { get; init; } = true;
    public int TickSeconds { get; init; } = 15;
    /// <summary>servers_web.id of this server, stored as the place of issue for reports. 0 = unknown.</summary>
    public int WebServerId { get; init; }
    public string OfferText { get; init; } = "VIP на 7 дней — 59 ₽: 4ill.ru/vip";
    public string MessageGranted { get; init; } = "[4ILL] Тебе выдан VIP на 1 час игрового времени — считается, только пока ты играешь. Возможности: !vip";
    public string MessageResumed { get; init; } = "[4ILL] VIP-тест продолжается. Осталось {0} игры.";
    public string MessageAlreadyRunning { get; init; } = "[4ILL] VIP-тест уже идёт. Осталось {0} игры.";
    public string MessageWarn { get; init; } = "[4ILL] До конца VIP-теста {0} игры. {1}";
    public string MessageEnded { get; init; } = "[4ILL] VIP-тест закончился. Понравилось? {0}";
    public string MessageHint { get; init; } = "[4ILL] Тебе доступен бесплатный VIP на 1 час игры — напиши !viptest";
}
