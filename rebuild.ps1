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
$Exe = Join-Path $OutDir 'AgentHub.exe'

function Stop-AgentHub {
    $procs = @(Get-Process -Name AgentHub -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    Write-Host '正在退出 AgentHub…'
    $procs | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 300
        $procs = @(Get-Process -Name AgentHub -ErrorAction SilentlyContinue)
        if ((Get-Date) -gt $deadline) {
            Write-Host 'AgentHub 未能退出，exe 仍被锁。' -ForegroundColor Red
            exit 1
        }
    } while ($procs.Count -gt 0)
    Start-Sleep -Milliseconds 500
}

function Invoke-Npm {
    param([Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $NpmArgs)
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) { $npm = Get-Command npm -ErrorAction Stop }
    & $npm.Source @NpmArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Stop-AgentHub

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

if (-not (Test-Path $Exe)) {
    Write-Host "找不到 $Exe" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '正在启动 AgentHub'
Start-Process -FilePath $Exe -WorkingDirectory $OutDir
Write-Host '已启动。桌面壳吃的是本目录打出的 wwwroot\app，不是 localhost:5173。'
