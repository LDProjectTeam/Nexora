# Vellum update system

Vellum updates installed games from signed **file manifests** and updates the launcher
itself through a signed **package manifest** + standalone bootstrapper. There is no
binary delta layer in this version; changed files are downloaded whole. That is stated
explicitly so a per-file diff is never presented as a binary patch.

## Components

| Responsibility | Implementation |
| --- | --- |
| Manifest fetching + signature verification | `HttpUpdateManifestProvider` |
| RSA signature checks | `ManifestSignatureVerifier` |
| Local/remote comparison | `FileDiffService` |
| SHA-256 verification | `IntegrityVerifier` |
| File downloads, queueing, Range resume | `UpdateDownloadManager` |
| Persistent queue and state | `UpdateStateStore` |
| Transactional install + rollback | `UpdateInstaller` |
| Check + queue orchestration | `GameUpdateService` |
| Launcher self-update | `LauncherUpdateService` |
| Package swap while Vellum is closed | `Vellum.Bootstrapper` + `LauncherPackageInstaller` |

## Configuration

`settings.json` (in `%APPDATA%\Vellum`, or `VELLUM_DATA_DIR` when set):

```json
{
  "AutoCheckUpdates": true,
  "UpdateCheckIntervalMinutes": 30,
  "AutoUpdateGames": false,
  "UpdateLaunchBehavior": "ask",
  "UpdateChannel": "stable",
  "UpdateManifestBaseUrl": "https://updates.example.com/games",
  "MaxConcurrentDownloads": 1,
  "MaxConcurrentUpdateFiles": 3,
  "MaxDownloadSpeedBps": 0,
  "UpdateTrustedPublicKeys": [ "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----" ],
  "RequireSignedManifests": true,
  "LauncherUpdateManifestUrl": "https://updates.example.com/launcher/manifest.json",
  "AutoCheckLauncherUpdates": true,
  "AutoDownloadLauncherUpdates": true
}
```

`MaxDownloadSpeedBps = 0` is unlimited. `UpdateManifestBaseUrl` is only a convention:
when a catalog entry has no manifest URL Vellum requests
`{base}/{gameId}/{channel}/manifest.json`. A game can also declare its own
`manifestUrl` (supports `{channel}` and `{gameId}` placeholders).

Signature verification is mandatory by default. Without a trusted public key update
checks fail closed. Each key may carry `# key-id: name` in a comment line directly
before the PEM block to scope it to a manifest `keyId`.

## Game manifest

```json
{
  "schemaVersion": 1,
  "gameId": "example-game",
  "version": "1.2.0",
  "buildId": "120",
  "releaseDate": "2026-11-01T12:00:00Z",
  "channel": "stable",
  "files": [
    {
      "path": "bin/game.exe",
      "size": 1048576,
      "sha256": "64 lowercase or uppercase hex characters",
      "url": "https://updates.example.com/games/example-game/1.2.0/bin/game.exe"
    }
  ],
  "deletedFiles": [ "old/unused-file.dat" ],
  "releaseNotes": "optional"
}
```

Rules enforced by the provider:

* `schemaVersion` must be `1`.
* `gameId` must match the game being checked.
* Paths are relative, normalized, unique, and may not escape the game folder
  (`..`, rooted paths, `C:`, UNC, ADS, device names, trailing dot/space are rejected).
* `sha256` must be 64 hex characters.
* File URLs must resolve to HTTP/HTTPS (relative URLs are resolved against the
  manifest URL).
* A file count and total size cap prevents hostile manifests.

A changed `buildId` with the same version counts as an update (Steam-style builds).
A remote build older than the installed one is never offered.

### Signatures

The signature covers the exact UTF-8 bytes of the manifest. Two transports are
supported:

1. **Detached headers** (recommended for plain manifest files):

```text
X-Vellum-Signature: <base64 RSA PKCS#1 v1.5 SHA-256 signature>
X-Vellum-Key-Id: publisher-key-1
```

2. **Signed envelope**:

```json
{
  "schemaVersion": 1,
  "keyId": "publisher-key-1",
  "signature": "<base64>",
  "payload": "<base64 of the manifest JSON bytes>"
}
```

Publisher script example:

```powershell
$payload = [IO.File]::ReadAllBytes("manifest.json")
$rsa = [Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem((Get-Content "publisher-private.pem" -Raw))
$sig = [Convert]::ToBase64String($rsa.SignData(
    $payload, [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1))
# PUT manifest.json with X-Vellum-Signature: $sig
```

