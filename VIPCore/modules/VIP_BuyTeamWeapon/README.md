# BuyTeamWeapon 1.0.3

Purchases wait for the deferred VIPCore spawn-feature callback to finish. VIP_Money can otherwise reset the account after an early purchase. The next-frame barrier works with Money registered before or after this feature. Each player spawn clears readiness; disconnect and module unload clear local state. Account replication targets CCSPlayerController_InGameMoneyServices.m_iAccount.

Commands, prices and configured VIP access are unchanged.

Validation: `dotnet run --project VIPCore/tests/BuyTeamWeapon.Tests -c Release` compiles the production source with lightweight API doubles and exercises both spawn-feature orders. It does not replace a real client acceptance check.

For the 2026-10-26 no-restart rollout, build with `-p:AssemblyName=VIP_BuyTeamWeaponPaid` and deploy the three matching files to `plugins/VIP_BuyTeamWeaponPaid/`. Unload the old module, back up and move its folder outside plugins, load the replacement and reload VIP configuration. Never retain both installations. The normal source build retains its established assembly name for existing packaging.

Some running CSS processes reject hot loading due to global MySqlConnector reflection-scanning errors. A new assembly identity only solves the separate retained-assembly-name error. If activation fails, keep the vulnerable module unloaded and report the affected instance; do not restart a server without authorization.
