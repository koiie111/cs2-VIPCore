using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core.Capabilities;

// Keep FxFilterApi types outside this facade: the CLR may fail to JIT a method
// referencing the missing optional assembly before entering that method's try/catch.
internal static class FxFilterBridge
{
    private static long _retryAt;
    public static bool Hidden(int viewer, int category)
    {
        if (Environment.TickCount64 < _retryAt) return false;
        try { return FxFilterBinding.Hidden(viewer, category); }
        catch (Exception) { _retryAt = Environment.TickCount64 + 1000; return false; }
    }
    public static bool Disabled(int category)
    {
        if (Environment.TickCount64 < _retryAt) return false;
        try { return FxFilterBinding.Disabled(category); }
        catch (Exception) { _retryAt = Environment.TickCount64 + 1000; return false; }
    }
    public static bool RainbowDisabled()
    {
        if (Environment.TickCount64 < _retryAt) return false;
        try { return FxFilterBinding.RainbowDisabled(); }
        catch (Exception) { _retryAt = Environment.TickCount64 + 1000; return false; }
    }
}
internal static class FxFilterBinding
{
    private static readonly PluginCapability<FxFilterApi.IFxFilter> Capability = new("4ill:fx");
    private static FxFilterApi.IFxFilter? _service;
    private static long _nextLookup;
    private static FxFilterApi.IFxFilter? Service()
    {
        var now = Environment.TickCount64;
        if (now >= _nextLookup)
        {
            _nextLookup = now + 1000;
            _service = null;
            try { _service = Capability.Get(); } catch (Exception) { }
        }
        return _service;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Hidden(int viewer, int category) => Service()?.IsHiddenFor(viewer, (FxFilterApi.FxCategory)category) ?? false;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Disabled(int category) => Service()?.IsDisabledOnServer((FxFilterApi.FxCategory)category) ?? false;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RainbowDisabled() => Service() is FxFilterApi.IFxServerSettings { RainbowDisabled: true };
}