The private key exists only in the publisher pipeline. Vellum never receives it.

### Future binary deltas

This version intentionally downloads whole changed files. To add real delta patches
later, extend the manifest with a versioned `patches` array (for example
`{ "fromBuildId": "100", "toBuildId": "110", "url": "...", "sha256": "..." }`) and add
an `IPatchProvider` implementation that is consulted by `GameUpdateService` before
`FileDiffService` queues a changed file. `FileDiffService`, the downloader and the
installer already operate on verified staged files, so a successfully applied patch
would be treated exactly like a downloaded file. Until such a provider exists, a
per-file diff must not be called a binary delta update.

## Bootstrapper entry point

`Vellum.Bootstrapper.exe` is the mandatory user entry point. The installer, Start Menu
shortcut, desktop shortcut and the portable launcher route to it. A direct launch of
`Vellum.exe` is redirected through the bootstrapper once; a one-time IPC handshake
prevents a loop. The main process itself does not spawn the bootstrapper on a normal
start.

Startup order:

1. The bootstrapper acquires `Local\Vellum.Bootstrapper.SingleInstance`.
2. It recovers an interrupted launcher transaction (uncommitted journal).
3. It checks the signed launcher manifest and updates the bootstrapper first when a
   newer bootstrapper is offered.
4. It downloads, verifies and applies the launcher package.
5. It verifies `Vellum.exe`, the runtime files and the installed manifest hashes.
6. It starts `Vellum.exe` with the original user arguments and working directory.
7. It exits. Vellum only creates its window after the result of this sequence.

The window shows: Checking for updates..., No updates available., Checking integrity...,
All checks passed., Downloading update, Installing update, Verifying files, Starting Vellum...
and Update failed, including progress, speed, remaining time and the error. A fast run
keeps the accurate final status on screen until the window has been visible for at least
1.2 seconds; the update/integrity workflow itself is never delayed. Errors, Retry and
Start previous are shown immediately, and the window stays open for the user's choice.
The previous version can be started only when the transaction was rolled back
successfully; a failed mandatory update cannot start a damaged launcher.

If a direct `Vellum.exe` launch starts the bootstrapper but no health marker appears
within 20 seconds (or the bootstrapper exits early), Vellum shows an explicit error with
three choices: retry the bootstrapper, start Vellum directly, or close. It never starts
the launcher automatically after the timeout.

The bootstrapper forwards all unknown arguments to `Vellum.exe` and keeps its working
directory. It owns `--bootstrapper-child` (loop prevention, not authentication),
`--apply-self` (the bootstrapper replacement mode) and `--skip-self-update` (prevents
an immediate self-update loop). The actual launch confirmation is a random one-time
token written over the child process' inherited standard-input pipe; it is never placed
in the command line or an environment variable. Setting `VELLUM_BOOTSTRAPPED` or passing
`--bootstrapped` manually does not bypass the bootstrapper. `--allow-direct` exists only
in Debug builds; Release ignores it.

### Bootstrapper self-update

The launcher manifest can include a bootstrapper section:

```json
"bootstrapper": {
  "version": "1.0.72",
  "url": "https://updates.example.com/launcher/1.0.72/Vellum.Bootstrapper.exe",
  "size": 77488192,
  "sha256": "...64 hex..."
}
```

The new single-file bootstrapper is downloaded, SHA-256 checked and started with
`--apply-self <request>`. The replacement process waits for the old bootstrapper PID,
backs up the old EXE, replaces it with the new EXE, verifies the result and starts the
new bootstrapper with the original arguments plus `--skip-self-update`. The new process
writes a health marker after its window initializes; if it does not report healthy
within 15 seconds, the old EXE is restored and started. If anything fails it restores
the backup and starts the old bootstrapper. Two failed attempts for the same version
within ten minutes stop the automatic self-update loop; the current working
bootstrapper still starts Vellum.

The launcher package may not contain `Bootstrapper/` files; bootstrapper updates use
the dedicated section above.

## Server contract

