[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SetupExe,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDir,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }
$setup = (Resolve-Path -LiteralPath $SetupExe).Path
$icon = Join-Path $repoRoot 'src\AgentHub\assets\agenthub.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw "缺少图标: $icon" }

$candidates = @()
if ($IsccPath) { $candidates += $IsccPath }
$candidates += @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$fromPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($fromPath) { $candidates += $fromPath.Source }
$iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6.3+ was not found.' }

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

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$iss = Join-Path $PSScriptRoot 'VelopackBootstrap.iss'
& $iscc "/DMyAppVersion=$Version" "/DMyVpkSetup=$setup" "/DMyOutputDir=$OutputDir" "/DMyIconFile=$icon" $iss
if ($LASTEXITCODE -ne 0) { throw 'Velopack bootstrap Inno compilation failed' }

$out = Join-Path $OutputDir "AgentHub-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $out)) { throw "没有打出安装包: $out" }
Write-Host "Installer: $out"
