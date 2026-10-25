<#
  MineLib release: bump version -> publish -> zip -> delta patch -> upload -> manifests -> deploy -> commit

  Usage: powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.5.0 -Notes "..."

  发布目标（服务器 / 账号 / 路径 / 部署密钥 / 站点目录）**不写在仓库里**：
    %USERPROFILE%\.minelib\deploy.json
  格式见 release.local.example.json。没有这个文件、或者没有对应的私钥，脚本会在上传前就停下，
  别人拿到这个仓库也推不到我的服务器（上传靠 SSH 私钥，私钥只在发布机上）。

  [-SkipDeploy] 只发 GitHub Release，不部署站点；[-DryRun] 只打印将要做什么，什么都不发。
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Notes = "",
  [string]$SiteRoot = "",
  [string]$Server = "",
  [string]$User = "",
  [string]$Target = "",
  [string]$Key = "",
  [string]$SigningKey = "$env:USERPROFILE\.minelib\release-key.xml",
  [switch]$SkipDeploy,
  [switch]$InitSigningKey,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── 本机发布配置（不进仓库）──────────────────────────────────────────────
$deployCfgPath = Join-Path $env:USERPROFILE '.minelib\deploy.json'
if (Test-Path $deployCfgPath) {
  try { $deployCfg = Get-Content $deployCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $deployCfg = $null }
  if ($deployCfg) {
    if (-not $SiteRoot -and $deployCfg.siteRoot) { $SiteRoot = [string]$deployCfg.siteRoot }
    if (-not $Server -and $deployCfg.server) { $Server = [string]$deployCfg.server }
    if (-not $User -and $deployCfg.user) { $User = [string]$deployCfg.user }
    if (-not $Target -and $deployCfg.target) { $Target = [string]$deployCfg.target }
    if (-not $Key -and $deployCfg.key) { $Key = [string]$deployCfg.key }
  }
}
if (-not $Key) { $Key = Join-Path $env:USERPROFILE '.ssh\mclib_deploy' }
if (-not $SiteRoot) { $SiteRoot = Join-Path (Split-Path -Parent $root) 'mclib' }
$pub = Join-Path $root 'bin\Release\net8.0-windows\win-x64\publish'
$zip = Join-Path $env:TEMP 'MineLibLauncher-win-x64.zip'
$patchFile = Join-Path $env:TEMP 'MineLibLauncher-patch.bin'
$baseDir = Join-Path $env:LOCALAPPDATA 'MineLib\release'
$baseExe = Join-Path $baseDir 'base.exe'
$url = 'https://launcher.endercraft.top/launcher/MineLibLauncher-win-x64.zip'
$patchUrl = 'https://launcher.endercraft.top/launcher/MineLibLauncher-patch.bin'

Write-Host "0/8 签名私钥：$SigningKey"

# 首次发布前初始化签名密钥（私钥留本机，公钥要写进 Core\ManifestSign.cs）
if ($InitSigningKey) {
  $devExe = Join-Path $root 'bin\Release\net8.0-windows\MineLibLauncher.exe'
  if (-not (Test-Path $devExe)) { throw '先 dotnet build -c Release，再初始化签名密钥' }
  if (Test-Path $SigningKey) { throw ('私钥已存在，不覆盖：' + $SigningKey) }
  $pubFile = Join-Path $env:TEMP 'minelib-pubkey.txt'
  if (Test-Path $pubFile) { Remove-Item $pubFile -Force }
  $mk = Start-Process -FilePath $devExe -ArgumentList @('--mkkey', $SigningKey) -PassThru -WindowStyle Hidden
  $mk.WaitForExit(120000) | Out-Null
  $pubText = if (Test-Path $pubFile) { (Get-Content $pubFile -Raw).Trim() } else { '' }
  if ([string]::IsNullOrWhiteSpace($pubText) -or $pubText.StartsWith('FAIL')) {
    throw ('生成签名密钥失败：' + $pubText)
  }
  Write-Host ('    私钥已写入：' + $SigningKey)
  Write-Host '    把下面这行公钥填进 Core\ManifestSign.cs 的 PublicKeyBase64：'
  Write-Host ('    ' + $pubText)
  exit 0
}

