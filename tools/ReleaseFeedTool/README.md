# Vellum release feed tool

Creates the signed launcher-update assets consumed by `LauncherUpdateService`:

- `Vellum-Launcher-<version>.zip` — launcher files at the archive root, excluding `Bootstrapper/`
- `launcher-manifest.json` — signed `SignedManifestEnvelope` for the package
- `Vellum.Bootstrapper.exe` — the matching single-file bootstrapper asset

The private signing key must stay outside the repository. The current publisher key is
stored at `%USERPROFILE%\.vellum\keys\vellum-release-private.pem`; back it up and never
commit or upload it. Its public key is in `vellum-release-public.pem` and is trusted by
the launcher through `UpdateTrustedPublicKeys`.

## Key generation

```powershell
.\.dotnet\dotnet.exe run --project tools\ReleaseFeedTool\ReleaseFeedTool.csproj -c Release -- keygen `
  --private "$env:USERPROFILE\.vellum\keys\vellum-release-private.pem" `
  --public  "$env:USERPROFILE\.vellum\keys\vellum-release-public.pem" `
  --key-id  vellum-release-2026
```

Do not regenerate the key for a normal release: existing installations only trust the
public keys configured in their settings.

## Build the feed assets

Use a published application directory that contains `Vellum.exe` and
`Bootstrapper\Vellum.Bootstrapper.exe`:

```powershell
$version = '1.0.73'
$repo = 'LDProjectTeam/Vellum'
$tag = "v$version"
$publish = 'artifacts\publish\win-x64'
$out = "artifacts\github-feed\$tag"
$privateKey = "$env:USERPROFILE\.vellum\keys\vellum-release-private.pem"

.\.dotnet\dotnet.exe run --project tools\ReleaseFeedTool\ReleaseFeedTool.csproj -c Release -- build `
  --version $version `
  --publish-dir $publish `
  --output-dir $out `
  --private-key $privateKey `
  --key-id vellum-release-2026 `
  --package-url "https://github.com/$repo/releases/download/$tag/Vellum-Launcher-$version.zip" `
  --bootstrapper-url "https://github.com/$repo/releases/download/$tag/Vellum.Bootstrapper.exe"
```

Verify the envelope before uploading:

```powershell
.\.dotnet\dotnet.exe run --project tools\ReleaseFeedTool\ReleaseFeedTool.csproj -c Release -- verify `
  --manifest "$out\launcher-manifest.json" `
  --public-key "$env:USERPROFILE\.vellum\keys\vellum-release-public.pem"
```

## Publish

Upload these three files to the same GitHub release as the installers:

```powershell
gh release upload $tag `
  "$out\launcher-manifest.json" `
  "$out\Vellum-Launcher-$version.zip" `
  "$out\Vellum.Bootstrapper.exe" `
  --clobber
```

`LauncherUpdateManifestUrl` points at the stable latest-asset alias:

```text
https://github.com/LDProjectTeam/Vellum/releases/latest/download/launcher-manifest.json
```

The manifest itself references explicit tag URLs for its package and bootstrapper, so a
later `latest` release cannot make an older manifest point at the wrong package.
