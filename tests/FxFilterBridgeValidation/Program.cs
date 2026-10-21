using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core.Capabilities;

if (args.Contains("missing"))
{
    for (var i = 0; i < 4; i++)
        Check(!FxFilterBridge.Hidden(1, i) && !FxFilterBridge.Disabled(i), "missing contract fails open " + i);
    Check(!FxFilterBridge.RainbowDisabled(), "missing contract rainbow fails open");
}
else
{
    System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "FxFilterApi.dll"));
    WithContract.Run();
}

static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }

internal static class WithContract
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run()
    {
        void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        Assert(!FxFilterBridge.Hidden(1, 0), "missing service fails open");
        var filter = new Filter();
        if (FxFilterApi.FxFilterProvider.Attach(filter))
            Capabilities.RegisterPluginCapability(new PluginCapability<FxFilterApi.IFxFilter>("4ill:fx"), FxFilterApi.FxFilterProvider.Get);
        // The bridge deliberately caches capability lookups for one second.
        Thread.Sleep(1100);
        Assert(FxFilterBridge.Hidden(1, 0) && !FxFilterBridge.Hidden(2, 0), "viewer-specific preferences");
        Assert(FxFilterBridge.Disabled(2) && !FxFilterBridge.Disabled(0), "server categories");
        Assert(FxFilterBridge.RainbowDisabled(), "rainbow optional server setting");
        filter.Active = false;
        FxFilterApi.FxFilterProvider.Detach(filter);
        Assert(!FxFilterBridge.Hidden(1, 0) && !FxFilterBridge.Disabled(2) && !FxFilterBridge.RainbowDisabled(), "unload immediately fails open with cached service");
        var replacement = new Filter();
        Assert(!FxFilterApi.FxFilterProvider.Attach(replacement), "reload reuses shared registration");
        Thread.Sleep(1100);
        Assert(FxFilterBridge.Hidden(1, 0), "reload discovers replacement service");
    }
    private sealed class Filter : FxFilterApi.IFxFilter, FxFilterApi.IFxServerSettings
    {
        public bool Active = true;
        public bool IsHiddenFor(int slot, FxFilterApi.FxCategory category) => Active && slot == 1 && category == FxFilterApi.FxCategory.Trails;
        public bool IsDisabledOnServer(FxFilterApi.FxCategory category) => Active && category == FxFilterApi.FxCategory.C4;
        public bool RainbowDisabled => Active;
    }
}
