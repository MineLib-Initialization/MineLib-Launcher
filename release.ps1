<#
  MineLib 发布脚本：更新版本号 → 发布单文件 → 上传 → 写更新清单 → 部署站点 → 提交
  用法：powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.4.1 -Notes "修复 xxx"
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Notes = "",
  [string]$SiteRoot = 'D:\dsh_Workspaces\Download\mclib',
  [string]$Server = '38.95.75.199',
  [string]$Key = "$env:USERPROFILE\.ssh\mclib_deploy"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pub = Join-Path $root 'bin\Release\net8.0-windows\win-x64\publish'
$zip = Join-Path $env:TEMP 'MineLibLauncher-win-x64.zip'
$url = 'https://launcher.endercraft.top/launcher/MineLibLauncher-win-x64.zip'

Write-Host "1/6 写入版本号 $Version"
$csproj = Join-Path $root 'MineLibLauncher.csproj'
$text = Get-Content $csproj -Raw
$text = [regex]::Replace($text, '<Version>[^<]*</Version>', '<Version>' + $Version + '</Version>')
Set-Content $csproj -Value $text -NoNewline -Encoding UTF8

Write-Host '2/6 发布单文件'
Push-Location $root
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None --nologo -v q | Out-Null
Pop-Location
$exe = Join-Path $pub 'MineLibLauncher.exe'
if (-not (Test-Path $exe)) { throw 'publish 失败：找不到 exe' }

Write-Host '3/6 打包'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

Write-Host ('4/6 上传（' + [math]::Round($size / 1MB, 1) + ' MB）')
$dest = $Server + ':/www/wwwroot/mclib/launcher/MineLibLauncher-win-x64.zip'
scp -i $Key -o StrictHostKeyChecking=accept-new $zip $dest 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw ('scp 失败 (exit ' + $LASTEXITCODE + ')') }

Write-Host '5/6 写更新清单'
$today = (Get-Date).ToString('yyyy-MM-dd')
$entry = [ordered]@{ version = $Version; date = $today; size = $size; sha256 = $sha; url = $url; notes = $Notes }
$entry | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $SiteRoot 'launcher\version.json') -Encoding UTF8

$histPath = Join-Path $SiteRoot 'launcher\versions.json'
$hist = Get-Content $histPath -Raw | ConvertFrom-Json
$list = @($hist.versions | Where-Object { $_.version -ne $Version })
$newList = @([pscustomobject]$entry) + $list
@{ versions = $newList } | ConvertTo-Json -Depth 6 | Set-Content $histPath -Encoding UTF8

Write-Host '6/6 部署站点并提交'
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $SiteRoot 'deploy.ps1') | Out-Null

Push-Location $root
git add -A
git commit -q -m ('release: v' + $Version)
git push -q origin main
Pop-Location
Push-Location $SiteRoot
git add -A
git commit -q -m ('release: publish v' + $Version + ' manifest')
git push -q origin main
Pop-Location

Write-Host ''
Write-Host ('done: v' + $Version + '  sha256=' + $sha.Substring(0, 16) + '  ' + [math]::Round($size / 1MB, 1) + ' MB')