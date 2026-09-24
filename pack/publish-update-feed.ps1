[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [switch]$Publish,
    [string]$GiteeToken = $env:GITEE_TOKEN,
    [string]$GithubToken = $env:GH_TOKEN
)

$ErrorActionPreference = 'Stop'
$owner = 'sept13-yu'
$repo = 'AgentHub'
$branch = 'update-feed'
$fileName = 'update-feed.json'
$giteeApi = "https://gitee.com/api/v5/repos/$owner/$repo"
$githubApi = "https://api.github.com/repos/$owner/$repo"

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw "无效版本 tag: $Tag" }
$version = $Matches[1]
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src/Directory.Build.props')
$propsVersion = [string]$props.Project.PropertyGroup.Version
$latestVersion = [string](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'latest.json') | ConvertFrom-Json).version
if ($version -ne $propsVersion -or $version -ne $latestVersion) {
    throw "tag $Tag、Directory.Build.props $propsVersion、latest.json $latestVersion 不一致"
}
if (-not (Test-Path -LiteralPath $AssetsDir -PathType Container)) { throw "产物目录不存在: $AssetsDir" }
$AssetsDir = (Resolve-Path -LiteralPath $AssetsDir).Path

function Get-Asset([string]$name) {
    $path = Join-Path $AssetsDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少发布产物: $name" }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -le 0) { throw "发布产物为空: $name" }
    return [ordered]@{
        fileName = $name
        size = $item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$feed = [ordered]@{
    version = $version
    releaseUrl = "https://github.com/$owner/$repo/releases/tag/$Tag"
    assets = [ordered]@{
        winX64 = Get-Asset "AgentHub-$version-win-x64.exe"
        macArm64 = Get-Asset "AgentHub-$version-mac-arm64.dmg"
        macX64 = Get-Asset "AgentHub-$version-mac-x64.dmg"
    }
}
$portableAsset = Get-Asset "AgentHub-$version-win-x64.zip"
$feedText = ($feed | ConvertTo-Json -Depth 5) + "`n"
$feedPath = Join-Path $AssetsDir $fileName
[System.IO.File]::WriteAllText($feedPath, $feedText, [System.Text.UTF8Encoding]::new($false))
Write-Host "更新清单已生成: $feedPath"
if (-not $Publish) { return }
if ([string]::IsNullOrWhiteSpace($GiteeToken)) { throw '缺少 GITEE_TOKEN' }
if ([string]::IsNullOrWhiteSpace($GithubToken)) { throw '缺少 GH_TOKEN' }

$encodedFeed = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($feedText))
$githubHeaders = @{
    Authorization = "Bearer $GithubToken"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'AgentHub-update-feed'
}
$giteeHeaders = @{ 'User-Agent' = 'AgentHub-update-feed'; Accept = 'application/json' }

function Invoke-Api([string]$method, [string]$uri, [hashtable]$headers, $body = $null) {
    $args = @{ Method = $method; Uri = $uri; Headers = $headers; ErrorAction = 'Stop'; TimeoutSec = 30 }
    if ($null -ne $body) {
        $args.ContentType = 'application/json'
        $args.Body = $body | ConvertTo-Json -Depth 5 -Compress
    }
    try {
        return [pscustomobject]@{ Status = 200; Data = (Invoke-RestMethod @args) }
    }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        if ($status -eq 404) { return [pscustomobject]@{ Status = 404; Data = $null } }
        $message = $_.Exception.Message.Replace($GiteeToken, '***').Replace($GithubToken, '***')
        throw "$method $($uri.Split('?')[0]) 失败 (HTTP $status): $message"
    }
}

function Assert-CurrentFeed($response, [string]$source) {
    if ($response.Status -eq 404) { return $null }
    # Gitee：文件不存在时经常返回 200 + 空数组 []，而不是 404
    if ($null -eq $response.Data -or @($response.Data).Count -eq 0) { return $null }
    if (-not $response.Data.sha) { return $null }
    if (-not $response.Data.content) { throw "$source 清单缺少 content" }
    $oldText = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
        ([string]$response.Data.content -replace '\s', '')))
    $old = $oldText | ConvertFrom-Json
    if (-not $old.version -or [string]$old.version -notmatch '^\d+\.\d+\.\d+$') {
        throw "$source 现有清单版本无效"
    }
    if ([version]$old.version -gt [version]$version) { throw "$source 现有版本 $($old.version) 高于 $version" }
    if ([version]$old.version -eq [version]$version -and $oldText -ne $feedText) {
        throw "$source 已发布同版本不同内容的清单；请使用新版本 tag"
    }
    return [pscustomobject]@{ Sha = [string]$response.Data.sha; Same = ($oldText -eq $feedText) }
}

