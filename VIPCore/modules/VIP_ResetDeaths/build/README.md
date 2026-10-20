# Allows a VIP player to reset their deaths

# Config

### in vip.json
`"ResetDeaths": true`

# in lang (not necessarily)

RU: `"vip.ResetDeaths": "Ваши смерти сброшены!"`
EN: `"vip.ResetDeaths": "Your deaths are reset!"`

## Live installation

Version 1.0.1 registers the feature when loaded after VIPCore is already ready.
It also removes its CoreReady event handler on unload.

Set `"ResetDeaths": true` in the `Values` of each allowed group in
`configs/plugins/VIPCore/vip.json`. To enable it for both tiers, set it for
`VIP` and `PREMIUM`. Run `css_vip_reload`, then
`css_plugins load VIP_ResetDeaths` from the server console. A server restart
is not required. Players use `!rd` or `!resetdeaths`.
