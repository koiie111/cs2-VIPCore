# EffectsFilter: проверка интеграции без игрового сервера

`sh tests/FxFilterBridgeValidation/run.sh` — настоящий capability registry CSS:
нет DLL, нет сервиса, настройки по зрителю/категории, unload, reload.

Все семь модулей собираются как прежде, FxFilterApi.dll — общая DLL (Private=False).
Её исходники и сборка: koiie111/cs2-EffectsFilter, ветка claude/fx-filter.
Копировать её только в shared/FxFilterApi/FxFilterApi.dll на будущем тестовом сервере.

PlayerTrail/PlayerParticle/C4Effect/Pet/Outfit объединяют общий фильтр по ИЛИ с прежними
ограничениями зрителя, сохраняя прежние настройки своего эффекта. Серверный выключатель
блокирует создание и убирает имеющиеся сущности игровым таймером. RainbowModel читает
серверный флаг через дополнительный IFxServerSettings, поскольку FakeConVar не нативный cvar.

KillIcon не имеет transmit-сущности. Серверный fx_disable_kill отключает подмену сразу.
Пересылка player_death каждому зрителю реализована за fx_killicon_per_client, по умолчанию 0.
Проверки двух клиентов и css_profiler пока не выполнены: Docker-стенд сайта не запускает CS2.
Перед включением проверить отсутствие дубликатов/рассинхрона килфида, совпадение полей
headshot/assister/penetrated и совместимость с остальными хуками player_death.
Без разрешения на деплой эта ветка на игровые серверы не устанавливается.