# 先确认 GitHub Release 已公开且三项资产的大小与本地一致，再更新任一清单。
$release = Invoke-Api 'GET' "$githubApi/releases/tags/$Tag" $githubHeaders
if ($release.Status -ne 200 -or $release.Data.draft -or $release.Data.prerelease) {
    throw "GitHub Release $Tag 尚未正式发布"
}
foreach ($asset in @($feed.assets.Values) + @($portableAsset)) {
    $remote = @($release.Data.assets | Where-Object { $_.name -eq $asset.fileName })
    if ($remote.Count -ne 1 -or [long]$remote[0].size -ne [long]$asset.size) {
        throw "GitHub Release 资产缺失或大小不一致: $($asset.fileName)"
    }
    if ($remote[0].digest -ne "sha256:$($asset.sha256)") {
        throw "GitHub Release SHA-256 不一致: $($asset.fileName)"
    }
}

# 首次发版从 main 建立专用分支；后续只更新此分支的单个清单文件。
$giteeAuth = 'access_token=' + [uri]::EscapeDataString($GiteeToken)
$giteeBranch = Invoke-Api 'GET' "$giteeApi/branches/$branch`?$giteeAuth" $giteeHeaders
if ($giteeBranch.Status -eq 404) {
    [void](Invoke-Api 'POST' "$giteeApi/branches" $giteeHeaders @{
        access_token = $GiteeToken; refs = 'main'; branch_name = $branch
    })
}
$githubBranch = Invoke-Api 'GET' "$githubApi/git/ref/heads/$branch" $githubHeaders
if ($githubBranch.Status -eq 404) {
    $base = Invoke-Api 'GET' "$githubApi/git/ref/heads/main" $githubHeaders
    if ($base.Status -ne 200 -or -not $base.Data.object.sha) { throw 'GitHub main 分支不可读' }
    [void](Invoke-Api 'POST' "$githubApi/git/refs" $githubHeaders @{
        ref = "refs/heads/$branch"; sha = [string]$base.Data.object.sha
    })
}

$giteeCurrent = Assert-CurrentFeed (Invoke-Api 'GET' "$giteeApi/contents/$fileName`?ref=$branch&$giteeAuth" $giteeHeaders) 'Gitee'
$githubCurrent = Assert-CurrentFeed (Invoke-Api 'GET' "$githubApi/contents/$fileName`?ref=$branch" $githubHeaders) 'GitHub'
$commitMessage = "发布 AgentHub $version 更新清单"
if (-not $giteeCurrent -or -not $giteeCurrent.Same) {
    $body = @{ access_token = $GiteeToken; content = $encodedFeed; message = $commitMessage; branch = $branch }
    if ($giteeCurrent) { $body.sha = $giteeCurrent.Sha }
    $method = if ($giteeCurrent) { 'PUT' } else { 'POST' }
    [void](Invoke-Api $method "$giteeApi/contents/$fileName" $giteeHeaders $body)
    Write-Host "Gitee 更新清单已发布: $version"
}
if (-not $githubCurrent -or -not $githubCurrent.Same) {
    $body = @{ content = $encodedFeed; message = $commitMessage; branch = $branch }
    if ($githubCurrent) { $body.sha = $githubCurrent.Sha }
    [void](Invoke-Api 'PUT' "$githubApi/contents/$fileName" $githubHeaders $body)
    Write-Host "GitHub 清单镜像已发布: $version"
}

# Gitee 原始文件地址可能有短暂缓存；发布后的 API 读回用于校验写入已落库。
foreach ($source in @(
    @{ Name = 'Gitee'; Uri = "$giteeApi/contents/$fileName`?ref=$branch&$giteeAuth"; Headers = $giteeHeaders },
    @{ Name = 'GitHub'; Uri = "$githubApi/contents/$fileName`?ref=$branch"; Headers = $githubHeaders }
)) {
    $current = Assert-CurrentFeed (Invoke-Api 'GET' $source.Uri $source.Headers) $source.Name
    if (-not $current -or -not $current.Same) { throw "$($source.Name) 清单发布后读回校验失败" }
}
Write-Host 'Gitee 和 GitHub 静态更新清单发布完成'
