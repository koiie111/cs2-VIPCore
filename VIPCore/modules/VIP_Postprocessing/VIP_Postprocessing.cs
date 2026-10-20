using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Postprocessing;

public class VipPostprocessing : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Postprocessing";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Selectable screen post-processing (color grading) for VIP players";

    private Postprocessing? _feature;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Post-processing resources must be precached on map load, so this listener lives on the plugin itself.
        RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheResources);
        Server.NextWorldUpdate(Initialize);
    }

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
        if (_api == null || _feature != null) return;
        _feature = new Postprocessing(this, _api);
        _api.RegisterFeature(_feature, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_feature == null) return;

        _feature.Stop();
        _api.UnRegisterFeature(_feature);
        _feature = null;
    }

    /// <summary>
    /// The core API only exposes group values for a given player, so resource files are collected
    /// straight from vip.json to have them precached before anyone joins.
    /// </summary>
    private void PrecacheResources(ResourceManifest manifest)
    {
        var dir = _api?.CoreConfigDirectory ?? Path.Combine(Application.RootDirectory, "configs/plugins/VIPCore/");
        var path = Path.Combine(dir, "vip.json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!doc.RootElement.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
                return;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups.EnumerateObject())
            {
                if (!group.Value.TryGetProperty("Values", out var values) ||
                    !values.TryGetProperty(Postprocessing.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("file", out var file) && file.GetString() is { Length: > 0 } name)
                        files.Add(name);
            }

            foreach (var file in files)
                manifest.AddResource(file);
        }
        catch (Exception e)
        {
            Logger.LogError("Failed to read post-processing files from {Path}: {Error}", path, e.Message);
        }
    }
}

public class PostprocessingEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Post-processing resource (.vpost).</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";

    /// <summary>Default fade time in seconds. Players can override it in the menu.</summary>
    [JsonPropertyName("fade")] public float Fade { get; set; } = 0.25f;

    /// <summary>Optional CounterStrikeSharp permission or group (e.g. "@css/vip", "#css/admin"). Empty = no restriction.</summary>
    [JsonPropertyName("required")] public string Required { get; set; } = "";

    /// <summary>Optional team restriction: "T" or "CT". Empty = both teams.</summary>
    [JsonPropertyName("team")] public string Team { get; set; } = "";
}

public class Postprocessing : VipFeatureBase
{
    public const string FeatureName = "Postprocessing";
    private const string CookieKey = "postprocessing_prefs";
    private const string Off = "off";
    private const string VolumeClass = "post_processing_volume";
    private const string VolumeName = "vipcore_postprocessing";
    private const int MaxPlayers = 64;

    private static readonly float[] FadeSteps = { 0f, 0.25f, 0.5f, 1f, 2f };

    /// <summary>Auto-exposure ranges (min, max), cycled from the menu.</summary>
    private static readonly (float Min, float Max)[] ExposureSteps =
    {
        (0.5f, 2f), (0.25f, 1f), (1f, 3f), (0.75f, 1.5f)
    };

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Per-player settings, stored as one JSON cookie. A null value means "use the config default".</summary>
    private class Prefs
    {
        /// <summary>null = never chosen (first allowed entry), "off" = disabled, otherwise an entry name.</summary>
        [JsonPropertyName("p")] public string? Effect { get; set; }
        [JsonPropertyName("f")] public float? Fade { get; set; }
        [JsonPropertyName("e")] public bool Exposure { get; set; } = true;
        [JsonPropertyName("x")] public int ExposureStep { get; set; }
        [JsonPropertyName("r")] public bool ReplaceMap { get; set; } = true;
        [JsonPropertyName("s")] public bool Spectators { get; set; } = true;
    }

    public override string Feature => FeatureName;

    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly CPostProcessingVolume?[] _active = new CPostProcessingVolume?[MaxPlayers];
    private readonly List<CPostProcessingVolume> _mapVolumes = new();
    private bool _stopped;

    public Postprocessing(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is not { IsValid: true }) return HookResult.Continue;
            Remove(player.Slot);
            Server.NextFrame(() => Apply(player));
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            Remove(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            Remove(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RemoveAll();
            RefreshMapVolumes();
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            Array.Clear(_active);
            _mapVolumes.Clear();
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            Remove(slot);
            _prefs.Remove(slot);
        });

