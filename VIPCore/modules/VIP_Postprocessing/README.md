# VIP_Postprocessing

Накладывает на экран VIP-игрока постобработку (цветокоррекцию) через `post_processing_volume`, закреплённый на игроке. Эффект появляется при спавне, снимается при смерти или смене команды и восстанавливается у живого игрока в начале раунда. Объём передаётся владельцу и, по его выбору, зрителям, наблюдающим за ним от первого или третьего лица. Игрок настраивает эффект в меню `!vip` (см. «Меню игрока»).

Порт модуля `Postprocessing` из [ByDexterTR/CS2Plugins](https://github.com/ByDexterTR/CS2Plugins/blob/main/VIPCore/modules/Postprocessing.cs) под API этого ядра (`VipFeatureBase`, `vipcore:core`).

## Установка

1. Соберите модуль вместе с остальными (CI кладёт его в `VIP_Modules.zip`) или вручную:
   ```bash
   dotnet publish VIPCore/modules/VIP_Postprocessing/VIP_Postprocessing.csproj -c Release
   ```
2. Скопируйте папку `VIP_Postprocessing` в `addons/counterstrikesharp/plugins/` (рядом с `VIPCore`).
3. Загрузите файлы постобработки (`.vpost` / `.vpost_c`) на сервер и клиентам (Workshop-аддон или FastDL — зависит от вашей схемы раздачи ресурсов).
4. Добавьте фичу `Postprocessing` в нужные группы `vip.json` (см. ниже) и перезапустите сервер или смените карту. `css_vip_reload` подхватит новые записи, но **ресурсы предзагружаются при старте карты**, поэтому новый файл заработает только после смены карты.

Строки меню берутся из `lang/*.json` ядра (ключи `Postprocessing` и `postprocessing.*`). Обновите `lang` ядра до версии с этим модулем, иначе пункты меню будут показаны ключами.

## Конфиг (`vip.json`)

Значение фичи — список эффектов:

```json
{
  "Groups": {
    "VIP1": {
      "Values": {
        "Postprocessing": [
          {
            "name": "Warm",
            "file": "materials/postprocessing/warm.vpost",
            "fade": 0.25
          }
        ]
      }
    },
    "VIP2": {
      "Values": {
        "Postprocessing": [
          {
            "name": "Warm",
            "file": "materials/postprocessing/warm.vpost",
            "fade": 0.25
          },
          {
            "name": "Night",
            "file": "materials/postprocessing/night.vpost",
            "fade": 1,
            "required": "@css/vip",
            "team": "CT"
          }
        ]
      }
    }
  }
}
```

Пути в примере — условные: подставьте ресурсы, которые реально есть на сервере или в вашем аддоне. Разные группы могут иметь разные наборы эффектов.

Имена полей — строго в нижнем регистре.

| Поле | Описание |
|------|----------|
| `name` | название в меню (обязательно, служит и идентификатором выбора — не меняйте его у уже выданных эффектов, иначе сохранённый выбор перестанет применяться) |
| `file` | путь к ресурсу постобработки `.vpost` (обязательно) |
| `fade` | время плавного перехода в секундах, по умолчанию `0.25` (как в оригинале) |
| `required` | необязательно: право или группа CounterStrikeSharp (`@css/vip`, `#css/admin`), нужное для этого эффекта |
| `team` | необязательно: `T` или `CT` — эффект доступен только за эту команду |

Запись без `name` или без `file` игнорируется; записи, недоступные игроку по `required`/`team`, в его меню не показываются. Значение `fade` из конфига служит значением по умолчанию — игрок может изменить его в меню. Все файлы из `file` всех групп автоматически добавляются в предзагрузку карты (читаются прямо из `vip.json`).

## Меню игрока

Пункт `Postprocessing` в `!vip` открывает меню:

| Пункт | Что делает |
|-------|------------|
| Эффект: имя | открывает список доступных эффектов + «Выключить»; выбранный эффект сразу применяется к живому игроку |
| Время перехода: N с | по клику переключает 0 → 0.25 → 0.5 → 1 → 2 |
| Автоэкспозиция: Да/Нет | включает или выключает автоматическую экспозицию (адаптацию яркости) |
| Диапазон экспозиции: min - max | при включённой автоэкспозиции по клику переключает 0.5–2 → 0.25–1 → 1–3 → 0.75–1.5 |
| Заменять эффекты карты: Да/Нет | скрывать ли собственную постобработку карты, пока активен ваш эффект |
| Зрители видят: Да/Нет | видят ли ваш эффект те, кто наблюдает за вами после смерти |
| Сбросить настройки | возвращает `fade` из конфига и все параметры по умолчанию |

Без явно сохранённого и доступного выбора постобработка выключена. Все настройки хранятся одной записью в cookie ядра и сохраняются между сессиями; пункты настройки эффекта скрыты, пока он выключен.

## Отличия от оригинала

- Фича регистрируется как `Selectable` в `VipFeatureBase`: игрок включает и выключает её в `!vip`, а конфиг лежит в `vip.json` (в оригинале — отдельный модуль на `VipModule` с `GroupValue`).
- Добавлены персональные настройки: время перехода, автоэкспозиция и её диапазон (в оригинале жёстко `0.5–2`), замена эффектов карты и видимость для зрителей (в оригинале игрок мог только выбрать эффект по имени).
- Скрытие объёма от других игроков сделано через `Listeners.CheckTransmit` API CounterStrikeSharp, а не через `HookTransmit` оригинального ядра.
- Для `required` модуль проверяет флаг `@…` через `AdminManager.PlayerHasPermissions`, а группу `#…` через `AdminManager.PlayerInGroup`.
- Эффект не создаётся для ботов.
- Оригинал по умолчанию показывал эффект зрителям всегда; здесь это можно отключить.


## 1.0.3 safety update

Effects are disabled until explicitly selected. Missing or unavailable saved choices do not select the first effect. Cookies remain unchanged. The cold-start map lifecycle fix is retained.

Plugin volumes are tracked before spawning and become master volumes only after successful setup. A failed setup removes the volume. Startup, round start, unload, and a periodic sweep in 1.0.3 remove orphaned `vipcore_postprocessing` volumes; map-authored volumes are retained. Observer target validation fails closed per recipient, so an invalid target cannot skip filtering for other clients.

`css_postprocessing_status` is server-console-only and reports active/owned/orphan counts and filter errors. The release has a distinct assembly name for safe managed loader replacement, with `VIP_Postprocessing.dll` as the canonical entry point. Unload existing instances before replacing files, check automatic loading before issuing a manual load, and verify exactly one loaded 1.0.3 module. Do not load the release DLL as a second plugin.

Validation: `dotnet build modules/VIP_Postprocessing -c Release` and `dotnet run --project tests/VIP_Postprocessing.PolicyTests -c Release`. Human in-game acceptance is still needed for the appearance of explicitly selected filters.

## 1.0.4

The transmit listener runs only while personal volumes exist, including volumes pending deletion. Map volumes are discovered at lifecycle boundaries and tracked through entity events. Removal retries use the specific entity handles. The server-console status includes `filter=on/off`.

Run the policy tests with `dotnet run --project tests/VIP_Postprocessing.PolicyTests -c Release`. Add `-- --benchmark` for a managed simulation of the snapshot filter.

## 1.0.5

При поздней загрузке VIPCore модуль повторяет регистрацию. На старте раунда он учитывает игроков, которые уже успели заспавниться. Для VIP без выбранного эффекта модуль пропускает отложенное применение. Пока эффект активен, раз в две игровые секунды модуль перепроверяет доступ к фиче и ограничения применённой записи. После возврата доступа сохранённый выбор применяется при следующем спавне, начале раунда или загрузке VIP игрока.

После `css_vip_reload` модуль сразу снимает активный эффект, если его убрали из конфига или игрок больше не проходит ограничения. Сохранённый выбор остаётся в cookie. Для этой проверки обновите модуль, VIPCore и общий `VipCoreApi.dll` из одного релиза.

Тесты запускают обработчики модуля с имитацией событий CS2. Бенчмарк измеряет перебор персональных объёмов в управляемом коде. Для оценки нагрузки на игровой поток сравнивайте профили на одной карте при одинаковом онлайне, числе активных эффектов и зрителей. Учитывайте `CheckTransmit`, создание и удаление сущностей, старт раунда и p99 времени кадра. При `active=0` и отсутствии ожидающих удаления объёмов команда `css_postprocessing_status` должна показывать `filter=off`.
