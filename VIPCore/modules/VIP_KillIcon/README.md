# VIP_KillIcon

Заменяет иконку оружия в килфиде (kill feed) для VIP-игрока, который совершил убийство, в зависимости от того, *как* оно было совершено: хедшот, без прицела, через дым, в прыжке и т.д.

Порт модуля `KillIcon` из [ByDexterTR/CS2Plugins](https://github.com/ByDexterTR/CS2Plugins/blob/main/VIPCore/modules/KillIcon.cs) под API этого ядра (`VipFeatureBase`, `vipcore:core`).

## Установка

1. Соберите модуль вместе с остальными (CI кладёт его в `VIP_Modules.zip`) или вручную:
   ```bash
   dotnet publish VIPCore/modules/VIP_KillIcon/VIP_KillIcon.csproj -c Release
   ```
2. Скопируйте папку `VIP_KillIcon` в `addons/counterstrikesharp/plugins/` (рядом с `VIPCore`).
3. Добавьте фичу `KillIcon` в нужные группы `vip.json` (см. ниже) и выполните `css_vip_reload` либо перезапустите сервер.

Название пункта в `!vip` берётся из `lang/*.json` ядра (ключ `KillIcon`). Игрок может включать и выключать фичу в меню.

## Конфиг (`vip.json`)

```json
{
  "Groups": {
    "VIP1": {
      "Values": {
        "KillIcon": {
          "headshot": "",
          "penetrated": "",
          "noscope": "",
          "throughsmoke": "",
          "blindkill": "",
          "assistflash": "",
          "jumpkill": "",
          "dominated": "",
          "squadwipe": "",
          "weapons": {
            "ak47": "awp",
            "weapon_knife": "hegrenade"
          }
        }
      }
    }
  }
}
```

Все поля необязательны; пустая строка или отсутствие поля отключает правило. Имена полей — строго в нижнем регистре.

| Поле | Условие |
|------|---------|
| `squadwipe` | жертва была последним живым игроком своей команды |
| `dominated` | убийство установило доминирование над жертвой |
| `jumpkill` | убийца находился в воздухе |
| `blindkill` | убийца был ослеплён |
| `assistflash` | ассист флешкой |
| `noscope` | убийство без прицела (снайперкой) |
| `throughsmoke` | через дым |
| `penetrated` | через стену/объект |
| `headshot` | хедшот |
| `weapons` | замена по оружию: ключ — имя оружия из события (`ak47` или `weapon_ak47`), значение — имя иконки |

### Приоритет

Применяется первое сработавшее правило, в порядке: `squadwipe` → `dominated` → `jumpkill` → `blindkill` → `assistflash` → `noscope` → `throughsmoke` → `penetrated` → `headshot` → `weapons`.

Самоубийства и убийства без валидного убийцы не затрагиваются. Работает только пока у убийцы фича включена (`Enabled`).

## Важно

- Значение — это **имя иконки оружия**, которое клиент ищет в своём HUD-наборе (например, `knife`, `awp`, `hegrenade`, `inferno`). Если иконки с таким именем нет, в килфиде ничего не отобразится. Для собственных иконок нужен клиентский ресурс (например, Workshop-аддон).
- Модуль меняет поле `weapon` события `player_death` до рассылки клиентам (`HookMode.Pre`). Другие плагины, которые читают `weapon` этого же события после него, увидят подменённое значение; статистика (ранги, `sa_*`) на это может реагировать. Проверьте на своих плагинах.
