using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using VipCoreApi;

namespace VIP_KillIcon;

public class VipKillIcon : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Kill Icon";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Replaces the kill feed weapon icon for VIP killers depending on how the kill happened";

    private KillIcon? _killIcon;
    private IVipCoreApi? _api;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _api.OnCoreReady += () =>
        {
            _killIcon = new KillIcon(this, _api);
            _api.RegisterFeature(_killIcon);
        };
    }

    public override void Unload(bool hotReload)
    {
        if (_api != null && _killIcon != null)
            _api.UnRegisterFeature(_killIcon);
    }
}

public class KillIconConfig
{
    [JsonPropertyName("headshot")] public string Headshot { get; set; } = "";
    [JsonPropertyName("penetrated")] public string Penetrated { get; set; } = "";
    [JsonPropertyName("noscope")] public string NoScope { get; set; } = "";
    [JsonPropertyName("throughsmoke")] public string ThroughSmoke { get; set; } = "";
    [JsonPropertyName("blindkill")] public string BlindKill { get; set; } = "";
    [JsonPropertyName("assistflash")] public string AssistFlash { get; set; } = "";
    [JsonPropertyName("jumpkill")] public string JumpKill { get; set; } = "";
    [JsonPropertyName("dominated")] public string Dominated { get; set; } = "";
    [JsonPropertyName("squadwipe")] public string SquadWipe { get; set; } = "";

    /// <summary>Per-weapon replacement: original weapon name (with or without "weapon_") -> icon name.</summary>
    [JsonPropertyName("weapons")] public Dictionary<string, string> Weapons { get; set; } = new();
}

public class KillIcon : VipFeatureBase
{
    private const uint FlagOnGround = 1u << 0; // FL_ONGROUND

    public override string Feature => "KillIcon";

    public KillIcon(VipKillIcon plugin, IVipCoreApi api) : base(api)
    {
        plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;

        if (attacker is null || !attacker.IsValid || victim is null || !victim.IsValid)
            return HookResult.Continue;
        if (attacker.Slot == victim.Slot) return HookResult.Continue;

        if (!IsClientVip(attacker) || !PlayerHasFeature(attacker)) return HookResult.Continue;
        if (GetPlayerFeatureState(attacker) is not IVipCoreApi.FeatureState.Enabled) return HookResult.Continue;

        KillIconConfig cfg;
        try
        {
            cfg = GetFeatureValue<KillIconConfig>(attacker);
        }
        catch (Exception)
        {
            return HookResult.Continue;
        }

        var icon = PickIcon(cfg, @event, attacker, victim);
        if (icon.Length > 0)
            @event.Weapon = icon;

        return HookResult.Continue;
    }

    private static string PickIcon(KillIconConfig cfg, EventPlayerDeath ev, CCSPlayerController attacker,
        CCSPlayerController victim)
    {
        if (cfg.SquadWipe.Length > 0 && SquadWiped(victim)) return cfg.SquadWipe;
        if (cfg.Dominated.Length > 0 && ev.Dominated > 0) return cfg.Dominated;
        if (cfg.JumpKill.Length > 0 && InAir(attacker)) return cfg.JumpKill;
        if (cfg.BlindKill.Length > 0 && ev.Attackerblind) return cfg.BlindKill;
        if (cfg.AssistFlash.Length > 0 && ev.Assistedflash) return cfg.AssistFlash;
        if (cfg.NoScope.Length > 0 && ev.Noscope) return cfg.NoScope;
        if (cfg.ThroughSmoke.Length > 0 && ev.Thrusmoke) return cfg.ThroughSmoke;
        if (cfg.Penetrated.Length > 0 && ev.Penetrated > 0) return cfg.Penetrated;
        if (cfg.Headshot.Length > 0 && ev.Headshot) return cfg.Headshot;

        if (cfg.Weapons.Count == 0 || string.IsNullOrEmpty(ev.Weapon)) return "";

        if (cfg.Weapons.TryGetValue(ev.Weapon, out var byName) && byName.Length > 0) return byName;
        return cfg.Weapons.TryGetValue("weapon_" + ev.Weapon, out var prefixed) ? prefixed : "";
    }

    private static bool InAir(CCSPlayerController attacker)
    {
        var pawn = attacker.PlayerPawn.Value;
        return pawn is { IsValid: true } && (pawn.Flags & FlagOnGround) == 0;
    }

    /// <summary>True when the victim was the last living player of their team.</summary>
    private static bool SquadWiped(CCSPlayerController victim)
    {
        foreach (var mate in CounterStrikeSharp.API.Utilities.GetPlayers())
        {
            if (!mate.IsValid || mate.Slot == victim.Slot) continue;
            if (mate.Team == victim.Team && mate.PawnIsAlive) return false;
        }

        return true;
    }
}