        // Module loaded on a live server: pick up VIPs already in game.
        RefreshMapVolumes();
        foreach (var player in Utilities.GetPlayers())
            if (!player.IsBot && IsClientVip(player))
                Apply(player);
    }

    /// <summary>Called on plugin unload; CounterStrikeSharp drops handlers and listeners itself.</summary>
    public void Stop()
    {
        _stopped = true;
        RemoveAll();
        _mapVolumes.Clear();
        _prefs.Clear();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped) return;
        _prefs.Remove(player.Slot);
        Apply(player);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        Remove(player.Slot);
        _prefs.Remove(player.Slot);
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var entries = TryGetEntries(player);
        if (entries == null) return;

        var prefs = Load(player);
        var entry = Current(player, entries);
        var menu = CreateMenu(GetTranslatedText(Feature));

        menu.AddMenuOption(GetTranslatedText("postprocessing.Effect",
                entry?.Name ?? GetTranslatedText("postprocessing.Disable")),
            (controller, _) => OpenEffects(controller, entries, state));

        if (entry != null)
        {
            // Settings cycle on click, are applied at once and reopen the menu so the new value is shown.
            menu.AddMenuOption(GetTranslatedText("postprocessing.Fade", Format(prefs.Fade ?? entry.Fade)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Fade = Next(FadeSteps, p.Fade ?? entry.Fade);
                    Save(controller);
                    Apply(controller);
                    OnSelectItem(controller, state);
                });

            menu.AddMenuOption(GetTranslatedText("postprocessing.Exposure", YesNo(prefs.Exposure)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Exposure = !p.Exposure;
                    Save(controller);
                    Apply(controller);
                    OnSelectItem(controller, state);
                });

            if (prefs.Exposure)
            {
                var step = ExposureSteps[prefs.ExposureStep];
                menu.AddMenuOption(
                    GetTranslatedText("postprocessing.Range", Format(step.Min), Format(step.Max)),
                    (controller, _) =>
                    {
                        var p = Load(controller);
                        p.ExposureStep = (p.ExposureStep + 1) % ExposureSteps.Length;
                        Save(controller);
                        Apply(controller);
                        OnSelectItem(controller, state);
                    });
            }

            menu.AddMenuOption(GetTranslatedText("postprocessing.ReplaceMap", YesNo(prefs.ReplaceMap)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.ReplaceMap = !p.ReplaceMap;
                    Save(controller);
                    OnSelectItem(controller, state);
                });

            menu.AddMenuOption(GetTranslatedText("postprocessing.Spectators", YesNo(prefs.Spectators)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Spectators = !p.Spectators;
                    Save(controller);
                    OnSelectItem(controller, state);
                });
        }

        menu.AddMenuOption(GetTranslatedText("postprocessing.Reset"), (controller, _) =>
        {
            var p = Load(controller);
            p.Fade = null;
            p.Exposure = true;
            p.ExposureStep = 0;
            p.ReplaceMap = true;
            p.Spectators = true;
            Save(controller);
            Apply(controller);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenEffects(CCSPlayerController player, List<PostprocessingEntry> entries, FeatureState state)
    {
        var current = Current(player, entries)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText("postprocessing.Select"));

        menu.AddMenuOption(GetTranslatedText("postprocessing.Disable"), (controller, _) =>
        {
            Load(controller).Effect = Off;
            Save(controller);
            Remove(controller.Slot);
            PrintToChat(controller, GetTranslatedText("postprocessing.Off"));
        }, current == Off);

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Load(controller).Effect = entry.Name;
                Save(controller);
                Apply(controller);
                PrintToChat(controller, GetTranslatedText("postprocessing.On", entry.Name));
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    private string YesNo(bool value) => GetTranslatedText(value ? "postprocessing.Yes" : "postprocessing.No");

    private static string Format(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The first step above <paramref name="current"/>, wrapping to the first one.</summary>
    private static float Next(float[] steps, float current)
    {
        foreach (var step in steps)
            if (step > current + 0.001f)
                return step;
        return steps[0];
    }

    // ---- settings ---------------------------------------------------------------------------

    private Prefs Load(CCSPlayerController player)
    {
        if (_prefs.TryGetValue(player.Slot, out var cached)) return cached;

        var loaded = new Prefs();
        var raw = GetPlayerCookie<string>(player.SteamID, CookieKey);
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<Prefs>(raw, PrefsJson) ?? loaded;
            }
            catch (JsonException)
            {
                // A damaged cookie falls back to the defaults.
            }
        }

        if (loaded.ExposureStep < 0 || loaded.ExposureStep >= ExposureSteps.Length) loaded.ExposureStep = 0;

        _prefs[player.Slot] = loaded;
        return loaded;
    }

    private void Save(CCSPlayerController player) =>
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(Load(player), PrefsJson));

    /// <summary>
    /// The entry the player picked if they may still use it, otherwise the first allowed entry
    /// (so a fresh VIP gets an effect without opening the menu). Null when disabled.
    /// </summary>
    private PostprocessingEntry? Current(CCSPlayerController player, List<PostprocessingEntry> entries)
    {
        var saved = Load(player).Effect;
        if (saved == Off) return null;
        return entries.FirstOrDefault(e => e.Name == saved) ?? entries.FirstOrDefault();
    }

    private List<PostprocessingEntry>? TryGetEntries(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            var entries = GetFeatureValue<List<PostprocessingEntry>>(player)
                .Where(e => e.Name.Length > 0 && e.File.Length > 0 && Allowed(player, e))
                .ToList();
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Allowed(CCSPlayerController player, PostprocessingEntry entry)
    {
        if (entry.Required.Length > 0 && !AdminManager.PlayerHasPermissions(player, entry.Required))
            return false;

        return entry.Team.ToUpperInvariant() switch
        {
            "T" => player.TeamNum == 2,
            "CT" => player.TeamNum == 3,
            _ => true
        };
    }

    // ---- volume -----------------------------------------------------------------------------

    private void Apply(CCSPlayerController? player)
    {
        if (_stopped || player is not { IsValid: true } || player.IsBot || player.Slot >= MaxPlayers) return;

        Remove(player.Slot);

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

        var entries = TryGetEntries(player);
        if (entries == null) return;

        var entry = Current(player, entries);
        var origin = pawn.AbsOrigin;
        if (entry == null || origin == null) return;

        var prefs = Load(player);
        var exposure = ExposureSteps[prefs.ExposureStep];

        var volume = Utilities.CreateEntityByName<CPostProcessingVolume>(VolumeClass);
        if (volume == null || !volume.IsValid || volume.Entity == null) return;

        var keys = new CEntityKeyValues();
        keys.SetString("targetname", VolumeName);
        keys.SetString("postprocessing", entry.File);
        keys.SetBool("master", true);
        keys.SetBool("enableexposure", prefs.Exposure);
        keys.SetFloat("fadetime", Math.Max(prefs.Fade ?? entry.Fade, 0f));
        keys.SetFloat("minexposure", exposure.Min);
        keys.SetFloat("maxexposure", exposure.Max);
        keys.SetFloat("exposurespeedup", 1f);
        keys.SetFloat("exposurespeeddown", 1f);
        keys.SetBool("startdisabled", false);
        keys.SetInt("spawnflags", 4097);
        keys.SetVector("origin", origin);

        volume.DispatchSpawn(keys);
        keys.Dispose();

        if (!volume.IsValid) return;

        volume.AcceptInput("SetParent", pawn, null, "!activator");
        _active[player.Slot] = volume;
    }

    private void RemoveAll()
    {
        for (var slot = 0; slot < MaxPlayers; slot++)
            Remove(slot);
    }

    private void Remove(int slot)
    {
        if (slot < 0 || slot >= MaxPlayers) return;

        var volume = _active[slot];
        _active[slot] = null;

        if (volume is { IsValid: true } && volume.DesignerName == VolumeClass)
            volume.Remove();
    }

    /// <summary>Volumes placed by the map, which can be hidden while a player's own effect is shown.</summary>
    private void RefreshMapVolumes()
    {
        _mapVolumes.Clear();
        foreach (var volume in Utilities.FindAllEntitiesByDesignerName<CPostProcessingVolume>(VolumeClass))
            if (volume.IsValid && !string.Equals(volume.Entity?.Name, VolumeName, StringComparison.Ordinal))
                _mapVolumes.Add(volume);
    }

    // ---- visibility -------------------------------------------------------------------------

    /// <summary>
    /// A player's volume is sent only to that player (and to spectators watching them, if the owner allows it);
    /// everyone else gets it stripped from their snapshot.
    /// </summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_stopped) return;

        var any = false;
        foreach (var volume in _active)
            if (volume != null) { any = true; break; }
        if (!any) return;

        foreach (var (info, viewer) in infoList)
        {
            if (viewer is not { IsValid: true } || viewer.Slot >= MaxPlayers) continue;

            // Whose screen this viewer is looking at: their own, or the player they spectate.
            var shown = viewer.Slot;
            var spectating = false;
            if (!viewer.PawnIsAlive)
            {
                var target = viewer.Pawn.Value?.ObserverServices?.ObserverTarget.Value;
                var observed = target is { IsValid: true } ? target.As<CCSPlayerPawn>().OriginalController.Value : null;
                if (observed is { IsValid: true } && observed.Slot >= 0 && observed.Slot < MaxPlayers)
                {
                    shown = observed.Slot;
                    spectating = true;
                }
            }

            if (spectating && _prefs.GetValueOrDefault(shown) is { Spectators: false })
                shown = -1;

            for (var owner = 0; owner < MaxPlayers; owner++)
            {
                if (owner == shown) continue;

                var volume = _active[owner];
                if (volume != null && volume.IsValid)
                    info.TransmitEntities.Remove((int)volume.Index);
            }

            if (shown < 0 || _active[shown] == null) continue;
            if (_prefs.GetValueOrDefault(shown) is not { ReplaceMap: true }) continue;

            foreach (var mapVolume in _mapVolumes)
                if (mapVolume.IsValid)
                    info.TransmitEntities.Remove((int)mapVolume.Index);
        }
    }
}
