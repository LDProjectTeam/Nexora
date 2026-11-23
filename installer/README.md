# Vellum Windows installer

The installer is built with Inno Setup 6 and publishes Vellum as a self-contained
`win-x64` application. It installs per-user under `%LocalAppData%\Programs\Vellum`,
creates a Start menu shortcut, and optionally creates a desktop shortcut. It does
not require administrator rights and does not remove the user's games or settings
when uninstalled. The default `Library` folder is created next to the installed
launcher, not next to the downloaded setup file.

## Build

1. Install Inno Setup 6.
2. From the repository root, run:

   ```powershell
   .\installer\Build-Installer.ps1
   ```

   If `ISCC.exe` is not on `PATH` or in a standard install directory, set
   `INNO_SETUP_COMPILER` to its full path before running the script.

The generated installer is written to `artifacts\installer`.
