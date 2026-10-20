# KillIcon for 4ill VIPCore

Inspired by https://github.com/ByDexterTR/CS2Plugins/blob/main/VIPCore/modules/KillIcon.cs.
Standalone module for the VipCoreApi capability used by this fork. Does not replace VIPCore.

Add `"KillIcon": ["knife", "taser", "hegrenade", "inferno", "awp", "ak47", "m4a1", "m4a1_silencer", "deagle", "ssg08", "p90", "negev"]` to eligible groups' Values in vip.json.
Omit the KillIcon key in groups without access. Add `"KillIcon": "Иконка убийства"` to VIPCore/lang/ru.json and en.json.
The selectable !vip item offers the allowed icons and a standard weapon option (the default).
Choice uses VIPCore's persistent player cookies. Access and the allowed list are checked again on every selection and kill.
Only the death notice weapon field changes; damage, headshots, assists and other event fields are preserved. Other plugins reading player_death after this pre-hook will also see the selected weapon name.

Build with `dotnet build VIP_KillIcon.csproj -c Release`. Deploy only VIP_KillIcon.dll and its deps.json/PDB into plugins/VIP_KillIcon; the shared VipCoreApi and CounterStrikeSharp runtime must already exist.
