[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [string]$Owner = 'sept13-yu',
    [string]$Repo = 'AgentHub',
    [string]$Token = $env:GITEE_TOKEN,
    [string]$TargetCommitish = $env:GITHUB_SHA
)

# 把 GitHub Actions 打好的 Velopack 产物挂到 Gitee Release（应用内更新源）。
# 鉴权：GITEE_TOKEN（Gitee 私人令牌，需 projects 权限）。缺 token 直接失败，避免发版只到 GitHub。
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw @'
缺少 GITEE_TOKEN。应用内更新走 Gitee Release，不能只发 GitHub。
请在 GitHub 仓库 Settings → Secrets and variables → Actions 添加 GITEE_TOKEN
（Gitee 私人令牌，勾选 projects 权限），然后重跑本 workflow。
'@
}

if (-not (Test-Path -LiteralPath $AssetsDir)) {
    throw "找不到产物目录: $AssetsDir"
}
$AssetsDir = (Resolve-Path -LiteralPath $AssetsDir).Path

$ver = $Tag -replace '^v', ''
if ([string]::IsNullOrWhiteSpace($ver)) { throw "无法从 tag 解析版本: $Tag" }
if ([string]::IsNullOrWhiteSpace($TargetCommitish)) { $TargetCommitish = $Tag }

$wanted = @(
    'releases.win.json',
    'assets.win.json',
    'RELEASES',
    "AgentHub-$ver-full.nupkg",
    "AgentHub-$ver-delta.nupkg"
)
$toUpload = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
$missingCore = [System.Collections.Generic.List[string]]::new()
foreach ($name in $wanted) {
    $path = Join-Path $AssetsDir $name
    if (Test-Path -LiteralPath $path) {
        [void]$toUpload.Add((Get-Item -LiteralPath $path))
        continue
    }
    if ($name -like '*-delta.nupkg') {
        Write-Warning "未找到 $name（首发或未拉到上一版基线），跳过。"
        continue
    }
    [void]$missingCore.Add($name)
}
if ($missingCore.Count -gt 0) {
    throw "Gitee 同步缺少文件: $($missingCore -join ', ')。这些文件应已复制到 $AssetsDir"
}
if ($toUpload.Count -eq 0) { throw "没有可上传到 Gitee 的 Velopack 文件。" }

$apiRoot = "https://gitee.com/api/v5/repos/$Owner/$Repo"
$releaseName = "AgentHub $ver"
$releaseBody = 'Velopack 增量更新产物（自 GitHub Release 同步）。应用内更新走此发行版；手动安装包仍见 GitHub。'
$userAgent = 'AgentHub-gitee-sync'

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromMinutes(20)
$http.DefaultRequestHeaders.UserAgent.ParseAdd($userAgent)
$http.DefaultRequestHeaders.Accept.ParseAdd('application/json')

function Redact([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $text }
    return $text.Replace($Token, '***', [System.StringComparison]::Ordinal)
}

function New-GiteeUri([string]$PathAndQuery) {
    $builder = [System.UriBuilder]::new("$apiRoot$PathAndQuery")
    $q = $builder.Query.TrimStart('?')
    $tok = 'access_token=' + [uri]::EscapeDataString($Token)
    $builder.Query = if ($q) { "$q&$tok" } else { $tok }
    return $builder.Uri
}

function Invoke-Gitee {
    param(
        [Parameter(Mandatory = $true)][System.Net.Http.HttpMethod]$Method,
        [Parameter(Mandatory = $true)][string]$PathAndQuery,
        [System.Net.Http.HttpContent]$Content
    )
    $req = [System.Net.Http.HttpRequestMessage]::new($Method, (New-GiteeUri $PathAndQuery))
    $resp = $null
    $status = 0
    $text = $null
    try {
        if ($Content) { $req.Content = $Content }
        $resp = $http.SendAsync($req).GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    finally {
        # 不要 Dispose Content，避免连带释放调用方传入的 Form/Multipart
        $req.Content = $null
        $req.Dispose()
        if ($resp) { $resp.Dispose() }
    }
    if ($status -in 401, 403) {
        throw "GITEE_TOKEN 无效或缺少 projects 权限 (HTTP $status)。请检查仓库 Secrets 中的 GITEE_TOKEN。"
    }
    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $json = $text | ConvertFrom-Json } catch { $json = $null }
    }
    return [pscustomobject]@{
        Status = $status
        Text   = $text
        Json   = $json
    }
}

function Get-GiteeErrorMessage($result) {
    $j = $result.Json
    if ($j -and $j.message) { return [string]$j.message }
    if ($j -and $j.error) { return [string]$j.error }
    return $result.Text
}

function Get-GiteeReleaseByTag([string]$tagName) {
    $encoded = [uri]::EscapeDataString($tagName)
    $r = Invoke-Gitee -Method ([System.Net.Http.HttpMethod]::Get) -PathAndQuery "/releases/tags/$encoded"
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.id) { return $r.Json }
    if ($r.Status -eq 404) { return $null }
    throw "查询 Gitee Release $tagName 失败 (HTTP $($r.Status)): $(Redact (Get-GiteeErrorMessage $r))"
}

