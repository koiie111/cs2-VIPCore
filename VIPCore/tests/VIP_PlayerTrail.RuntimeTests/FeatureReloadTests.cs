using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VIPCore;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

internal static class FeatureReloadTests
{
    internal static void Run()
    {
        var group = JsonSerializer.Deserialize<VipGroup>(
            """{"Values":{"zero":0,"off":false,"object":{},"array":[],"empty":"","text":"on","null":null}}""")!;
        foreach (var key in new[] { "zero", "off", "object", "array", "text" })
            Require(group.HasFeature(key), $"Configured value for {key}");
        foreach (var key in new[] { "empty", "null", "missing" })
            Require(!group.HasFeature(key), $"No access for {key}");

        // BasePlugin normally registers native listeners. This test only needs the core's managed state.
        var core = (VipCore)RuntimeHelpers.GetUninitializedObject(typeof(VipCore));
        typeof(VipCore).GetField(nameof(VipCore.Features))!
            .SetValue(core, new ConcurrentDictionary<string, Feature>());
        core.CoreConfig = new CoreConfig { VipLogging = false };
        core.Config = JsonSerializer.Deserialize<Config>(
            """
            {"Groups":{"VIP":{"Values":{"PlayerTrail":{"Width":1,"Colors":["Blue #0000FF"]},"Other":1}},
                       "Premium":{"Values":{"PlayerTrail":{"Width":2,"Particles":[{"File":"trail.vpcf"}]}}},
                       "NoTrail":{"Values":{"Other":2}}}}
            """)!;
        var api = (VIPCore.VipCoreApi)RuntimeHelpers.GetUninitializedObject(typeof(VIPCore.VipCoreApi));
        typeof(VIPCore.VipCoreApi).GetField("_vipCore", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.SetValue(api, core);
        typeof(VipCore).GetField(nameof(VipCore.Users))!
            .SetValue(core, new ConcurrentDictionary<ulong, User>());
        typeof(VIPCore.VipCoreApi).GetField("_playersCookie", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.SetValue(api, new Dictionary<ulong, PlayerCookie>());
        core.VipApi = api;
        core.Users[1] = new User { name = "online", group = "VIP" };
        core.Users[2] = new User { name = "other", group = "NoTrail" };
        var previous = new TestTrail(api);
        api.RegisterFeature(previous, FeatureType.Selectable);
        Require(core.Users[1].FeatureState["PlayerTrail"] == FeatureState.Enabled, "Live registration for online VIP");
        Require(core.Users[2].FeatureState["PlayerTrail"] == FeatureState.NoAccess, "Unconfigured group has no access");
        var configured = Values(core);
        api.UnRegisterFeature(previous);

        Require(!core.Features.ContainsKey("PlayerTrail"), "Unload removes the menu handler");
        Require(Values(core).SequenceEqual(configured), "Unload preserves each group's settings");

        Require(!api.GetAllRegisteredFeatures().Any(f => f.feature == "PlayerTrail"), "Unloaded feature is hidden");
        var replacement = new TestTrail(api);
        api.RegisterFeature(replacement, FeatureType.Selectable);
        api.UnRegisterFeature(previous);
        Require(ReferenceEquals(core.Features["PlayerTrail"].OnSelectItem?.Target, replacement),
            "Reload registers the new menu handler");
        Require(Values(core).SequenceEqual(configured), "Reload reuses configured values without a config reload");
        Require(string.IsNullOrEmpty(core.Config.Groups["NoTrail"].Values["PlayerTrail"].ToString()),
            "Reload does not grant the feature to an unconfigured group");

        var user = core.Users[1];
        var toggle = new TestToggle(api);
        api.RegisterFeature(toggle);
        var configuredGroup = core.Config;
        var withoutToggle = new Config();
        configuredGroup.Groups["VIP"].Values[toggle.Feature] = 1;
        user.FeatureState[toggle.Feature] = FeatureState.Disabled;
        void Apply(string key, FeatureState state) => user.FeatureState[key] = state;
        core.Config = withoutToggle;
        core.RefreshGroupAccess(1, user, configuredGroup, Apply);
        Require(user.FeatureState[toggle.Feature] == FeatureState.NoAccess, "Config revocation clears the toggle");
        core.Config = configuredGroup;
        core.RefreshGroupAccess(1, user, withoutToggle, Apply);
        Require(user.FeatureState[toggle.Feature] == FeatureState.Disabled, "Regrant restores the saved toggle");
        core.RefreshGroupAccess(1, user, configuredGroup, (_, _) => throw new Exception("Spurious config change"));
    }

    private static IEnumerable<string?> Values(VipCore core) =>
        core.Config.Groups.Values.Select(group => group.Values.GetValueOrDefault("PlayerTrail")?.ToString()).ToArray();

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }

    private sealed class TestToggle(IVipCoreApi api) : VipFeatureBase(api)
    {
        public override string Feature => "endurance";
    }

    private sealed class TestTrail(IVipCoreApi api) : VipFeatureBase(api)
    {
        public override string Feature => "PlayerTrail";
    }
}
