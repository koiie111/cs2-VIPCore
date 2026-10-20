using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_HitSound;

public class VipHitSound : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Hit Sound";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Plays a selectable sound when a VIP player hits an enemy";

    private HitSound? _hitSound;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload) => Server.NextWorldUpdate(Initialize);

    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();

    private void Initialize()
    {
        if (_api != null) return;
        _api = PluginCapability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _hitSound != null) return;
        _hitSound = new HitSound(this, _api);
        _api.RegisterFeature(_hitSound, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_hitSound == null) return;

        _hitSound.Stop();
        _api.UnRegisterFeature(_hitSound);
    }
}

public class HitSoundEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Sound file played on the client with <c>play</c>, e.g. "sounds/ui/killcard_1.vsnd".</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>Built-in soundevent name, e.g. "UI.PlayerPing". Wins over <c>path</c> when both are set.</summary>
    [JsonPropertyName("emit")] public string Emit { get; set; } = "";

    [JsonPropertyName("volume")] public float Volume { get; set; } = 1f;

    /// <summary>True: offered in the headshot category; false: in the normal one.</summary>
    [JsonPropertyName("hs")] public bool Hs { get; set; }
}

public class HitSound : VipFeatureBase
{
    private const string NormalCookie = "hitsound_normal";
    private const string HeadshotCookie = "hitsound_hs";
    private const string Off = "off";

    private static ConVar? _cvFfa;

    // Per-slot selection: null = never chosen (use the default), "off" = disabled, otherwise an entry name.
    private readonly Dictionary<int, (string? Normal, string? Hs)> _selected = new();
    private bool _stopped;

    public override string Feature => "HitSound";

    public HitSound(VipHitSound plugin, IVipCoreApi api) : base(api)
    {
        plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
    }

    /// <summary>Called on plugin unload; CounterStrikeSharp drops the event handler itself, so we only go quiet.</summary>
    public void Stop()
    {
        _stopped = true;
        _selected.Clear();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group) => _selected.Remove(player.Slot);

    public override void OnPlayerRemoved(CCSPlayerController player, string group) => _selected.Remove(player.Slot);

    private HookResult OnPlayerHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        if (_stopped) return HookResult.Continue;

        var attacker = ev.Attacker;
        var victim = ev.Userid;
        if (attacker is not { IsValid: true, IsBot: false } || victim is not { IsValid: true })
            return HookResult.Continue;
        if (attacker.Slot == victim.Slot) return HookResult.Continue;

        var entries = TryGetEntries(attacker);
        if (entries == null) return HookResult.Continue;

        _cvFfa ??= ConVar.Find("mp_teammates_are_enemies");
        var ffa = _cvFfa?.GetPrimitiveValue<bool>() ?? false;
        if (!ffa && victim.Team == attacker.Team) return HookResult.Continue;

        var headshot = ev.Hitgroup == (int)HitGroup_t.HITGROUP_HEAD;
        var entry = headshot ? Pick(attacker, entries, true) : null;
        entry ??= Pick(attacker, entries, false);
        if (entry == null) return HookResult.Continue;

        var listeners = new List<CCSPlayerController> { attacker };
        AddSpectators(attacker, listeners);
        Play(attacker, listeners, entry);

        return HookResult.Continue;
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var entries = TryGetEntries(player);
        if (entries == null) return;

        var menu = CreateMenu(GetTranslatedText(Feature));

        if (Category(entries, false).Count > 0)
            menu.AddMenuOption(GetTranslatedText("hitsound.Normal"),
                (controller, _) => OpenCategory(controller, entries, false));
        if (Category(entries, true).Count > 0)
            menu.AddMenuOption(GetTranslatedText("hitsound.Headshot"),
                (controller, _) => OpenCategory(controller, entries, true));

