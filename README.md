<div align="center">

<img src="docs/logo.png" width="88" alt="Nexora">

# 🎮 Nexora

**Игровой лаунчер для Windows: каталог, загрузки и библиотека в одном окне**

[![Version](https://img.shields.io/badge/version-1.0.68-3ddc97?style=for-the-badge)](https://github.com/LDProjectTeam/Nexora/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?style=for-the-badge&logo=windows)](https://github.com/LDProjectTeam/Nexora/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Proprietary-darkred?style=for-the-badge)](LICENSE)
[![CI](https://github.com/LDProjectTeam/Nexora/actions/workflows/ci.yml/badge.svg)](https://github.com/LDProjectTeam/Nexora/actions/workflows/ci.yml)

<p align="center">
  <a href="docs/screenshots/home.png"><img src="docs/screenshots/home.png" alt="Главный экран Nexora" width="32%"></a>
  <a href="docs/screenshots/library.png"><img src="docs/screenshots/library.png" alt="Библиотека Nexora" width="32%"></a>
  <a href="docs/screenshots/details.png"><img src="docs/screenshots/details.png" alt="Карточка игры в Nexora" width="32%"></a>
</p>

</div>

**RU** [Русский](#русский) | **UA** [Українська](#українська) | **EN** [English](#english)

---

## Русский

**Лаунчер для Windows, который ведёт игру от поиска в каталоге до кнопки «Играть»**

> Nexora ищет игры в подключённых источниках, качает их через BitTorrent или HTTP с докачкой, проверяет файлы по SHA-256 и собирает установленное в одну библиотеку.

[**⬇️ Скачать**](https://github.com/LDProjectTeam/Nexora/releases/latest) · [**🌐 Сайт**](https://ldprojectteam.github.io/Nexora/) · [**🐛 Сообщить о баге**](https://github.com/LDProjectTeam/Nexora/issues)

### ✨ Возможности

| Функция | Описание |
| --- | --- |
| 🎮 **Каталог игр** | Поиск, подборки «В тренде / Топ загрузок / По алфавиту / Сюрприз», фильтры и обложки из Steam |
| 🔎 **Свои источники** | Встроенный каталог Rutor и подключение JSON-каталогов по ссылке |
| 🧲 **BitTorrent** | Движок rqbit на Rust: magnet-ссылки, `.torrent`, fast resume, лимиты скорости |
| ⬇️ **HTTP / HTTPS** | Докачка через `Range`, временная папка `staging`, idle-timeout |
| 🛡️ **Проверка SHA-256** | Хеши финальных файлов сверяются после распаковки; при несовпадении установка отменяется |
| 🧩 **Атомарная установка** | Обновление не повреждает рабочую копию, если загрузка оборвалась |
| 📚 **Библиотека** | Статусы установки и обновлений, запуск игры, проверка файлов, папка и удаление |
| 🔄 **Версии и обновления** | Выбор подходящего релиза игры и обновление установленной копии |
| 🎨 **Интерфейс** | Тёмная тема, цветовые акценты, языки RU / EN / UA, кэш обложек в WebP |

### 🚀 Установка

1. Открой [**Releases**](https://github.com/LDProjectTeam/Nexora/releases/latest)
2. Скачай `Nexora-Setup-1.0.68-win-x64.exe`
3. Запусти установщик и выбери папку библиотеки при первом старте

Установка идёт в профиль пользователя, права администратора не нужны. `.NET` ставить отдельно не требуется: сборка self-contained.

> Установщик не подписан цифровым сертификатом, поэтому Windows SmartScreen может показать предупреждение об издателе.

### 🔧 Сборка из исходников

Нужны .NET 8 SDK, PowerShell и [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet restore Nexora.sln
dotnet build Nexora.sln -c Release
dotnet test tests/Nexora.Tests/Nexora.Tests.csproj -c Release

# запуск собранного лаунчера
.\Run-Launcher.bat

# установщик в artifacts\installer
.\installer\Build-Installer.ps1

# статический сайт в website\dist
.\website\Build-Website.ps1
```

### 🛠 Архитектура

```
Nexora/
├── src/
│   ├── Nexora.Core/          # каталог, загрузчики, установка, проверка файлов
│   └── Nexora.App/           # WPF-интерфейс, view models, темы, локализация
├── sidecar/                  # вспомогательные сервисы
├── tools/bin/                # rqbit.exe и служебные утилиты
├── installer/                # Inno Setup 6 и сборка установщика
├── website/                  # статический сайт и скриншоты
├── docs/                     # добавление игр и оптимизация
└── tests/Nexora.Tests/       # xUnit-тесты
```

### 📚 Документация

- [Как добавить игру в каталог](docs/ADDING_GAMES.md)
- [Оптимизация кэша и обложек](docs/OPTIMIZATION.md)
- [Сборка и публикация сайта](website/README.md)

---

## Українська

**Лаунчер для Windows, який веде гру від пошуку в каталозі до кнопки «Грати»**

> Nexora шукає ігри в підключених джерелах, завантажує їх через BitTorrent або HTTP з докачуванням, перевіряє файли за SHA-256 і збирає встановлене в одну бібліотеку.

[**⬇️ Завантажити**](https://github.com/LDProjectTeam/Nexora/releases/latest) · [**🌐 Сайт**](https://ldprojectteam.github.io/Nexora/) · [**🐛 Повідомити про баг**](https://github.com/LDProjectTeam/Nexora/issues)

### ✨ Можливості

| Функція | Опис |
| --- | --- |
| 🎮 **Каталог ігор** | Пошук, добірки «У тренді / Топ завантажень / За абеткою / Сюрприз», фільтри й обкладинки зі Steam |
| 🔎 **Власні джерела** | Вбудований каталог Rutor і підключення JSON-каталогів за посиланням |
| 🧲 **BitTorrent** | Рушій rqbit на Rust: magnet-посилання, `.torrent`, fast resume, обмеження швидкості |
| ⬇️ **HTTP / HTTPS** | Докачування через `Range`, тимчасова папка `staging`, idle-timeout |
| 🛡️ **Перевірка SHA-256** | Хеші фінальних файлів звіряються після розпакування; у разі розбіжності встановлення скасовується |
| 🧩 **Атомарне встановлення** | Оновлення не пошкоджує робочу копію, якщо завантаження обірвалося |
| 📚 **Бібліотека** | Статуси встановлення й оновлень, запуск гри, перевірка файлів, папка та видалення |
| 🔄 **Версії та оновлення** | Вибір відповідного релізу гри й оновлення встановленої копії |
| 🎨 **Інтерфейс** | Темна тема, кольорові акценти, мови RU / EN / UA, кеш обкладинок у WebP |

### 🚀 Встановлення

1. Відкрий [**Releases**](https://github.com/LDProjectTeam/Nexora/releases/latest)
2. Завантаж `Nexora-Setup-1.0.68-win-x64.exe`
3. Запусти інсталятор і вибери папку бібліотеки під час першого запуску

Встановлення відбувається в профіль користувача, права адміністратора не потрібні. `.NET` встановлювати окремо не потрібно: збірка self-contained.

> Інсталятор не підписано цифровим сертифікатом, тому Windows SmartScreen може показати попередження про видавця.

### 🔧 Збирання з вихідного коду

Потрібні .NET 8 SDK, PowerShell і [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet restore Nexora.sln
dotnet build Nexora.sln -c Release
dotnet test tests/Nexora.Tests/Nexora.Tests.csproj -c Release

# запуск зібраного лаунчера
.\Run-Launcher.bat

# інсталятор у artifacts\installer
.\installer\Build-Installer.ps1

# статичний сайт у website\dist
.\website\Build-Website.ps1
```

### 📚 Документація

- [Як додати гру до каталогу](docs/ADDING_GAMES.md)
- [Оптимізація кешу й обкладинок](docs/OPTIMIZATION.md)
- [Збирання та публікація сайту](website/README.md)

---

## English

**A Windows launcher that takes a game from catalog search to the Play button**

> Nexora finds games in connected sources, downloads them over BitTorrent or HTTP with resume, verifies files by SHA-256, and keeps everything installed in one library.

[**⬇️ Download**](https://github.com/LDProjectTeam/Nexora/releases/latest) · [**🌐 Website**](https://ldprojectteam.github.io/Nexora/) · [**🐛 Report a Bug**](https://github.com/LDProjectTeam/Nexora/issues)

### ✨ Features

| Feature | Description |
| --- | --- |
| 🎮 **Game catalog** | Search, featured/top/alphabetical/surprise picks, filters, and Steam artwork |
| 🔎 **Custom sources** | Built-in Rutor catalog plus JSON catalogs loaded from a URL |
| 🧲 **BitTorrent** | The rqbit Rust engine: magnet links, `.torrent` files, fast resume, speed limits |
| ⬇️ **HTTP / HTTPS** | `Range` resume, a staging folder, and idle-timeout handling |
| 🛡️ **SHA-256 verification** | Final file hashes are checked after unpacking; a mismatch cancels the install |
| 🧩 **Atomic install** | An interrupted update never damages a working copy |
| 📚 **Library** | Install and update statuses, launch, file checks, folder access, and removal |
| 🔄 **Versions and updates** | Pick the right game release and update an installed copy |
| 🎨 **Interface** | Dark theme, accent colors, RU / EN / UA, WebP artwork cache |

### 🚀 Installation

1. Open [**Releases**](https://github.com/LDProjectTeam/Nexora/releases/latest)
2. Download `Nexora-Setup-1.0.68-win-x64.exe`
3. Run the installer and choose a library folder on first launch

The app installs into your user profile and does not need administrator rights. .NET is included in the self-contained build.

> The installer is not digitally signed, so Windows SmartScreen may show a publisher warning.

### 🔧 Building from Source

You need the .NET 8 SDK, PowerShell, and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet restore Nexora.sln
dotnet build Nexora.sln -c Release
dotnet test tests/Nexora.Tests/Nexora.Tests.csproj -c Release

# run the built launcher
.\Run-Launcher.bat

# installer in artifacts\installer
.\installer\Build-Installer.ps1

# static site in website\dist
.\website\Build-Website.ps1
```

---

## 🛠️ Tech Stack

```
UI:        .NET 8 · WPF · XAML · MVVM
Core:      C# · async I/O · SHA-256
Torrent:   rqbit (Rust) · separate sidecar process
Archives:  SharpCompress · SkiaSharp · HtmlAgilityPack
Installer: Inno Setup 6
CI:        GitHub Actions · xUnit
Website:   HTML · CSS · JavaScript
```

---

## 📜 License / Лицензия

Copyright (c) 2026 **Nexora**. All rights reserved.

This project is proprietary. Copying, modifying, or distributing the code without written permission from the author is prohibited. See [LICENSE](LICENSE) for details.

Данный проект является проприетарным. Копирование, изменение или распространение кода без письменного разрешения автора запрещено. Подробности — в [LICENSE](LICENSE).

---

<div align="center">

Сделано для игроков · Made for players

</div>
