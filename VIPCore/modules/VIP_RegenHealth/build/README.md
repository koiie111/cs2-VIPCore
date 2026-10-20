# VIP health regeneration

Add `HealthRegen` only to the VIP groups that should receive this feature.
For example, put this in the `PREMIUM.Values` object in `vip.json`:

```json
"HealthRegen": {
  "Health": 1,
  "Delay": 8,
  "Interval": 1
}
```

`Health` is the HP restored per tick, `Delay` is the time since the latest health
damage, and `Interval` is the time between subsequent heals (all times in seconds).
The example restores the first HP after eight seconds without damage, followed
by one HP each second, up to the player's maximum health. Every new health hit
restarts the full delay. Fractional times are supported with a 0.1-second polling
resolution. Server stalls do not cause catch-up healing bursts.

The module supports loading on a running server. Reload VIPCore configuration
with `css_vip_reload` after editing `vip.json`, then load this module with
`css_plugins load VIP_RegenHealth`. No game-server restart is required.

Optional VIPCore language entries:

- RU: `"HealthRegen": "Регенерация здоровья"`
- EN: `"HealthRegen": "Health Regeneration"`

Run the deterministic timing regression checks from the repository root:

```sh
dotnet run --project VIPCore/tests/VIP_RegenHealth.ScheduleTests -c Release
```
