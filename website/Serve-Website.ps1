[CmdletBinding()]
param([int]$Port = 8080)
$ErrorActionPreference = 'Stop'
$webRoot = if (Test-Path (Join-Path $PSScriptRoot 'dist/index.html')) { Join-Path $PSScriptRoot 'dist' } else { $PSScriptRoot }
$installerRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/installer'
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$mime = @{
    '.html' = 'text/html; charset=utf-8'; '.css' = 'text/css; charset=utf-8'; '.js' = 'text/javascript; charset=utf-8'
    '.json' = 'application/json; charset=utf-8'; '.ico' = 'image/x-icon'; '.svg' = 'image/svg+xml'; '.png' = 'image/png'
    '.webmanifest' = 'application/manifest+json; charset=utf-8'; '.txt' = 'text/plain; charset=utf-8'; '.exe' = 'application/octet-stream'
}
Write-Host "Serving $webRoot at http://127.0.0.1:$Port/ (Ctrl+C to stop)"
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        try {
            $request = $context.Request
            $response = $context.Response
            if ($request.HttpMethod -notin @('GET', 'HEAD')) { $response.StatusCode = 405; continue }
            $relative = [Uri]::UnescapeDataString($request.Url.AbsolutePath.TrimStart('/'))
            if ([string]::IsNullOrWhiteSpace($relative)) { $relative = 'index.html' }
            if ($relative -match '^downloads/Vellum-Setup-\d+\.\d+\.\d+-win-x64\.exe$') {
                $target = Join-Path $installerRoot (Split-Path $relative -Leaf)
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $response.StatusCode = 404; continue }
            } else {
                $target = [IO.Path]::GetFullPath((Join-Path $webRoot $relative))
                $rootPrefix = [IO.Path]::GetFullPath($webRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { $response.StatusCode = 404; continue }
            }
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $response.StatusCode = 404; continue }
            $response.ContentType = $mime[[IO.Path]::GetExtension($target).ToLowerInvariant()]
            if (-not $response.ContentType) { $response.ContentType = 'application/octet-stream' }
            $response.Headers['X-Content-Type-Options'] = 'nosniff'
            $response.Headers['Referrer-Policy'] = 'strict-origin-when-cross-origin'
            $info = Get-Item -LiteralPath $target
            $response.ContentLength64 = $info.Length
            if ($request.HttpMethod -eq 'GET') {
                $inputStream = [IO.File]::OpenRead($target)
                try { $inputStream.CopyTo($response.OutputStream) } finally { $inputStream.Dispose() }
            }
        } catch {
            $context.Response.StatusCode = 500
            Write-Warning $_.Exception.Message
        } finally { $context.Response.Close() }
    }
} finally { $listener.Stop(); $listener.Close() }
