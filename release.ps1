<#
  MineLib release: bump version -> publish -> zip -> delta patch -> upload -> manifests -> deploy -> commit
  Usage: powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.5.0 -Notes "..." [-SkipDeploy]
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Notes = "",
  [string]$SiteRoot = 'D:\dsh_Workspaces\Download\mclib',
  [string]$Server = '38.95.75.199',
  [string]$Key = "$env:USERPROFILE\.ssh\mclib_deploy",
  [switch]$SkipDeploy
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pub = Join-Path $root 'bin\Release\net8.0-windows\win-x64\publish'
$zip = Join-Path $env:TEMP 'MineLibLauncher-win-x64.zip'
$patchFile = Join-Path $env:TEMP 'MineLibLauncher-patch.bin'
$baseDir = Join-Path $env:LOCALAPPDATA 'MineLib\release'
$baseExe = Join-Path $baseDir 'base.exe'
$url = 'https://launcher.endercraft.top/launcher/MineLibLauncher-win-x64.zip'
$patchUrl = 'https://launcher.endercraft.top/launcher/MineLibLauncher-patch.bin'

Write-Host "1/7 set version $Version"
$csproj = Join-Path $root 'MineLibLauncher.csproj'
$text = Get-Content $csproj -Raw
$text = [regex]::Replace($text, '<Version>[^<]*</Version>', '<Version>' + $Version + '</Version>')
Set-Content $csproj -Value $text -NoNewline -Encoding UTF8

Write-Host '2/7 publish single file (uncompressed bundle, so delta patches stay small)'
Push-Location $root
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None --nologo -v q | Out-Null
Pop-Location
$exe = Join-Path $pub 'MineLibLauncher.exe'
if (-not (Test-Path $exe)) { throw 'publish failed: no exe' }

Write-Host '3/7 zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$exeSha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$exeSize = (Get-Item $exe).Length
Write-Host ("    zip {0} MB  exe {1} MB" -f [math]::Round($size / 1MB, 1), [math]::Round($exeSize / 1MB, 1))

Write-Host '4/7 delta patch'
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

Write-Host '5/7 upload (4 parallel streams for the zip)'
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
  $cmd = '/c ssh -i "' + $Key + '" -o StrictHostKeyChecking=no mclib@' + $Server + ' "cat > /tmp/mlpart' + $i + '.bin" < "' + $parts[$i] + '"'
  $procs += Start-Process -FilePath $env:ComSpec -ArgumentList $cmd -NoNewWindow -PassThru
}
$procs | ForEach-Object { $_.WaitForExit() }
$remote = 'mclib@' + $Server
ssh -i $Key -o StrictHostKeyChecking=no $remote "cat /tmp/mlpart0.bin /tmp/mlpart1.bin /tmp/mlpart2.bin /tmp/mlpart3.bin > /www/wwwroot/mclib/launcher/MineLibLauncher-win-x64.zip && rm -f /tmp/mlpart*.bin"
if ($LASTEXITCODE -ne 0) { throw 'remote reassemble failed' }
$parts | ForEach-Object { Remove-Item $_ -Force -ErrorAction SilentlyContinue }

if ($patchOk) {
  scp -i $Key -o StrictHostKeyChecking=no $patchFile ($remote + ':/www/wwwroot/mclib/launcher/MineLibLauncher-patch.bin')
  if ($LASTEXITCODE -ne 0) { throw 'patch upload failed' }
}
$remoteSum = ssh -i $Key -o StrictHostKeyChecking=no $remote "sha256sum /www/wwwroot/mclib/launcher/MineLibLauncher-win-x64.zip | cut -d' ' -f1"
if ($remoteSum.Trim() -ne $sha) { throw ('remote sha mismatch: ' + $remoteSum) }
Write-Host '    remote sha ok'

Write-Host '6/7 manifests'
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
[System.IO.File]::WriteAllText((Join-Path $SiteRoot 'launcher\version.json'), ($entry | ConvertTo-Json -Depth 6), $utf8NoBom)

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
  Write-Host '7/7 deploy site + commit'
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