function New-FormContent([hashtable]$fields) {
    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    foreach ($key in $fields.Keys) {
        $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new([string]$key, [string]$fields[$key]))
    }
    return [System.Net.Http.FormUrlEncodedContent]::new($pairs)
}

function New-GiteeRelease {
    $attempts = 4
    for ($i = 1; $i -le $attempts; $i++) {
        $form = New-FormContent @{
            tag_name         = $Tag
            name             = $releaseName
            body             = $releaseBody
            target_commitish = $TargetCommitish
            prerelease       = 'false'
        }
        try {
            $r = Invoke-Gitee -Method ([System.Net.Http.HttpMethod]::Post) -PathAndQuery '/releases' -Content $form
        }
        finally {
            $form.Dispose()
        }

        if ($r.Status -in 200, 201 -and $r.Json -and $r.Json.id) {
            Write-Host "已创建 Gitee Release $($r.Json.id) ($releaseName)"
            return $r.Json
        }

        $existing = Get-GiteeReleaseByTag $Tag
        if ($existing) {
            Write-Host "Gitee Release 已存在，复用 id=$($existing.id)"
            return $existing
        }

        $msg = Redact (Get-GiteeErrorMessage $r)
        $maybeMirrorLag = ($r.Status -in 400, 404, 422) -or ($msg -match '不存在|not found|Not Found|commit|tag')
        if ($maybeMirrorLag -and $i -lt $attempts) {
            $wait = 15 * $i
            Write-Warning "创建 Gitee Release 未成功 (HTTP $($r.Status)): $msg ；等待 ${wait}s 后重试 ($i/$attempts)，可能是镜像尚未同步 tag/commit。"
            Start-Sleep -Seconds $wait
            continue
        }
        throw "创建 Gitee Release 失败 (HTTP $($r.Status)): $msg"
    }
    throw "创建 Gitee Release 失败：超过重试次数。"
}

function Get-GiteeAttachNames([long]$releaseId) {
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $page = 1
    do {
        $r = Invoke-Gitee -Method ([System.Net.Http.HttpMethod]::Get) -PathAndQuery "/releases/$releaseId/attach_files?page=$page&per_page=100"
        if ($r.Status -ne 200) {
            throw "列出 Gitee 附件失败 (HTTP $($r.Status)): $(Redact (Get-GiteeErrorMessage $r))"
        }
        if ($null -eq $r.Json) { break }
        $items = @($r.Json)
        foreach ($item in $items) {
            if ($item -and $item.name) { [void]$names.Add([string]$item.name) }
        }
        $page++
    } while ($items.Count -ge 100)
    return $names
}

function Send-GiteeAttach([long]$releaseId, [System.IO.FileInfo]$file) {
    $attempts = 3
    for ($i = 1; $i -le $attempts; $i++) {
        $multipart = [System.Net.Http.MultipartFormDataContent]::new()
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $fileContent = [System.Net.Http.StreamContent]::new($stream)
            $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
            $multipart.Add($fileContent, 'file', $file.Name)
            $r = Invoke-Gitee -Method ([System.Net.Http.HttpMethod]::Post) -PathAndQuery "/releases/$releaseId/attach_files" -Content $multipart
        }
        finally {
            $multipart.Dispose()
            $stream.Dispose()
        }
        if ($r.Status -in 200, 201) {
            Write-Host "已上传 $($file.Name) ($([math]::Round($file.Length / 1MB, 1)) MB)"
            return
        }
        $msg = Redact (Get-GiteeErrorMessage $r)
        if ($i -lt $attempts) {
            Write-Warning "上传 $($file.Name) 失败 (HTTP $($r.Status)): $msg ；重试 $i/$attempts"
            Start-Sleep -Seconds (10 * $i)
            continue
        }
        throw "上传 $($file.Name) 失败 (HTTP $($r.Status)): $msg"
    }
}

try {
    Write-Host "同步 $Tag → https://gitee.com/$Owner/$Repo （目录 $AssetsDir）"
    $release = Get-GiteeReleaseByTag $Tag
    if ($release) {
        Write-Host "复用已有 Gitee Release id=$($release.id) name=$($release.name)"
    }
    else {
        $release = New-GiteeRelease
    }
    $releaseId = [long]$release.id
    $existing = Get-GiteeAttachNames $releaseId
    $uploaded = 0
    $skipped = 0
    foreach ($file in $toUpload) {
        if ($file.Name -like 'AgentHub-Setup-*-win-x64.exe') {
            Write-Host "跳过安装包（只发 GitHub）: $($file.Name)"
            $skipped++
            continue
        }
        if ($existing.Contains($file.Name)) {
            Write-Host "已有附件，跳过: $($file.Name)"
            $skipped++
            continue
        }
        Send-GiteeAttach $releaseId $file
        [void]$existing.Add($file.Name)
        $uploaded++
    }
    Write-Host "Gitee 同步完成: 新上传 $uploaded，跳过 $skipped。https://gitee.com/$Owner/$Repo/releases/tag/$Tag"
}
finally {
    $http.Dispose()
}
