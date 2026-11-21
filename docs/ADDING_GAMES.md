# Руководство: Добавление игр в Nexora Launcher

Лаунчер разработан с **универсальной архитектурой данных**. Чтобы добавить любую игру или обновить каталог, вам **НЕ нужно переписывать код лаунчера** — достаточно добавить запись в `data/games.json` или подключить внешний URL в Настройках лаунчера (Google Диск, GitHub, персональный сервер).

---

## 1. Структура записи игры в `games.json`

```json
{
  "id": "my-game-unique-id",
  "name": "Название игры",
  "description": "Краткое описание игры, сюжет, особенности.",
  "developer": "Название студии",
  "publisher": "Издатель",
  "version": "1.0.0",
  "categories": ["Action", "RPG", "Sci-Fi"],
  "tags": ["singleplayer", "open-world"],
  "coverUrl": "https://url-to-cover-image.jpg",
  "iconUrl": "https://url-to-icon.png",
  "screenshots": [],
  "releaseDate": "2024-05-10",
  "sizeBytes": 15728640000,
  "downloadSizeBytes": 12582912000,
  "executable": "bin/Game.exe",
  "launchArguments": ["-fullscreen", "-novid"],
  "installSubfolder": "MyGame",
  "downloadSources": [
    {
      "type": "torrent",
      "priority": 1,
      "magnetLink": "magnet:?xt=urn:btih:ХЕШ_ТОРРЕНТА&dn=MyGame"
    },
    {
      "type": "http",
      "priority": 2,
      "url": "https://cdn.example.com/games/mygame.zip",
      "archiveType": "zip"
    }
  ],
  "updates": [
    {
      "version": "1.1.0",
      "releaseNotes": "Список изменений в обновлении",
      "sources": [
        {
          "type": "torrent",
          "priority": 1,
          "magnetLink": "magnet:?xt=urn:btih:ХЕШ_ПАТЧА&dn=MyGamePatch"
        }
      ]
    }
  ],
  "dependencies": ["DirectX June 2010", "VC++ 2015-2022 x64"],
  "checksums": {
    "algorithm": "SHA256",
    "files": {
      "bin/Game.exe": "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"
    }
  }
}
```

> **Важно про `checksums`:** это хеши **финальных файлов игры** (например, `bin/Game.exe`), а не скачанного архива.
> Лаунчер проверяет их после распаковки. При несовпадении установка отменяется, повреждённая загрузка удаляется,
> а карточка предлагает скачать игру заново. Если проверять нечего (у источника нет хешей), в лог пишется предупреждение.
>
> `data/games.json` поставляется пустым (`"games": []`) — это шаблон для собственного каталога. Игры по умолчанию
> ищутся через подключённые источники (Rutor) и дополнительные каталоги из настроек.

---

## 2. Поддерживаемые типы источников загрузки (`downloadSources`)

| Тип (`type`) | Описание | Обязательные поля |
|---|---|---|
| `torrent` | Загрузка через встроенный BitTorrent-движок | `magnetLink` или `torrentFileUrl` |
| `http` / `https` | Прямая загрузка архива или файла по протоколу HTTP(S) с докачкой Range | `url`, `archiveType: "zip"` |
| `local` / `file` | Локальный диск, сетевая SMB-папка или флешка | `url` (относительный или абсолютный путь) |

Лаунчер автоматически пробует источники по порядку возрастания `priority` (1 — наивысший). Если торрент недоступен, автоматически будет взят следующий источник HTTP или зеркало.

---

## 3. Подключение Google Диска или своего каталога

В лаунчере во вкладке **«Настройки»** -> **«Кастомные источники каталога»** пользователь может вставить прямую ссылку на свой `games.json`.
При обновлении каталога лаунчер объединит все игры в один удобный список!
