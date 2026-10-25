namespace VIP_Postprocessing;

internal static class PostprocessingPolicy
{
    public static bool HasExplicitEffect(string? effect) => !string.IsNullOrEmpty(effect) && effect != "off";
    public static bool CanTransmit(int owner, int shown) => owner >= 0 && owner < 64 && shown == owner;
}
