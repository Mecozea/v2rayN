[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$AssetCache,
    [string]$DotnetPath = 'dotnet',
    [string]$Proxy,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '7.25.8'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/nowhere-windows-x64' }
if (!$AssetCache) { $AssetCache = Join-Path $repo 'artifacts/nowhere-package-cache' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$AssetCache = [IO.Path]::GetFullPath($AssetCache)
if (Test-Path -LiteralPath $OutputDirectory) {
    if (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1) {
        throw 'Output directory must be empty. Choose a new directory to preserve existing configuration.'
    }
}
New-Item -ItemType Directory -Force $OutputDirectory, $AssetCache | Out-Null
$manifest = Get-Content (Join-Path $PSScriptRoot 'nowhere-windows-assets.json') -Raw | ConvertFrom-Json

function Get-VerifiedAsset($Entry, [string]$Name) {
    $path = Join-Path $AssetCache $Name
    $valid = (Test-Path -LiteralPath $path) -and ((Get-FileHash -LiteralPath $path).Hash -eq $Entry.sha256)
    if (!$valid) {
        $curlArgs = @('--fail', '--location', '--silent', '--show-error', '--connect-timeout', '20', '--max-time', '240', '--retry', '2')
        if ($Proxy) { $curlArgs += @('--proxy', $Proxy) }
        $curlArgs += @($Entry.url, '--output', $path)
        & curl.exe @curlArgs
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $Name" }
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $Entry.sha256) {
        throw "Trusted asset SHA-256 mismatch: $Name"
    }
    return $path
}

$stage = Join-Path $AssetCache ('stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
try {
    foreach ($entry in $manifest.cores) {
        $archive = Get-VerifiedAsset $entry ($entry.name + '.zip')
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $stage $entry.name)
    }
    foreach ($entry in $manifest.rules) {
        $null = Get-VerifiedAsset $entry $entry.name
    }

    # Include .NET itself: the tester does not need an SDK or separately installed runtime.
    & $DotnetPath publish (Join-Path $repo 'v2rayN/v2rayN/v2rayN.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }

    & $DotnetPath publish (Join-Path $repo 'v2rayN/AmazTool/AmazTool.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishTrimmed=true -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'AmazTool publish failed' }

    $bin = Join-Path $OutputDirectory 'bin'
    foreach ($name in @('xray', 'nowhere', 'sing_box', 'srss')) {
        New-Item -ItemType Directory -Force (Join-Path $bin $name) | Out-Null
    }
    $xray = Join-Path $stage 'xray'
    Copy-Item -LiteralPath (Join-Path $xray 'xray.exe'), (Join-Path $xray 'LICENSE'), (Join-Path $xray 'LICENSE-Wintun') -Destination (Join-Path $bin 'xray')
    Copy-Item -LiteralPath (Join-Path $xray 'geoip.dat'), (Join-Path $xray 'geosite.dat') -Destination $bin
    $singbox = Join-Path $stage 'sing_box/sing-box-1.14.4-windows-amd64'
    Copy-Item -LiteralPath (Join-Path $singbox 'sing-box.exe'), (Join-Path $singbox 'libcronet.dll'), (Join-Path $singbox 'LICENSE') -Destination (Join-Path $bin 'sing_box')
    # Both the executable directory and application working directory can resolve driver DLLs.
    Copy-Item -LiteralPath (Join-Path $xray 'wintun.dll'), (Join-Path $xray 'LICENSE-Wintun') -Destination (Join-Path $bin 'sing_box')
    Copy-Item -LiteralPath (Join-Path $xray 'wintun.dll') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $stage 'nowhere/nowhere.exe') -Destination (Join-Path $bin 'nowhere')
    Copy-Item -LiteralPath (Join-Path $repo 'docs/licenses/Nowhere-LICENSE') -Destination (Join-Path $bin 'nowhere/LICENSE')
    foreach ($entry in $manifest.rules) {
        Copy-Item -LiteralPath (Join-Path $AssetCache $entry.name) -Destination (Join-Path $bin 'srss')
    }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $OutputDirectory
    foreach ($document in @('nowhere-integration.md', 'nowhere-windows-quickstart.md', 'nowhere-validation-2026-10-30.md', 'nowhere-progress.md', 'nowhere-macos-acceptance.md', 'nowhere-release.md')) {
        Copy-Item -LiteralPath (Join-Path $repo "docs/$document") -Destination $OutputDirectory
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'nowhere-windows-assets.json') -Destination $OutputDirectory

    foreach ($command in @(
        @{ Path = (Join-Path $bin 'nowhere/nowhere.exe'); Argument = '--version' },
        @{ Path = (Join-Path $bin 'xray/xray.exe'); Argument = 'version' },
        @{ Path = (Join-Path $bin 'sing_box/sing-box.exe'); Argument = 'version' }
    )) {
        & $command.Path $command.Argument
        if ($LASTEXITCODE -ne 0) { throw "Bundled executable failed: $($command.Path)" }
    }
    $hashes = Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($OutputDirectory.Length + 1)
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
    }
    $hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
    Write-Host "Windows x64 package ready: $OutputDirectory"
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $cachePrefix = $AssetCache.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedStage.StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the package cache'
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