        menu.Open(player);
    }

    private void OpenCategory(CCSPlayerController player, List<HitSoundEntry> entries, bool hs)
    {
        var category = Category(entries, hs);
        var current = Setting(player, hs) ?? (hs ? Off : category.FirstOrDefault()?.Name ?? Off);
        var categoryName = GetTranslatedText(hs ? "hitsound.Headshot" : "hitsound.Normal");

        var menu = CreateMenu(categoryName);

        menu.AddMenuOption(GetTranslatedText("hitsound.Disable"), (controller, _) =>
        {
            Save(controller, hs, Off);
            PrintToChat(controller, GetTranslatedText("hitsound.Off", categoryName));
        }, current == Off);

        foreach (var entry in category)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Save(controller, hs, entry.Name);
                PrintToChat(controller, GetTranslatedText("hitsound.On", entry.Name));
                Play(controller, new List<CCSPlayerController> { controller }, entry); // preview
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    // ---- selection --------------------------------------------------------------------------

    private void Save(CCSPlayerController player, bool hs, string value)
    {
        var (normal, headshot) = Load(player);
        _selected[player.Slot] = hs ? (normal, value) : (value, headshot);
        SetPlayerCookie(player.SteamID, hs ? HeadshotCookie : NormalCookie, value);
    }

    private string? Setting(CCSPlayerController player, bool hs)
    {
        var (normal, headshot) = Load(player);
        return hs ? headshot : normal;
    }

    private (string? Normal, string? Hs) Load(CCSPlayerController player)
    {
        if (_selected.TryGetValue(player.Slot, out var cached)) return cached;

        var normal = GetPlayerCookie<string>(player.SteamID, NormalCookie);
        var hs = GetPlayerCookie<string>(player.SteamID, HeadshotCookie);
        var loaded = (string.IsNullOrEmpty(normal) ? null : normal, string.IsNullOrEmpty(hs) ? null : hs);
        _selected[player.Slot] = loaded;
        return loaded;
    }

    /// <summary>
    /// The entry to play for the category. Without a saved choice the normal category uses its first entry,
    /// while the headshot category stays empty so the normal sound plays on headshots too.
    /// </summary>
    private HitSoundEntry? Pick(CCSPlayerController player, List<HitSoundEntry> entries, bool hs)
    {
        var category = Category(entries, hs);
        var setting = Setting(player, hs);

        if (setting == Off) return null;
        if (setting == null) return hs ? null : category.FirstOrDefault();
        return category.FirstOrDefault(e => e.Name == setting);
    }

    private static List<HitSoundEntry> Category(List<HitSoundEntry> entries, bool hs) =>
        entries.Where(e => e.Hs == hs).ToList();

    private List<HitSoundEntry>? TryGetEntries(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            return GetFeatureValue<List<HitSoundEntry>>(player)
                .Where(e => e.Name.Length > 0 && (e.Path.Length > 0 || e.Emit.Length > 0))
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- sound ------------------------------------------------------------------------------

    /// <summary>Adds every dead player who is currently spectating <paramref name="player"/>.</summary>
    private static void AddSpectators(CCSPlayerController player, List<CCSPlayerController> listeners)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true }) return;

        foreach (var other in Utilities.GetPlayers())
        {
            if (!other.IsValid || other.IsBot || other.Slot == player.Slot || other.PawnIsAlive) continue;

            var target = other.Pawn.Value?.ObserverServices?.ObserverTarget.Value;
            if (target is { IsValid: true } && target.Index == pawn.Index)
                listeners.Add(other);
        }
    }

    private static void Play(CCSPlayerController source, List<CCSPlayerController> listeners, HitSoundEntry entry)
    {
        if (entry.Volume <= 0f) return;

        if (entry.Emit.Length > 0)
        {
            if (!source.IsValid) return;

            var filter = new RecipientFilter();
            foreach (var listener in listeners)
                filter.Add(listener);

            source.EmitSound(entry.Emit, filter, entry.Volume);
            return;
        }

        foreach (var listener in listeners)
            listener.ExecuteClientCommand($"play {entry.Path}");
    }
}
