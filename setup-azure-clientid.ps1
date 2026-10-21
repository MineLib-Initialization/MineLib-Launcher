<#
  MineLib 启动器 —— 一次性注册微软登录用的 Azure 应用，并把 client id 写进工程。

  为什么需要这一步：微软要求设备码登录的请求方必须是一个已注册的应用。
  这个 client id 会随启动器一起公开发布（HMCL、Prism 都这么做），它不是密钥。
  注册只需要做一次，之后所有用户直接点「用 Microsoft 账号登录」，不用配置任何东西。

  用法（在仓库根目录）：
      powershell -ExecutionPolicy Bypass -File .\setup-azure-clientid.ps1

  过程中会弹出浏览器让你登录 —— 登录由你完成，脚本不接触你的密码，
  登录态只留在 Azure CLI 自己的缓存里。
#>
param(
  [string]$DisplayName = 'MineLib Launcher'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Write-Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# 1. Azure CLI
Write-Step '1/4 检查 Azure CLI'
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
  Write-Host '没有找到 az，尝试用 winget 安装 Microsoft.AzureCLI（可能弹 UAC 提权）…'
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw '没有 winget，请手动安装 Azure CLI: https://aka.ms/installazurecliwindows'
  }
  winget install -e --id Microsoft.AzureCLI --accept-source-agreements --accept-package-agreements
  $env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path', 'User')
  if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw '装好了但当前会话还找不到 az，请关掉窗口重跑一次。'
  }
}
az version --output json

# 2. 登录（浏览器由你自己操作）
Write-Step '2/4 登录微软账号（浏览器会弹出）'
$signedIn = $false
az account show 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $signedIn = $true; Write-Host '已有登录态，直接复用。' }
if (-not $signedIn) {
  az login --allow-no-subscriptions
  if ($LASTEXITCODE -ne 0) {
    Write-Host '浏览器登录失败，改用设备码。' -ForegroundColor Yellow
    az login --use-device-code --allow-no-subscriptions
  }
}

# 3. 创建应用：仅个人 Microsoft 账户 + 允许公共客户端流（设备码必需）
Write-Step '3/4 注册应用'
$appId = az ad app list --display-name $DisplayName --query "[0].appId" -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($appId)) {
  $appId = az ad app create --display-name $DisplayName --sign-in-audience PersonalMicrosoftAccount --query appId -o tsv
  Write-Host ('已创建应用: ' + $appId)
} else {
  Write-Host ('复用已有应用: ' + $appId)
}
az ad app update --id $appId --set isFallbackPublicClient=true | Out-Null
Write-Host '已打开「允许公共客户端流」'

# 4. 写入工程
Write-Step '4/4 写入工程'
$appId = $appId.Trim()
Set-Content -Path (Join-Path $root 'clientid.txt') -Value $appId -Encoding ASCII -NoNewline
Write-Host ('clientid.txt: ' + $appId)

$accounts = Join-Path $root 'Core\Accounts.cs'
if (Test-Path $accounts) {
  $text = Get-Content $accounts -Raw
  $new = [regex]::Replace($text, 'public const string DefaultClientId = "[^"]*";', ('public const string DefaultClientId = "' + $appId + '";'))
  if ($new -ne $text) {
    Set-Content -Path $accounts -Value $new -Encoding UTF8
    Write-Host 'Core/Accounts.cs 的 DefaultClientId 已更新'
  } else {
    Write-Host '没定位到 DefaultClientId，请手动填写下面这个值：' -ForegroundColor Yellow
    Write-Host $appId
  }
}

Write-Host ''
Write-Host ('完成。client id = ' + $appId) -ForegroundColor Green
Write-Host '下一步：dotnet publish 后所有用户点按钮即可登录。'
