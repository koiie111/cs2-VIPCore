using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Postprocessing;

public class VipPostprocessing : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Postprocessing";
    public const string Version = "1.0.4";
    public override string ModuleVersion => Version;
    public override string ModuleDescription => "Selectable screen post-processing (color grading) for VIP players";

    private Postprocessing? _feature;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    private bool _mapRunning;
    private bool _unloaded;

    public override void Load(bool hotReload)
    {
        // Cold start loads modules before the map: no entity system and no global variables yet. Anything that
        // enumerates players or entities waits for OnMapStart; a live (hot) load already has a running map.
        _mapRunning = hotReload || !string.IsNullOrEmpty(Server.MapName);
        RegisterListener<Listeners.OnMapStart>(_ => _mapRunning = true);
        RegisterListener<Listeners.OnMapEnd>(() => _mapRunning = false);
        // Post-processing resources must be precached on map load, so this listener lives on the plugin itself.
        RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheResources);
        AddCommand("css_postprocessing_status", "Server-console-only postprocessing safety diagnostics", (player, command) =>
        {
            if (player != null) return;
            command.ReplyToCommand(_feature?.Diagnostics() ?? $"Postprocessing {Version}: feature not ready");
        });
        Server.NextWorldUpdate(Initialize);
    }

    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();

    private void Initialize()
    {
        if (_unloaded || _api != null) return;
        _api = PluginCapability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _feature != null) return;
        _feature = new Postprocessing(this, _api, _mapRunning);
        _api.RegisterFeature(_feature, FeatureType.Selectable);
        _feature.Start();
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
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
    private const int MaxPlayers = VolumeVisibility.Slots;

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
        /// <summary>null or "off" disables the effect. Otherwise this is the saved entry name.</summary>
        [JsonPropertyName("p")] public string? Effect { get; set; }
        [JsonPropertyName("f")] public float? Fade { get; set; }
        [JsonPropertyName("e")] public bool Exposure { get; set; } = true;
        [JsonPropertyName("x")] public int ExposureStep { get; set; }
        [JsonPropertyName("r")] public bool ReplaceMap { get; set; } = true;
        [JsonPropertyName("s")] public bool Spectators { get; set; } = true;
    }

    public override string Feature => FeatureName;

    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly VolumeVisibility _volumes = new();
    private readonly long[] _pendingApply = new long[MaxPlayers];
    private readonly BasePlugin _plugin;
    private bool _mapRunning;
    private int _mapGeneration;
    private long _applySequence;
    private bool _filterRegistered;
    private int _purged;
    private long _blocked;
    private long _transmitErrors;
    private bool _stopped;

    public Postprocessing(BasePlugin plugin, IVipCoreApi api, bool mapRunning) : base(api)
    {
        _plugin = plugin;
        _mapRunning = mapRunning;
        plugin.AddTimer(2f, () =>
        {
            if (_mapRunning && !_stopped && _volumes.Retiring.Count != 0)
                RetryRemovals();
        }, TimerFlags.REPEAT);
        plugin.RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _mapRunning = false;
            ResetMap();
        });
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is not { IsValid: true }) return HookResult.Continue;
            RemoveVolume(player.Slot);
            QueueApply(player);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            ClearPlayer(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            ClearPlayer(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            for (var slot = 0; slot < MaxPlayers; slot++)
                RemoveVolume(slot);
            RefreshVolumes();
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        plugin.RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            ResetMap();
            _mapRunning = true;
            var map = _mapGeneration;
            Server.NextFrame(() =>
            {
                if (!_stopped && _mapRunning && map == _mapGeneration)
                    Start();
            });
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            ClearPlayer(slot);
            _prefs.Remove(slot);
        });
    }

    public void Start()
    {
        if (!_mapRunning) return;
        RefreshVolumes();
        foreach (var player in Utilities.GetPlayers())
            QueueApply(player);
    }

    private void ResetMap()
    {
        _mapGeneration++;
        Array.Clear(_pendingApply);
        _prefs.Clear();
        _volumes.Clear();
        UpdateFilterRegistration();
    }

    private void QueueApply(CCSPlayerController player)
    {
        if (_stopped || !_mapRunning || !player.IsValid || player.IsBot) return;
        var slot = player.Slot;
        if ((uint)slot >= MaxPlayers || _pendingApply[slot] != 0) return;

        var controller = player.EntityHandle.Raw;
        var request = ++_applySequence;
        _pendingApply[slot] = request;
        Server.NextFrame(() =>
        {
            if (_stopped || _pendingApply[slot] != request) return;
            _pendingApply[slot] = 0;
            var current = Utilities.GetPlayerFromSlot(slot);
            if (current is { IsValid: true } && current.EntityHandle.Raw == controller)
                Apply(current);
        });
    }

    public void Stop()
    {
        _stopped = true;
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;

        if (_mapRunning)
        {
            RefreshVolumes();
            for (var slot = 0; slot < MaxPlayers; slot++)
                RemoveVolume(slot);
            RetryRemovals();
        }

        ResetMap();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped) return;
        _prefs.Remove(player.Slot);
        QueueApply(player);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        ClearPlayer(player.Slot);
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
                    UpdateVisibility(controller, p);
                    Save(controller);
                    OnSelectItem(controller, state);
                });

            menu.AddMenuOption(GetTranslatedText("postprocessing.Spectators", YesNo(prefs.Spectators)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Spectators = !p.Spectators;
                    UpdateVisibility(controller, p);
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
            ClearPlayer(controller.Slot);
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

    private PostprocessingEntry? Current(CCSPlayerController player, List<PostprocessingEntry> entries)
    {
        var saved = Load(player).Effect;
        if (!PostprocessingPolicy.HasExplicitEffect(saved)) return null;
        return entries.Find(entry => entry.Name == saved);
    }

    private bool CanUseFeature(CCSPlayerController player) =>
        IsClientVip(player) && PlayerHasFeature(player) && GetPlayerFeatureState(player) is FeatureState.Enabled;

    private List<PostprocessingEntry>? ReadEntries(CCSPlayerController player)
    {
        try
        {
            return GetFeatureValue<List<PostprocessingEntry>>(player);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private List<PostprocessingEntry>? TryGetEntries(CCSPlayerController player)
    {
        if (!CanUseFeature(player)) return null;
        var entries = ReadEntries(player);
        entries?.RemoveAll(entry => !Usable(player, entry));
        return entries is { Count: > 0 } ? entries : null;
    }

    private static bool Usable(CCSPlayerController player, PostprocessingEntry? entry) =>
        entry != null && !string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(entry.File) && Allowed(player, entry);

    private static bool Allowed(CCSPlayerController player, PostprocessingEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Required) && !AdminManager.PlayerHasPermissions(player, entry.Required))
            return false;

        if (string.Equals(entry.Team, "T", StringComparison.OrdinalIgnoreCase)) return player.TeamNum == 2;
        if (string.Equals(entry.Team, "CT", StringComparison.OrdinalIgnoreCase)) return player.TeamNum == 3;
        return true;
    }

    // ---- volume -----------------------------------------------------------------------------

    private void Apply(CCSPlayerController player)
    {
        if (_stopped || !_mapRunning || !player.IsValid || player.IsBot) return;
        var slot = player.Slot;
        if ((uint)slot >= MaxPlayers) return;
        _pendingApply[slot] = 0;
        RemoveVolume(slot);

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
            !CanUseFeature(player)) return;

        var prefs = Load(player);
        if (!PostprocessingPolicy.HasExplicitEffect(prefs.Effect)) return;
        var entries = ReadEntries(player);
        if (entries == null) return;
        PostprocessingEntry? entry = null;
        foreach (var candidate in entries)
        {
            if (candidate?.Name != prefs.Effect || !Usable(player, candidate)) continue;
            entry = candidate;
            break;
        }
        if (entry == null || pawn.AbsOrigin is not { } origin) return;
        var exposure = ExposureSteps[prefs.ExposureStep];

        uint handle = 0;
        try
        {
            using var keys = new CEntityKeyValues();
            keys.SetString("targetname", VolumeName);
            keys.SetString("postprocessing", entry.File);
            keys.SetBool("master", false);
            keys.SetBool("enableexposure", prefs.Exposure);
            keys.SetFloat("fadetime", Math.Max(prefs.Fade ?? entry.Fade, 0f));
            keys.SetFloat("minexposure", exposure.Min);
            keys.SetFloat("maxexposure", exposure.Max);
            keys.SetFloat("exposurespeedup", 1f);
            keys.SetFloat("exposurespeeddown", 1f);
            keys.SetBool("startdisabled", false);
            keys.SetInt("spawnflags", 4097);
            keys.SetVector("origin", origin);

            var volume = Utilities.CreateEntityByName<CPostProcessingVolume>(VolumeClass);
            if (volume is not { IsValid: true }) return;

            var entityHandle = volume.EntityHandle;
            handle = entityHandle.Raw;
            _volumes.Add(slot, handle, entityHandle.Index, prefs.Spectators, prefs.ReplaceMap);
            UpdateFilterRegistration();
            volume.DispatchSpawn(keys);
            if (!_volumes.Owns(handle) || ResolveVolume(handle) is not { } spawned)
            {
                _volumes.Forget(handle);
                UpdateFilterRegistration();
                return;
            }

            spawned.AcceptInput("SetParent", pawn, null, "!activator");
            spawned.Master = true;
            Utilities.SetStateChanged(spawned, "CPostProcessingVolume", "m_bMaster");
        }
        catch (Exception error)
        {
            if (handle != 0 && _volumes.Handle(slot) == handle)
                RemoveVolume(slot);
            _plugin.Logger.LogError(error, "Postprocessing spawn failed; effect removed");
        }
    }

    private void UpdateVisibility(CCSPlayerController player, Prefs prefs)
    {
        var slot = player.Slot;
        if ((uint)slot < MaxPlayers)
            _volumes.SetPreferences(slot, prefs.Spectators, prefs.ReplaceMap);
    }

    private void ClearPlayer(int slot)
    {
        if ((uint)slot >= MaxPlayers) return;
        _pendingApply[slot] = 0;
        RemoveVolume(slot);
    }

    private void RemoveVolume(int slot)
    {
        var handle = _volumes.Handle(slot);
        if (handle == 0) return;

        _volumes.Retire(handle, _volumes.Index(slot));
        TryRemoveVolume(handle);
    }

    private static CPostProcessingVolume? ResolveVolume(uint handle)
    {
        var volume = new CHandle<CPostProcessingVolume>(handle).Value;
        return volume is { IsValid: true } && volume.DesignerName == VolumeClass ? volume : null;
    }

    private void TryRemoveVolume(uint handle)
    {
        try
        {
            var volume = ResolveVolume(handle);
            if (volume == null)
            {
                _volumes.Forget(handle);
                UpdateFilterRegistration();
                return;
            }

            // Neutralize a master before requesting engine deletion. A retiring index stays hidden.
            if (volume.Master)
            {
                volume.Master = false;
                Utilities.SetStateChanged(volume, "CPostProcessingVolume", "m_bMaster");
            }
            volume.Remove();
        }
        catch (Exception error)
        {
            _plugin.Logger.LogError(error, "Postprocessing could not remove volume {Handle}", handle);
        }
    }

    private void RetryRemovals()
    {
        // Remove can synchronously call OnEntityDeleted.
        foreach (var handle in _volumes.Retiring.Keys.ToArray())
            TryRemoveVolume(handle);
    }

    private static bool IsOwned(CPostProcessingVolume volume) =>
        string.Equals(volume.Entity?.Name, VolumeName, StringComparison.Ordinal);

    private void RefreshVolumes()
    {
        _volumes.Map.Clear();
        foreach (var volume in Utilities.FindAllEntitiesByDesignerName<CPostProcessingVolume>(VolumeClass).ToArray())
            TrackSpawnedVolume(volume);
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (_mapRunning && entity.DesignerName == VolumeClass)
            TrackSpawnedVolume(entity.As<CPostProcessingVolume>());
    }

    private void TrackSpawnedVolume(CPostProcessingVolume volume)
    {
        if (!volume.IsValid) return;
        var handle = volume.EntityHandle;
        if (_volumes.Owns(handle.Raw)) return;

        if (IsOwned(volume))
        {
            _volumes.Retire(handle.Raw, handle.Index);
            UpdateFilterRegistration();
            _purged++;
            TryRemoveVolume(handle.Raw);
            return;
        }

        _volumes.Map[handle.Raw] = handle.Index;
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_volumes.NeedsFilter || _volumes.Map.Count != 0)
        {
            _volumes.Forget(entity.EntityHandle.Raw);
            UpdateFilterRegistration();
        }
    }

    private void UpdateFilterRegistration()
    {
        var needed = _mapRunning && _volumes.NeedsFilter;
        if (needed == _filterRegistered) return;
        if (needed)
            _plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        else
            _plugin.RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        _filterRegistered = needed;
    }

    public string Diagnostics()
    {
        if (!_mapRunning) return $"Postprocessing {VipPostprocessing.Version}: waiting for map";
        var owned = Utilities.FindAllEntitiesByDesignerName<CPostProcessingVolume>(VolumeClass)
            .Count(volume => volume.IsValid && IsOwned(volume));
        var active = System.Numerics.BitOperations.PopCount(_volumes.Active);
        return $"Postprocessing {VipPostprocessing.Version}: default=off active={active} owned={owned} orphan={owned - active} purged={_purged} blocked={_blocked} transmitErrors={_transmitErrors} filter={(_filterRegistered ? "on" : "off")}";
    }

    private int ShownOwner(CCSPlayerController? viewer)
    {
        if (viewer is not { IsValid: true }) return -1;
        if (viewer.PawnIsAlive) return viewer.Slot;
        if (_volumes.Spectatable == 0) return -1;

        var target = viewer.Pawn.Value?.ObserverServices?.ObserverTarget.Value;
        if (target is not { IsValid: true } || target.DesignerName != "player") return -1;
        var observed = target.As<CCSPlayerPawn>().OriginalController.Value;
        if (observed is not { IsValid: true }) return -1;
        var slot = observed.Slot;
        return (_volumes.Spectatable & VolumeVisibility.OwnerBit(slot)) != 0 ? slot : -1;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        var count = infoList.Count;
        var retiring = _volumes.Retiring;
        var mapVolumes = _volumes.Map;
        long blocked = 0;
        for (var i = 0; i < count; i++)
        {
            var (info, viewer) = infoList[i];
            var shown = -1;
            try
            {
                if (_volumes.Active != 0)
                    shown = ShownOwner(viewer);
            }
            catch (Exception)
            {
                _transmitErrors++;
            }

            var hidden = _volumes.HiddenOwners(shown);
            blocked += System.Numerics.BitOperations.PopCount(hidden) + retiring.Count;
            while (hidden != 0)
                info.TransmitEntities.Remove(_volumes.Index(VolumeVisibility.TakeSlot(ref hidden)));
            if (retiring.Count != 0)
                foreach (var index in retiring.Values)
                    info.TransmitEntities.Remove(index);
            if (_volumes.HideMap(shown))
                foreach (var index in mapVolumes.Values)
                    info.TransmitEntities.Remove(index);
        }
        _blocked += blocked;
    }
}
