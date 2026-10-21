# VIP Test: one lifetime redemption

VIP Test can be redeemed once per Steam ID in the configured VIP database. A row in
`vipcore_test` permanently blocks another redemption, even when its old `end_time`
has expired. Existing rows are retained. An existing `vip_test_count > 0` cookie is
also imported as a permanent database record when that player requests a trial.
Servers sharing a VIP database share this limit; separate VIP databases keep their
existing independent scopes.

The database primary key reserves a single session before granting privileges.
Concurrent requests share its original expiry. Reconnects and plugin reloads resume
only the remaining time using temporary VIP privileges; they never extend the expiry.
Existing deployed `vipcore_test_sessions` records remain valid and are never reset.
Database failures never grant a trial. Paid privileges are checked in storage before
reserving a trial. Do not delete redemption/session rows when expiring VIP privileges.

```json
{
  "VipTestEnabled": true,
  "VipTestDuration": 3600,
  "VipTestGroup": "PREMIUM",
  "AutoGrantOnJoin": true
}
```

`css_viptest` and automatic grants use the same permanent check. `AutoGrantOnJoin`
waits five seconds after a full connection to allow VIP Core to load existing
privileges. Missing `AutoGrantOnJoin` retains manual-only behavior for old configs.
Legacy `VipTestCooldown` and `VipTestCount` fields are ignored; there is no reset
interval or configurable repeat count. VIP duration remains independent of the
permanent redemption marker.

Build with `dotnet build VIPCore/modules/VIP_Test/VIP_Test.csproj -c Release`.
The assembly identity is `VIP_Test_Lifetime_2_1_0` to avoid collisions with the
previous deployed assembly during live replacement. The checked-in distribution
keeps the standard `plugins/VIP_Test/VIP_Test.dll` filename (and corresponding
`.deps.json`/`.pdb` names) for normal CounterStrikeSharp discovery. When packaging
a new build, copy the three `VIP_Test_Lifetime_2_1_0.*` files to those standard names.
Integration tests require an **empty disposable MySQL database**:

```sh
VIP_TEST_TEST_CONNECTION='Server=localhost;Database=vip_test_disposable;User ID=...;Password=...' \
  dotnet run --project VIPCore/tests/VIP_Test.LifetimeTests
```

Tests cover first redemption, reconnect without extension, expired legacy records,
cookie migration, concurrent claims, restart persistence, deployed session migration,
paid VIP protection, and database failure without a grant. The test refuses
nonempty databases and removes its own table afterward.
