# Vellum Migrator

Отдельный переносимый exe для перехода с Nexora на Vellum.

## Что делает

1. Находит старый профиль `%APPDATA%\Nexora` (или `%APPDATA%\GameVault`).
2. Находит установленную Nexora: стандартную папку, `Nexora` в корне любого диска, `Programs\Nexora`, `%ProgramFiles%` и запись в реестре.
3. Копирует недостающие файлы в `%APPDATA%\Vellum`; уже существующие файлы Vellum не перезаписываются.
4. По желанию запускает штатный деинсталлятор Nexora.
5. По желанию запускает установленный Vellum или `Vellum-Setup-*.exe`, лежащий рядом с мигратором.
6. Даёт выбрать каталог установки Vellum или заменить старую Nexora в её папке.

## Куда установится Vellum

- По умолчанию: `%LocalAppData%\Programs\Vellum` (например, `C:\Users\<имя>\AppData\Local\Programs\Vellum`).
- В окне мигратора путь можно изменить кнопкой «Обзор…».
- Галочка «Установить в папку Nexora» подставляет старый каталог (например, `D:\Nexora`) и включает удаление старой версии. Папка `Library` с играми при этом остаётся.
- Выбранный путь передаётся установщику как `/DIR="..."`; в мастере установки его всё ещё можно поменять.

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
| `--installer <path>` | явно указать `Vellum-Setup-*.exe` |
| `--install-dir <path>` | куда установить Vellum (передаётся setup как `/DIR`) |
| `--roaming <path>` | другой каталог профилей (для проверки) |
| `--install-root <path>` | другой каталог установки (для проверки) |
| `--log <path>` | файл журнала |
| `--help` | справка |

Журнал по умолчанию: `%TEMP%\Vellum-Migrator.log`.

## Безопасность

- Данные копируются, а не переносятся: при ошибке старый профиль остаётся на месте.
- Существующие файлы в `%APPDATA%\Vellum` не перезаписываются.
- Удаление старой версии выключено по умолчанию и может запросить UAC.
