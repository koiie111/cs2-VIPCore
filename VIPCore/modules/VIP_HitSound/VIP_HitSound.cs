using System.Globalization;
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
    public override string ModuleDescription => "Plays a selectable sound to a VIP player when they hit an enemy";

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
    private const string VolumeCookie = "hitsound_volume";
    private const string Off = "off";

    private static readonly int[] VolumeSteps = { 10, 25, 50, 75, 100 };

    private static ConVar? _cvFfa;

    private class Prefs
    {
        /// <summary>null = never chosen (use the default), "off" = disabled, otherwise an entry name.</summary>
        public string? Normal;
        public string? Hs;
        public int Volume = 100;
    }

    private readonly Dictionary<int, Prefs> _prefs = new();
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
        _prefs.Clear();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group) => _prefs.Remove(player.Slot);

    public override void OnPlayerRemoved(CCSPlayerController player, string group) => _prefs.Remove(player.Slot);

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

        Play(attacker, entry, Load(attacker).Volume);

        return HookResult.Continue;
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var entries = TryGetEntries(player);
        if (entries == null) return;

        var prefs = Load(player);
        var menu = CreateMenu(GetTranslatedText(Feature));

        if (Category(entries, false).Count > 0)
            menu.AddMenuOption(GetTranslatedText("hitsound.Normal"),
                (controller, _) => OpenCategory(controller, entries, false));
        if (Category(entries, true).Count > 0)
            menu.AddMenuOption(GetTranslatedText("hitsound.Headshot"),
                (controller, _) => OpenCategory(controller, entries, true));

        // Settings cycle on click and reopen the menu so the new value is shown.
        menu.AddMenuOption(GetTranslatedText("hitsound.Volume", prefs.Volume), (controller, _) =>
        {
            var p = Load(controller);
            var next = VolumeSteps.FirstOrDefault(v => v > p.Volume);
            p.Volume = next == 0 ? VolumeSteps[0] : next;
            SetPlayerCookie(controller.SteamID, VolumeCookie, p.Volume);
            PlayPreview(controller, entries, p);
            OnSelectItem(controller, state);
        });

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
                Play(controller, entry, Load(controller).Volume); // preview
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    // ---- selection --------------------------------------------------------------------------

    private void Save(CCSPlayerController player, bool hs, string value)
    {
        var prefs = Load(player);
        if (hs) prefs.Hs = value;
        else prefs.Normal = value;
        SetPlayerCookie(player.SteamID, hs ? HeadshotCookie : NormalCookie, value);
    }

    private string? Setting(CCSPlayerController player, bool hs)
    {
        var prefs = Load(player);
        return hs ? prefs.Hs : prefs.Normal;
    }

    private Prefs Load(CCSPlayerController player)
    {
        if (_prefs.TryGetValue(player.Slot, out var cached)) return cached;

        var normal = GetPlayerCookie<string>(player.SteamID, NormalCookie);
        var hs = GetPlayerCookie<string>(player.SteamID, HeadshotCookie);
        var volume = GetPlayerCookie<int>(player.SteamID, VolumeCookie);

        var loaded = new Prefs
        {
            Normal = string.IsNullOrEmpty(normal) ? null : normal,
            Hs = string.IsNullOrEmpty(hs) ? null : hs,
            Volume = VolumeSteps.Contains(volume) ? volume : 100
        };
        _prefs[player.Slot] = loaded;
        return loaded;
    }

    /// <summary>Plays the sound the player would hear on a normal hit, so a volume change can be judged by ear.</summary>
    private void PlayPreview(CCSPlayerController player, List<HitSoundEntry> entries, Prefs prefs)
    {
        var entry = Pick(player, entries, false) ?? Pick(player, entries, true);
        if (entry != null) Play(player, entry, prefs.Volume);
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

    /// <param name="percent">The player's own volume setting, multiplied with the entry volume.</param>
    private static void Play(CCSPlayerController player, HitSoundEntry entry, int percent)
    {
        var volume = entry.Volume * percent / 100f;
        if (volume <= 0f) return;

        if (entry.Emit.Length > 0)
        {
            if (player.IsValid) player.EmitSound(entry.Emit, new RecipientFilter(player), volume);
            return;
        }

        player.ExecuteClientCommand(
            $"playvol {entry.Path} {Math.Clamp(volume, 0f, 1f).ToString("0.##", CultureInfo.InvariantCulture)}");
    }
}
