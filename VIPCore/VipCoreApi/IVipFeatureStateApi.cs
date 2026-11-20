using CounterStrikeSharp.API.Core;
using static VipCoreApi.IVipCoreApi;

namespace VipCoreApi;

public interface IVipFeatureStateApi
{
    // A missing VIP record or feature has no selection to restore.
    FeatureState GetPlayerFeatureStateOrDefault(CCSPlayerController player, string feature);

    // Programmatic changes via SetPlayerFeatureState. Menu clicks already dispatch OnSelectItem.
    event Action<CCSPlayerController, string, FeatureState>? PlayerFeatureStateSet;
}

// Optional notification for modules that cache live effects or recipient preferences.
public interface IVipFeatureAvailabilityApi : IVipFeatureStateApi
{
    event Action? FeatureAvailabilityChanged;
}
