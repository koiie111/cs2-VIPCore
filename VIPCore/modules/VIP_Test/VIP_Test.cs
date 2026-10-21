using System.Text.Json;
using System.Reflection;
using System.Threading;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;
using VipCoreApi;

namespace VIP_Test;

public class VipTest : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Test";
    public override string ModuleVersion => "2.1.0";

    private const string Feature = "vip_test_count";
    private IVipCoreApi? _api;
    private Config _config = null!;
    private LifetimeTrialStore _store = null!;
    private Task _ready = Task.CompletedTask;
    private readonly HashSet<ulong> _pending = new();
    private bool _loaded;
    private int _serverId;
    private int _timeMode;
    private readonly HashSet<ulong> _checked = new();
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Keep the retryable CSS entity cache used by the deployed auto-grant version.
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
        using var core = JsonDocument.Parse(File.ReadAllText(Path.Combine(_api.CoreConfigDirectory, "vip_core.json")));
        _serverId = core.RootElement.GetProperty("ServerId").GetInt32();
        _timeMode = core.RootElement.GetProperty("TimeMode").GetInt32();
        _store = new LifetimeTrialStore(_api.GetDatabaseConnectionString);
        _ready = _store.InitializeAsync();
        _loaded = true;
        // Observe initialization errors even if nobody requests a trial.
        _ = ObserveInitializationAsync();
        RegisterEventHandler<EventPlayerConnectFull>((ev, info) => { Schedule(ev.Userid); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerSpawn>((ev, info) => { Schedule(ev.Userid); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerDisconnect>((ev, info) =>
        {
            if (ev.Userid is { IsValid: true } player) _checked.Remove(player.SteamID);
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnMapStart>(_ => _checked.Clear());
        if (hotReload) foreach (var player in Utilities.GetPlayers()) Schedule(player);
    }

    private void Schedule(CCSPlayerController? player)
    {
        if (!_config.AutoGrantOnJoin || player is not { IsValid: true, IsBot: false, IsHLTV: false }) return;
        var userId = player.UserId;
        AddTimer(5.0f, () =>
        {
            if (player.IsValid && player.UserId == userId) RequestTrial(player, true);
        });
    }

    private async Task ObserveInitializationAsync()
    {
        try { await _ready; }
        catch (Exception ex) { Logger.LogError(ex, "VIP Test storage unavailable; trials are disabled until storage recovers."); }
    }

    public override void Unload(bool hotReload) => _loaded = false;

    [ConsoleCommand("css_viptest")]
    public void OnCommandVipTest(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller != null) RequestTrial(controller, false);
    }

    private void RequestTrial(CCSPlayerController player, bool silent)
    {
        if (!_loaded || _api == null || !_config.VipTestEnabled || !player.IsValid || player.IsBot || player.IsHLTV)
            return;
        if (silent && _checked.Contains(player.SteamID)) return;
        if (_api.IsClientVip(player))
        {
            if (!silent) _api.PrintToChat(player, _api.GetTranslatedText("vip.AlreadyVipPrivileges"));
            return;
        }
        var steamId = player.AuthorizedSteamID;
        if (steamId == null || _config.VipTestDuration <= 0 ||
            !_api.GetVipGroups().Contains(_config.VipTestGroup) || !_pending.Add(steamId.SteamId64)) return;

        if (_ready.IsFaulted) _ready = _store.InitializeAsync();
        // All engine and cookie access stays on the game thread.
        var usedCookie = _api.GetPlayerCookie<int>(steamId.SteamId64, Feature) > 0;
        _ = GivePlayerVipTestAsync(player, player.UserId, steamId, usedCookie, silent);
    }

    private async Task GivePlayerVipTestAsync(CCSPlayerController player, int? userId, SteamID steamId, bool usedCookie, bool silent)
    {
        try
        {
            await _ready;
            var trial = await _store.GetOrClaimAsync(steamId.SteamId2, usedCookie, _config.VipTestDuration,
                _config.VipTestGroup, steamId.AccountId, _serverId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Server.NextFrame(() =>
            {
                _pending.Remove(steamId.SteamId64);
                if (!_loaded || !player.IsValid || player.SteamID != steamId.SteamId64 || player.UserId != userId) return;
                _checked.Add(steamId.SteamId64);
                var remaining = (trial?.Expires ?? 0) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (remaining <= 0)
                {
                    if (!silent) _api!.PrintToChat(player, _api.GetTranslatedText("viptest.YouCanNoLongerTakeTheVip"));
                    return;
                }
                if (_api!.IsClientVip(player)) return;
                if (!_api.GetVipGroups().Contains(trial!.VipGroup))
                {
                    _checked.Remove(steamId.SteamId64);
                    Logger.LogError("VIP Test stored group is missing; session retained.");
                    return;
                }
                var coreUnits = TrialTime.ToCoreUnits(remaining, _timeMode);
                _api.GiveClientTemporaryVip(player, trial.VipGroup, coreUnits);
                _api.SetPlayerCookie(steamId.SteamId64, Feature, 1);
                var duration = TimeSpan.FromSeconds(remaining);
                _api.PrintToChat(player, _api.GetTranslatedText("viptest.SuccessfullyPassed",
                    duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")));
            });
        }
        catch (Exception ex)
        {
            // Never grant on database failure: a missing record must not mean a free retry.
            Logger.LogError(ex, "Failed to reserve lifetime VIP Test for {SteamId}", steamId.SteamId2);
            Server.NextFrame(() => _pending.Remove(steamId.SteamId64));
        }
    }

    private Config LoadConfig()
    {
        var path = Path.Combine(_api!.ModulesConfigDirectory, "vip_test.json");
        if (File.Exists(path)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!;
        var config = new Config { AutoGrantOnJoin = true };
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return config;
    }
}

public class Config
{
    public bool VipTestEnabled { get; init; } = true;
    public int VipTestDuration { get; init; } = 3600;
    public string VipTestGroup { get; init; } = "group_name";
    public bool AutoGrantOnJoin { get; init; }
    // Legacy VipTestCount/VipTestCooldown JSON fields are ignored. Every trial is lifetime-only.
}
