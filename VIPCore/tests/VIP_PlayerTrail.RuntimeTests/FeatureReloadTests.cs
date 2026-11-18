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
        var previous = new TestTrail(api);
        api.RegisterFeature(previous, FeatureType.Selectable);
        var configured = Values(core);
        api.UnRegisterFeature(previous);

        Require(!core.Features.ContainsKey("PlayerTrail"), "Unload removes the menu handler");
        Require(Values(core).SequenceEqual(configured), "Unload preserves each group's settings");

        var replacement = new TestTrail(api);
        api.RegisterFeature(replacement, FeatureType.Selectable);
        Require(ReferenceEquals(core.Features["PlayerTrail"].OnSelectItem?.Target, replacement),
            "Reload registers the new menu handler");
        Require(Values(core).SequenceEqual(configured), "Reload reuses configured values without a config reload");
        Require(string.IsNullOrEmpty(core.Config.Groups["NoTrail"].Values["PlayerTrail"].ToString()),
            "Reload does not grant the feature to an unconfigured group");
    }

    private static IEnumerable<string?> Values(VipCore core) =>
        core.Config.Groups.Values.Select(group => group.Values.GetValueOrDefault("PlayerTrail")?.ToString()).ToArray();

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }

    private sealed class TestTrail(IVipCoreApi api) : VipFeatureBase(api)
    {
        public override string Feature => "PlayerTrail";
    }
}
