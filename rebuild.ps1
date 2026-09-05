# 用 UTF-8 打中文，避免 cmd 代码页乱码
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
try { chcp 65001 | Out-Null } catch { }

$Root = $PSScriptRoot
$Frontend = Join-Path $Root 'src\AgentHub\frontend'
$Csproj = Join-Path $Root 'src\AgentHub\AgentHub.csproj'
$OutDir = Join-Path $Root 'src\AgentHub\bin\Debug\net10.0-windows10.0.19041.0'

if (Get-Process -Name AgentHub -ErrorAction SilentlyContinue) {
    Write-Host 'AgentHub 还在跑。请先在托盘右键退出，否则 AgentHub.exe 被锁，编译无法覆盖。' -ForegroundColor Yellow
    Write-Host ''
}

function Invoke-Npm {
    param([Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $NpmArgs)
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) { $npm = Get-Command npm -ErrorAction Stop }
    & $npm.Source @NpmArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host '[1/2] 构建 Vue（只改前端时 dotnet build 可能整次跳过，这里强制打）'
Push-Location $Frontend
try {
    $viteCmd = Join-Path $Frontend 'node_modules\.bin\vite.cmd'
    $vite = Join-Path $Frontend 'node_modules\.bin\vite'
    if (-not (Test-Path $viteCmd) -and -not (Test-Path $vite)) {
        Write-Host '前端依赖不完整，先 npm ci'
        Invoke-Npm ci
    }
    Invoke-Npm run build
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host '[2/2] 构建 C#（Release + ReadyToRun 预编译，启动少付 JIT）'
Write-Host '输出仍在原目录：开机自启注册表指向那里，不能换。'
dotnet publish $Csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:SkipFrontendBuild=true -o $OutDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host '编译完成。托盘退出后重新打开 AgentHub。'
Write-Host '桌面壳吃的是本目录打出的 wwwroot\app，不是 localhost:5173。'
