namespace VipCoreApi;

public interface IVipConfigReloadApi
{
    // Raised after the core has applied the new configuration and refreshed online feature access.
    event Action? ConfigReloaded;
}
