[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [string]$Owner = 'sept13-yu',
    [string]$Repo = 'AgentHub',
    [string]$Token = $env:GITEE_TOKEN,
    [string]$TargetCommitish = $env:GITHUB_SHA
)

# 本机把 Velopack 产物挂到 Gitee Release（应用内更新源）。
# GitHub Actions 不再调用本脚本；GitHub Release 成功后再在本地执行。Setup exe 不上传。
# 鉴权：环境变量 GITEE_TOKEN（Gitee 私人令牌，需 projects 权限）。
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw @'
缺少 GITEE_TOKEN。请在本机设置环境变量 GITEE_TOKEN
（Gitee 私人令牌，勾选 projects 权限）后再运行本脚本。
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
    $maxAttempts = 4
    $resp = $null
    $status = 0
    $text = $null
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $req = [System.Net.Http.HttpRequestMessage]::new($Method, (New-GiteeUri $PathAndQuery))
        $resp = $null
        try {
            if ($Content) { $req.Content = $Content }
            $resp = $http.SendAsync($req).GetAwaiter().GetResult()
            $status = [int]$resp.StatusCode
            $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            break
        }
        catch {
            $msg = [string]$_.Exception.Message
            $transient = $msg -match 'SSL|TLS|timeout|timed out|connection|temporarily|NameResolution|Socket'
            if (-not $transient -or $attempt -ge $maxAttempts) { throw }
            $wait = 2 * $attempt
            Write-Warning "Gitee 请求瞬时失败 ($msg)；${wait}s 后重试 ($attempt/$maxAttempts)"
            Start-Sleep -Seconds $wait
        }
        finally {
            # 不要 Dispose Content，避免连带释放调用方传入的 Form/Multipart
            $req.Content = $null
            $req.Dispose()
            if ($resp) { $resp.Dispose(); $resp = $null }
        }
    }
    if ($status -in 401, 403) {
        throw "GITEE_TOKEN 无效或缺少 projects 权限 (HTTP $status)。请检查本机环境变量 GITEE_TOKEN。"
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
    if ($null -eq $result.Text) { return '' }
    return [string]$result.Text
}

function Get-GiteeReleaseByTag([string]$tagName) {
    $encoded = [uri]::EscapeDataString($tagName)
    $r = Invoke-Gitee -Method ([System.Net.Http.HttpMethod]::Get) -PathAndQuery "/releases/tags/$encoded"
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.id) { return $r.Json }
    # Gitee 对不存在的 tag 可能回 404，也可能 200 + 空 body / 无 id；都按未找到处理，交给创建逻辑。
    if ($r.Status -eq 404) { return $null }
    if ($r.Status -eq 200 -and (-not $r.Json -or -not $r.Json.id)) { return $null }
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

        $existingRelease = Get-GiteeReleaseByTag $Tag
        if ($existingRelease) {
            Write-Host "Gitee Release 已存在，复用 id=$($existingRelease.id)"
            return $existingRelease
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

# 不把 HashSet/List 当返回值：PowerShell 会解包集合，空集合变成 $null，有元素变成 Object[]，
# 后面 Contains/Add 都会坏。改为写入调用方传入的集合。
function Add-GiteeAttachNames {
    param(
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        # 空 HashSet 在 PowerShell 里算 “empty collection”，不加 AllowEmptyCollection 会拒绑。
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$Names
    )
    $page = 1
    do {
        $r = Invoke-Gitee -Method ([System.Net.Http.HttpMethod]::Get) -PathAndQuery "/releases/$ReleaseId/attach_files?page=$page&per_page=100"
        if ($r.Status -ne 200) {
            throw "列出 Gitee 附件失败 (HTTP $($r.Status)): $(Redact (Get-GiteeErrorMessage $r))"
        }
        # [] / null / 单对象 都收成数组；空页结束翻页。
        $items = @()
        if ($null -ne $r.Json) { $items = @($r.Json) }
        foreach ($item in $items) {
            if ($item -and $item.name) { [void]$Names.Add([string]$item.name) }
        }
        $page++
    } while ($items.Count -ge 100)
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
    if (-not $release -or -not $release.id) {
        throw "未能获得有效的 Gitee Release（缺少 id）。"
    }
    $releaseId = [long]$release.id
    $existing = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Add-GiteeAttachNames -ReleaseId $releaseId -Names $existing
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
