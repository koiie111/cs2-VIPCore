# BuyTeamWeapon 1.0.5

Enable the module for a VIP group with `"BuyTeamWeapon": true` in `vip.json`. Eligible players on either team can buy all 18 team-exclusive firearms in a buy zone at standard CS2 prices. For example, `!mp9` costs $1250, and `!m4a1` buys an M4A1-S for $2900.

At round start, the chat lists the opposing team's commands. Keep the module's bundled `lang` directory alongside its DLL. CounterStrikeSharp loads those files using the server locale.

Purchases wait for VIPCore's spawn bonuses, including VIP_Money. The plugin deducts and replicates the price after the game hands out the weapon.

Run `dotnet run --project VIPCore/tests/BuyTeamWeapon.Tests -c Release` to check the commands, purchase conditions and localized hints. In CS2, join T with the feature enabled and confirm that the round hint contains `!mp9`. In the buy zone, buy it with $1250 and confirm that you receive an MP9 and the HUD shows $0. Repeat on CT and confirm that the round hint lists T weapons.