# ── 发布目标与密钥的硬门槛：缺任何一项都在上传前停下 ──────────────────────
if (-not $SkipDeploy) {
  if ([string]::IsNullOrWhiteSpace($Server)) {
    throw ('没有配置发布目标：' + $deployCfgPath + '（需要 server / user / target）。' +
           '别人拿到本仓库也推不到我的服务器，因为上传还要 SSH 私钥。')
  }
  if (-not (Test-Path $Key)) {
    throw ('找不到部署私钥：' + $Key + '。上传需要私钥，仓库里不含任何密钥。')
  }
  Write-Host ("发布目标：" + $User + "@" + $Server + "  → " + $Target)
} else {
  Write-Host '本次跳过站点部署（-SkipDeploy）'
}
if ($DryRun) {
  Write-Host ('[DryRun] 版本 ' + $Version + '，站点目录 ' + $SiteRoot)
  Write-Host ('[DryRun] 上传会用：' + $Key + ' → ' + $(if ($SkipDeploy) { '（不部署）' } else { $User + '@' + $Server + ':' + $Target }))
  Write-Host '[DryRun] 到此为止，什么都没有发布'
  exit 0
}

Write-Host "1/8 set version $Version"
$csproj = Join-Path $root 'MineLibLauncher.csproj'
$text = Get-Content $csproj -Raw
$text = [regex]::Replace($text, '<Version>[^<]*</Version>', '<Version>' + $Version + '</Version>')
Set-Content $csproj -Value $text -NoNewline -Encoding UTF8

Write-Host '2/8 publish single file (uncompressed bundle, so delta patches stay small)'
Push-Location $root
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None --nologo -v q | Out-Null
Pop-Location
$exe = Join-Path $pub 'MineLibLauncher.exe'
if (-not (Test-Path $exe)) { throw 'publish failed: no exe' }

Write-Host '3/8 zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$exeSha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$exeSize = (Get-Item $exe).Length
Write-Host ("    zip {0} MB  exe {1} MB" -f [math]::Round($size / 1MB, 1), [math]::Round($exeSize / 1MB, 1))

Write-Host '4/8 delta patch'
$patchOk = $false
$patchSha = ''
$patchSize = 0
if (Test-Path $baseExe) {
  $baseSha = (Get-FileHash $baseExe -Algorithm SHA256).Hash.ToLower()
  if ($baseSha -ne $exeSha) {
    if (Test-Path $patchFile) { Remove-Item $patchFile -Force }
    $log = Join-Path $env:TEMP 'minelib-patch.txt'
    if (Test-Path $log) { Remove-Item $log -Force }
    $p = Start-Process -FilePath $exe -ArgumentList @('--mkpatch', $baseExe, $exe, $patchFile) -PassThru -WindowStyle Hidden
    $p.WaitForExit(900000) | Out-Null
    if (Test-Path $patchFile) {
      $patchSha = (Get-FileHash $patchFile -Algorithm SHA256).Hash.ToLower()
      $patchSize = (Get-Item $patchFile).Length
      $patchOk = $true
      Write-Host ("    patch {0} MB (base {1})" -f [math]::Round($patchSize / 1MB, 2), $baseSha.Substring(0, 8))
      if (Test-Path $log) { Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host ('    ' + $_) } }
    }
  }
}
if (-not $patchOk) { Write-Host '    no baseline, skip patch' }

Write-Host '5/8 upload (4 parallel streams for the zip)'
$parts = @()
$len = (Get-Item $zip).Length
$bytes = [System.IO.File]::ReadAllBytes($zip)
$n = 4
$chunk = [math]::Ceiling($len / $n)
for ($i = 0; $i -lt $n; $i++) {
  $start = $i * $chunk
  $count = [math]::Min($chunk, $len - $start)
  $part = "$env:TEMP\mlpart$i.bin"
  $fs = [System.IO.File]::Create($part)
  $fs.Write($bytes, $start, $count)
  $fs.Close()
  $parts += $part
}
$procs = @()
for ($i = 0; $i -lt $n; $i++) {
  $cmd = '/c ssh -i "' + $Key + '" -o StrictHostKeyChecking=accept-new ' + $User + '@' + $Server + ' "cat > /tmp/mlpart' + $i + '.bin" < "' + $parts[$i] + '"'
  $procs += Start-Process -FilePath $env:ComSpec -ArgumentList $cmd -NoNewWindow -PassThru
}
$procs | ForEach-Object { $_.WaitForExit() }
$remote = $User + '@' + $Server
ssh -i $Key -o StrictHostKeyChecking=accept-new $remote "cat /tmp/mlpart0.bin /tmp/mlpart1.bin /tmp/mlpart2.bin /tmp/mlpart3.bin > $Target/launcher/MineLibLauncher-win-x64.zip && rm -f /tmp/mlpart*.bin"
if ($LASTEXITCODE -ne 0) { throw 'remote reassemble failed' }
$parts | ForEach-Object { Remove-Item $_ -Force -ErrorAction SilentlyContinue }

