[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$ZipOnly,
    [switch]$PublishOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectPath = Join-Path $repoRoot 'src\AgentHub\AgentHub.csproj'
$frontendPath = Join-Path $repoRoot 'src\AgentHub\frontend'
$distPath = Join-Path $repoRoot 'dist'
$publishPath = Join-Path $distPath 'win-x64'

[xml]$project = Get-Content -Raw -LiteralPath $projectPath
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'AgentHub.csproj is missing Version' }

if (Test-Path -LiteralPath $publishPath) {
    $resolvedDist = [System.IO.Path]::GetFullPath($distPath).TrimEnd('\') + '\'
    $resolvedPublish = [System.IO.Path]::GetFullPath($publishPath)
    if (-not $resolvedPublish.StartsWith($resolvedDist, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside dist: $resolvedPublish"
    }
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

Push-Location $frontendPath
try {
    if (-not (Test-Path -LiteralPath (Join-Path $frontendPath 'node_modules'))) {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
}
finally { Pop-Location }

dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:SkipFrontendBuild=true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if ($PublishOnly) {
    Write-Host "Published: $publishPath"
    return
}

$zipPath = Join-Path $distPath "AgentHub-$version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Portable package: $zipPath"

if ($ZipOnly) { return }

$candidates = @()
if ($IsccPath) { $candidates += $IsccPath }
# 真编译器优先：Chocolatey 的 PATH 垫片 PE 版本常是 0.0.0.0，且旁边没有 unins000。
$candidates += @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$fromPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($fromPath) { $candidates += $fromPath.Source }
$iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6.3+ was not found. Use -ZipOnly for a portable package only.' }

function Test-IsccAtLeast63([string]$compiler) {
    $blobs = @((Get-Item -LiteralPath $compiler).VersionInfo.ProductVersion)
    $unins = Join-Path (Split-Path -Parent $compiler) 'unins000.exe'
    if (Test-Path -LiteralPath $unins) {
        $blobs += (Get-Item -LiteralPath $unins).VersionInfo.ProductVersion
    }
    $blobs += (& $compiler '/?' 2>&1 | Out-String)
    foreach ($raw in $blobs) {
        $text = [string]$raw
        $dotted = [regex]::Match($text, '\d+(?:\.\d+){1,3}')
        if ($dotted.Success -and [version]$dotted.Value -ge [version]'6.3') { return $true }
        if ($text -match 'Inno Setup 6') { return $true }
    }
    return $false
}

if (-not (Test-IsccAtLeast63 $iscc)) { throw "Inno Setup 6.3+ is required. ISCC: $iscc" }

$iss = Join-Path $PSScriptRoot 'AgentHub.iss'
& $iscc "/DMyAppVersion=$version" "/DMySourceDir=$publishPath" "/DMyOutputDir=$distPath" $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
Write-Host "Installer: $(Join-Path $distPath "AgentHub-Setup-$version-win-x64.exe")"
