# VIP Test 3.0: one free hour of play per player

Every SteamID gets **one hour of played time** of a VIP group, once, across all servers that share the VIP database.
The row in `vip_test_trials` is both the «once» lock and the log used to measure trial → purchase conversion.

- **New players** (never seen in `vip_test_known_players`) receive the trial automatically 5 seconds after joining.
- **Everyone else** can take it once with `!viptest`. On the first join after the release an old player sees a one-time hint.
  `vip_test_known_players` is seeded once from analytics (see the plan, task C7); afterwards the module adds players itself.
- **Played time**: a timer adds seconds only while the player is in a team (T/CT). Spectators and unassigned players do not
  spend the budget. Seconds are saved every 60 s and on disconnect; a reconnect resumes the remainder and never extends it.
- **Chat messages**: on issue, 10 minutes before the end (`WarnBeforeSeconds`) and when the time is used up, each with the
  offer text from the config.
- **Purchase during the trial**: detected on the next save (a `vip_users` row can only be a paid one); the trial ends with
  `end_reason = paid_privilege`, the player keeps the purchased privilege and is told by the site to reconnect.
- **Old redemptions** (`vipcore_test`, VIP Test 2.x) block a new trial and are recorded as `legacy`.
- A database failure never grants a trial.

The temporary VIP lives in server memory only. It is granted for a 12-hour safety net and ended by this module through
`IVipCoreApi.EndTemporaryVip`, which does not touch the database. That method exists in VIPCore from the `global-vip`
branch: **deploy VIPCore before this module**. On an older core the module logs an error and the VIP ends by the safety net.

```json
{
  "VipTestEnabled": true,
  "VipTestGroup": "VIP",
  "BudgetSeconds": 3600,
  "WarnBeforeSeconds": 600,
  "AutoGrantOnJoin": true,
  "AutoGrantNewPlayersOnly": true,
  "TickSeconds": 15,
  "WebServerId": 0,
  "OfferText": "VIP на 7 дней — 59 ₽: 4ill.ru/vip",
  "MessageGranted": "[4ILL] Тебе выдан VIP на 1 час игрового времени — считается, только пока ты играешь. Возможности: !vip",
  "MessageResumed": "[4ILL] VIP-тест продолжается. Осталось {0} игры.",
  "MessageAlreadyRunning": "[4ILL] VIP-тест уже идёт. Осталось {0} игры.",
  "MessageWarn": "[4ILL] До конца VIP-теста {0} игры. {1}",
  "MessageEnded": "[4ILL] VIP-тест закончился. Понравилось? {0}",
  "MessageHint": "[4ILL] Тебе доступен бесплатный VIP на 1 час игры — напиши !viptest"
}
```

`WebServerId` is `servers_web.id` of the server, stored as the place of issue. Legacy `VipTestDuration`, `VipTestCount` and
`VipTestCooldown` are ignored. The group must exist in `vip.json`, otherwise the module logs an error and issues nothing.

Tables are created with `IF NOT EXISTS` and match `php/migrations/sql/20261021_0001_create_vip_global_storage.sql` of the
site repository.

Build: `dotnet build VIPCore/modules/VIP_Test/VIP_Test.csproj -c Release`. The assembly identity is
`VIP_Test_PlayTime_3_0_0` (avoids a collision with the previous deployed assembly during live replacement); the checked-in
distribution keeps the standard `plugins/VIP_Test/VIP_Test.dll` names.

Tests need an **empty disposable MySQL database**:

```sh
VIP_TEST_TEST_CONNECTION='Server=localhost;Database=vip_test_disposable;User ID=...;Password=...' \
  dotnet run --project VIPCore/tests/VIP_Test.PlayTimeTests
```

They cover the time rules (team counting, warning, Russian durations), first and repeated claims, accumulation and the cap,
ending, paid privilege before and during the trial, legacy redemptions, 20 simultaneous claims, restart, and fail-closed on
a database error.