if ($patchOk) {
  scp -i $Key -o StrictHostKeyChecking=accept-new $patchFile ($remote + ':' + $Target + '/launcher/MineLibLauncher-patch.bin')
  if ($LASTEXITCODE -ne 0) { throw 'patch upload failed' }
}
$remoteSum = ssh -i $Key -o StrictHostKeyChecking=accept-new $remote "sha256sum $Target/launcher/MineLibLauncher-win-x64.zip | cut -d' ' -f1"
if ($remoteSum.Trim() -ne $sha) { throw ('remote sha mismatch: ' + $remoteSum) }
Write-Host '    remote sha ok'

Write-Host '6/8 manifests + signature'
$today = (Get-Date).ToString('yyyy-MM-dd')
$entry = [ordered]@{
  version = $Version
  date = $today
  size = $size
  sha256 = $sha
  url = $url
  notes = $Notes
  exe = [ordered]@{ size = $exeSize; sha256 = $exeSha }
}
if ($patchOk) {
  $entry['patch'] = [ordered]@{
    from = ([System.IO.File]::ReadAllText((Join-Path $SiteRoot 'launcher\version.json')) | ConvertFrom-Json).version
    url = $patchUrl
    size = $patchSize
    sha256 = $patchSha
  }
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$manifestPath = Join-Path $SiteRoot 'launcher\version.json'
[System.IO.File]::WriteAllText($manifestPath, ($entry | ConvertTo-Json -Depth 6), $utf8NoBom)

# 清单签名：启动器内置公钥验签，验不过就拒绝更新。
# 私钥只在发布机上（%USERPROFILE%\.minelib\release-key.xml），不进仓库。
if (-not (Test-Path $SigningKey)) {
  throw ('缺少签名私钥：' + $SigningKey + '。首次发布先跑 release.ps1 -InitSigningKey，并把公钥写进 Core\ManifestSign.cs')
}
$signLog = Join-Path $env:TEMP 'minelib-sign.txt'
if (Test-Path $signLog) { Remove-Item $signLog -Force }
$signer = Start-Process -FilePath $exe -ArgumentList @('--sign', $manifestPath, $SigningKey) -PassThru -WindowStyle Hidden
$signer.WaitForExit(180000) | Out-Null
$signOk = (Test-Path $signLog) -and (Select-String -Path $signLog -Pattern 'SIGN OK selfcheck=True' -Quiet)
if (-not $signOk) {
  $body = if (Test-Path $signLog) { Get-Content $signLog -Raw -Encoding UTF8 } else { '（没有输出）' }
  throw ('清单签名失败：' + $body)
}
Write-Host '    清单已签名（本地自检通过）'

$histPath = Join-Path $SiteRoot 'launcher\versions.json'
$hist = [System.IO.File]::ReadAllText($histPath) | ConvertFrom-Json
$list = @($hist.versions | Where-Object { $_.version -ne $Version })
$newList = @([pscustomobject]$entry) + $list
[System.IO.File]::WriteAllText($histPath, (@{ versions = $newList } | ConvertTo-Json -Depth 8), $utf8NoBom)

New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
Copy-Item $exe $baseExe -Force

# GitHub Release（public repo，作为备用下载源，也方便别人用包管理器拉）
try {
  $notesFile = Join-Path $env:TEMP 'minelib-relnotes.md'
  $relNotes = if ($Notes) { $Notes } else { 'MineLib 启动器 ' + $Version }
  [System.IO.File]::WriteAllText($notesFile, $relNotes, (New-Object System.Text.UTF8Encoding($false)))
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'   # gh 查不到时会往 stderr 写，别让它变成终止错误
  $existing = gh release list --repo MineLib-Initialization/MineLib-Launcher --limit 50 2>$null |
    Select-String -SimpleMatch ('v' + $Version)
  $ErrorActionPreference = $prev
  if (-not $existing) {
    gh release create ('v' + $Version) $zip --repo MineLib-Initialization/MineLib-Launcher --title ('MineLib 启动器 ' + $Version) --notes-file $notesFile --latest 2>&1 | Out-Null
    Write-Host ('    GitHub Release v' + $Version + ' ok')
  } else {
    Write-Host ('    GitHub Release v' + $Version + ' already exists')
  }
} catch {
  Write-Host ('    GitHub Release failed: ' + $_.Exception.Message)
}

if (-not $SkipDeploy) {
  Write-Host '7/8 deploy site + commit'
  powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $SiteRoot 'deploy.ps1') | Out-Null
  Push-Location $root
  git add -A
  git commit -q -m ('release: v' + $Version)
  git push -q origin main
  Pop-Location
  Push-Location $SiteRoot
  git add -A
  git commit -q -m ('site: publish v' + $Version + ' manifest')
  git push -q origin main
  Pop-Location
}

Write-Host ''
Write-Host ('done: v' + $Version + '  zip=' + [math]::Round($size / 1MB, 1) + ' MB  patch=' + [math]::Round($patchSize / 1MB, 2) + ' MB')