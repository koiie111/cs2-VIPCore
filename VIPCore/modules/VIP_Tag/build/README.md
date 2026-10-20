# Shows a tag on the scoreboard and a coloured tag in chat for the VIP player

# Config

### in vip.json

```json
"Tag": {
  "ScoreboardTag": "ADMIN",
  "ChatTag": "[ADMIN]",
  "ColorTag": "red",
  "NameColor": "red",
  "MessageColor": "grey"
}
```

`ScoreboardTag` - plain text shown on the scoreboard (clan tag)

`ChatTag` - prefix shown before the name in chat

`ColorTag` - colour of the chat tag prefix

`NameColor` - colour of the player name in chat

`MessageColor` - colour of the message body in chat

Supported colours: `default, white, darkred, green, lightyellow, lightblue, olive, lime, red, lightpurple, purple, grey, yellow, gold, silver, blue, darkblue, bluegrey, magenta, lightred, orange, teamcolor`

# Gag / Silence

The chat tag is rendered by this module, so it respects gag/silence. The admin plugin is detected
automatically at load (any/all of these):

- CS2-SimpleAdmin (via its `simpleadmin:api`),
- Iks_Admin (via its `iksadmin:core` capability),
- CS2-AdminPlus (via its `communication_data.json`).

A gagged or silenced player sends nothing through the tag. If none of these admin plugins is
installed, a warning is logged and gag/silence is not enforced for the chat tag.

# Menu

The feature is a toggle in `!vip` - the player can Enable or Disable their tag.


# Premium color selection (4ill)

Keep `Tag` as the regular toggle. Add `TagColor` only to groups allowed to choose a color:

```json
"Tag": {
  "ScoreboardTag": "PREMIUM",
  "ChatTag": "[PREMIUM]",
  "ColorTag": "gold",
  "NameColor": "gold",
  "MessageColor": "default"
},
"TagColor": [
  "default", "darkred", "green", "yellow", "blue", "olive", "lime", "red",
  "lightpurple", "purple", "grey", "gold", "silver", "darkblue", "lightred", "teamcolor"
]
```

`TagColor` opens a palette in `!vip`. It changes the chat prefix and player name together;
message text retains `MessageColor`. The choice persists in VIPCore cookies and is revalidated
against the player's current palette on each message. A downgraded player immediately falls back
to their group's fixed colors. Scoreboard tags are plain text.

For VIP, omit `TagColor` and set `ScoreboardTag` to `VIP`, `ChatTag` to `[VIP]`,
and both `ColorTag` and `NameColor` to `blue`. The premium default is gold.
Add `"TagColor": "Цвет тега и ника"` to the VIPCore language files for the menu label.

The module registers once both at startup and on live load. Existing VIP players have their VIP
feature states refreshed when the module registers. Unloading detaches VIPCore feature events.
The package includes MySqlConnector because some deployed SimpleAdmin API types reference it
during CounterStrikeSharp type discovery. Do not copy or replace the CounterStrikeSharp runtime.

The deployment palette has 15 distinct fixed colors plus the dynamic team-color option.
Aliases such as blue/lightblue, yellow/lightyellow and gold/orange are not repeated in the menu.