Minimum endpoints:

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/{gameId}/{channel}/manifest.json` | Current build manifest (signed) |
| GET | file `url` from the manifest | Game payload |
| GET | launcher manifest URL | Launcher update manifest (signed) |
| GET | launcher `packageUrl` | Launcher zip package |

Range requests (`Range: bytes=N-`) should be supported for resumable downloads.
If a server ignores Range, Vellum restarts that file from zero safely. ETag/caching is
optional.

Recommended storage layout:

```text
updates/
  games/
    example-game/
      stable/manifest.json
      stable/1.2.0/bin/game.exe
      stable/1.2.0/data/pack.bin
    example-game/beta/manifest.json
  launcher/
    manifest.json
    1.0.72/Vellum-1.0.72.zip
    1.0.72/Vellum.Bootstrapper.exe
```

Manifests may list files from any host as long as URLs are absolute HTTP(S).

## Download behavior

* Queue with per-game jobs, global concurrency (`MaxConcurrentDownloads`).
* Per-job file concurrency (`MaxConcurrentUpdateFiles`).
* Aggregate speed limit (`MaxDownloadSpeedBps`).
* Pause/resume/cancel; queued jobs survive launcher restarts.
* Partial files are stored outside the live install in
  `{library}\.vellum-updates\{gameId}\partial\...part` (AppData fallback when the
  game is at a volume root).
* HTTP Range resumes partial files. Completed files are verified and moved to
  `.vellum-updates\{gameId}\payload`.
* Transient network errors retry with exponential backoff. No network leaves the
  update paused with the partial payload intact.
* A cross-process lock file prevents two Vellum instances updating the same game.

## Install transaction

1. Verify staged sizes and SHA-256.
2. Check free space for backups.
3. Preflight every target file for Windows sharing locks (running games).
4. Write a journal, then start applying.
5. Copy replaced/deleted files to a rollback backup.
6. Copy staged files next to the target and rename them into place.
7. Delete files removed by the new manifest.
8. Verify the entire installed tree against the manifest.
9. Persist version/build id and the installed file list.
10. Clean staging; keep the last rollback backup.

If any step fails, the journal restores the backup. If the process is killed, the
next launcher start rolls back every uncommitted journal before the game can launch.
A game is not marked updated until verification passes. User files that were not in
the previous manifest are never deleted.

## Launcher self-update

Launcher manifest:

```json
{
  "schemaVersion": 1,
  "appId": "vellum-launcher",
  "version": "1.0.72",
  "channel": "stable",
  "packageUrl": "https://updates.example.com/launcher/1.0.72/Vellum.zip",
  "packageSize": 12345678,
  "packageSha256": "...64 hex...",
  "entryPoint": "Vellum.exe",
  "files": [
    { "path": "Vellum.exe", "size": 1048576, "sha256": "...", "url": "" }
  ],
  "deletedFiles": [ "old-file.dll" ],
  "bootstrapper": {
    "version": "1.0.72",
    "url": "https://updates.example.com/launcher/1.0.72/Vellum.Bootstrapper.exe",
    "size": 77488192,
    "sha256": "...64 hex..."
  },
  "releaseNotes": "optional"
}
```

The zip is downloaded with Range resume, its SHA-256 is verified, and only entries
listed in the signed manifest are extracted (zip-slip is rejected). On a normal launch
the bootstrapper performs this update itself before `Vellum.exe` starts. When the
running launcher asks to restart, it writes a request and starts the bootstrapper; the
bootstrapper applies it and starts Vellum. The launcher never changes its own files.
On failure it restores the backup. Two consecutive failures for the same version stop
automatic retries to prevent an update loop.
User settings live in AppData and are never part of the package.

A running EXE cannot safely replace itself, which is why the swap happens in the
separate bootstrapper.

## Local state

`updates.json` (atomic write + `.bak`) stores the queue, per-game installed manifest
hashes, pending manifest URL, byte counters, errors and launcher attempt counters.
`updates/manifests/{gameId}.json` stores a fetched signed manifest so a paused update
can resume without renegotiating. `updates/journals` contains in-flight transaction
state. `updates/rollback/{gameId}` is the latest rollback backup.
`updates/launcher/installed.json` is written only after a launcher package verifies;
it lets the bootstrapper hash critical files before every launch. If the EXE version
does not match the state version (for example after an Inno/MSI install), the stale
hashes are ignored instead of blocking the freshly installed build. The Inno setup
also clears the old launcher update state during installation.
`updates/launcher/{version}/backup/journal.json` is the launcher rollback journal
(committed journals are retained for a manual rollback; uncommitted ones are recovered
before the next launch, unless they belong to a different installed version).
