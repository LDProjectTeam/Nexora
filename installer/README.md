# Vellum packaging

Builds every distributable artifact from the same self-contained `win-x64` publish.

## Artifacts

| Script | Output |
|---|---|
| `Build-Installer.ps1` | `artifacts\installer\Vellum-Setup-<version>-win-x64.exe` |
| `Build-Portable.ps1` | `artifacts\portable\Vellum-Portable-<version>-win-x64.zip` |
| `Build-Msi.ps1` | `artifacts\msi\Vellum-<version>-win-x64.msi` |
| `..\migrator\Build-Migrator.ps1` | `artifacts\migrator\Vellum-Migrator.exe` |
| `Build-All.ps1` | all of the above |

## Build

```powershell
.\installer\Build-All.ps1
```

Requirements:

- .NET 8 SDK (the repository `.dotnet\dotnet.exe` is used when present)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the setup `.exe`
- [WiX Toolset 3](https://github.com/wixtoolset/wix3/releases) for the `.msi`
  (`WIX_BIN` may point to the folder with `candle.exe`, `light.exe`, `heat.exe`)

## Installer behaviour

- Installs per-user under `%LocalAppData%\Programs\Vellum`; no administrator rights.
- Creates a Start menu shortcut and an optional desktop shortcut.
- Removes only its own files on uninstall; the game library and `%AppData%\Vellum` stay.
- The game library default is created by the app on first run, not by the setup.

## Portable build

The ZIP contains a single `Vellum` folder with `Vellum.exe`, the self-contained
runtime, the catalog data, and the torrent engine. Extract it anywhere and run
`Vellum.exe`; settings still live in `%AppData%\Vellum`, so it shares the profile
with the installed build.

## MSI

The MSI is authored with WiX Toolset 3 (`Vellum.wxs`) and harvests the publish
folder with `heat.exe`. It installs per-user into
`%LocalAppData%\Programs\Vellum`, adds a Start menu shortcut, and upgrades older
MSI versions through a fixed `UpgradeCode`.
