# Vellum Migrator

Отдельный переносимый exe для перехода с Nexora на Vellum.

## Что делает

1. Находит старый профиль `%APPDATA%\Nexora` (или `%APPDATA%\GameVault`).
2. Копирует недостающие файлы в `%APPDATA%\Vellum`; уже существующие файлы Vellum не перезаписываются.
3. По желанию запускает штатный деинсталлятор Nexora.
4. По желанию запускает установленный Vellum или `Vellum-Setup-*.exe`, лежащий рядом с мигратором.

Старый профиль не удаляется — он остаётся резервной копией.

## Сборка

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\migrator\Build-Migrator.ps1
```

Результат: `artifacts\migrator\Vellum-Migrator.exe` (single-file, self-contained, win-x64).

## Запуск

Обычный запуск открывает окно с найденными путями и тремя флажками.

Тихий режим:

```powershell
.\Vellum-Migrator.exe --silent
.\Vellum-Migrator.exe --silent --uninstall-old --no-launch
.\Vellum-Migrator.exe --silent --dry-run
```

| Параметр | Значение |
|---|---|
| `--silent` | без окна, результат в журнал и код возврата |
| `--dry-run` | только показать план, ничего не менять |
| `--no-profile` | не переносить профиль |
| `--uninstall-old` | удалить старую версию Nexora |
| `--no-launch` | не запускать Vellum после переноса |
| `--roaming <path>` | другой каталог профилей (для проверки) |
| `--install-root <path>` | другой каталог установки (для проверки) |
| `--log <path>` | файл журнала |
| `--help` | справка |

Журнал по умолчанию: `%TEMP%\Vellum-Migrator.log`.

## Безопасность

- Данные копируются, а не переносятся: при ошибке старый профиль остаётся на месте.
- Существующие файлы в `%APPDATA%\Vellum` не перезаписываются.
- Удаление старой версии выключено по умолчанию и может запросить UAC.
