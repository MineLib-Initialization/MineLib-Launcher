# MineLib 启动器

Windows 上的 Minecraft Java 版启动器，C# / .NET 8 / WPF，界面自绘（不使用任何第三方启动器的 UI 资源）。
程序自身不托管、不镜像任何游戏文件：所有下载都指向 Mojang 官方地址或你在设置里选的镜像。

当前版本：**0.11.0**

## 功能

- **客户端管理**：一个客户端文件夹（游戏根目录）下并排放多个客户端，共用 libraries 与 assets；也可以登记多个客户端文件夹随时切换。
- **版本与加载器**：读 Mojang 官方清单的全部版本（正式版/快照可筛可选）；支持 Fabric、Quilt、Forge、NeoForge，默认选稳定版。
- **模组**：调用 Modrinth 官方 API 检索、按加载器与游戏版本筛选、下载到 mods。
- **账号**：离线、外置登录（Yggdrasil + authlib-injector）、微软正版（链路已实现，见"已知限制"）。
- **Java**：自动查找本机 Java；找不到时按游戏版本从 Eclipse Adoptium 取一份 JRE 到程序目录的 `MineLib\java`。
- **下载加速**：连接池 64、默认并发 32（可调 4/8/16/32/64）、单个大文件分 16 段并发、支持 HTTP/2、jar 与依赖库并行下载。
- **下载源**：设置里可切换「Mojang 官方」/「BMCLAPI 镜像」，自带一键测速。
- **自动更新**：启动时比对官网清单，优先下载增量补丁（通常几百 KB），下次启动替换并弹窗列出改动；任何一步校验失败自动回落整包。
- **日志**：游戏输出实时进界面（批量刷新）并落盘到 `<游戏目录>\logs\minelib-<时间>.log`。
- **崩溃兜底**：未处理异常写进 `<配置目录>\logs\crash-*.log`，界面不会因为一个异常直接退出。

## 运行要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 x64 |
| 运行时 | 单文件自包含，不需要另装 .NET |
| Java | 1.17+ 需 Java 17/21，1.12.2 及更早需 Java 8（可用「下载 Java」自动获取） |
| 磁盘 | 单个客户端通常在 1 GB 量级 |

## 目录结构（便携）

```
<启动器目录>\
├─ MineLibLauncher.exe
├─ MineLibLauncher\          配置、安装器缓存、自带 Java、崩溃日志
│   ├─ config.json
│   ├─ config.json.bak
│   ├─ java\
│   ├─ installers\
│   └─ logs\crash-*.log
└─ .minecraft\               默认客户端文件夹
    ├─ versions\             每个客户端一个版本目录
    ├─ libraries\  assets\   所有客户端共用
    └─ mods\  saves\
```

启动器目录不可写时（例如放在 Program Files）自动回退到 `%APPDATA%\MineLib` 与 `%APPDATA%\.minecraft`，设置页会显示当前实际位置。

## 界面

顶部四个全局页签：

- **客户端**：左侧客户端文件夹下拉 + 客户端列表，右侧是所选版本、账号、最近日志；「启动游戏」在右上角。
- **下载**：官方版本列表 +「下载并安装」；子页签「模组」检索 Modrinth。
- **账号**：三种登录方式与已保存账号。
- **设置**：启动器标题、下载源与测速、运行环境（内存 / Java / JVM 参数 / 版本隔离 / 并发）、文件位置、迁移到启动器目录。

旧版本（"一个客户端一个独立 .minecraft 目录"）的数据会在启动时自动合并进当前客户端文件夹，只做一次。

### 提示文案约定

界面上给用户看的提示只讲两件事：**发生了什么**、**怎么重试**，统一写成
`在<动作>时出现错误，<重试方式>`（例如「在下载游戏文件时出现错误，再次点击下载以重试」）。

原因、URL、文件名、异常原文一律写进日志（客户端页的「日志」、`logs/` 与崩溃日志），不摆到界面上。
代码里统一走 `Core/Notice.cs`：

```csharp
AppendLog("下载依赖库失败：" + ex.Message);                                  // 细节进日志
SetStatus(Notice.Error("下载游戏文件", "再次点击下载以重试"), false);        // 界面只说这两件事
```

界面上**不弹系统对话框**：普通提示走右下角提示条（几秒自动消失），需要用户确认的走界面内卡片
（背景压暗、Esc 取消、破坏性操作是实心红按钮）。启动阶段还没窗口时用自绘的小窗口兜底，
同样不用系统 MessageBox。

## 配置文件

`config.json` 主要字段：

| 字段 | 说明 |
|---|---|
| `GameDir` | 当前客户端文件夹 |
| `Roots` | 所有客户端文件夹及其中的客户端列表 |
| `WindowTitle` | 启动器标题 |
| `DownloadSourceId` | `official` 或 `bmclapi` |
| `MemoryMb` / `JvmArgs` / `Isolation` | 运行环境 |
| `DownloadThreads` | 下载并发 |
| `JavaPath` | 手动指定的 javaw.exe |
| `Version` | 上次选中的客户端 |
| `Accounts` | 账号（外置登录会保存 token） |

写入是「先写临时文件再替换」，覆盖前保留一份 `config.json.bak`；主文件损坏时自动回退备份。

## 命令行（调试与自检）

全部写到 `%TEMP%\minelib-*.txt`，不开界面、不写用户配置：

| 参数 | 验证内容 |
|---|---|
| `--selftest-migrate` | 旧多目录数据合并；版本改名连带更新 jar 与 inheritsFrom |
| `--selftest-update` | 更新替换脚本：成功替换 / 目标被占用时保留暂存 |
| `--selftest-http` | 下载去重、取消后不留 .part、sha1 校验 |
| `--selftest-http64` | 高并发下载压测：500 个资源对象 × 64 并发跑两轮 + 两批任务并发抢同一批文件，断言文件齐全、哈希正确、无残留 .part |
| `--selftest-assets` | 资源对象缺失检测、1.6 及更早的 virtual 资源铺开 |
| `--selftest-loader` | 安装器结果识别、Fabric 真实安装 |
| `--selftest-args` | 启动参数占位符替换、超长 classpath 走 @argfile |
| `--selftest-client` | 客户端改名、依赖检测 |
| `--selftest-auth` | 离线 UUID 与 Java 一致、注入器就位、错误提示 |
| `--selftest-javafind` | Java 探测与缓存、Adoptium 版本映射 |
| `--selftest-modrinth` | 检索、文件列表、下载落盘与文件名消毒 |
| `--selftest-log` | 日志批量刷盘的吞吐与完整性 |
| `--selftest-crash` | 全局异常兜底与崩溃日志 |

其它：`--selftest`（加载器/模组接口）、`--selftest-download`（下载测速）、`--selftest-java <主版本>`（自动获取 Java）、`--selftest-create`（装一个客户端到临时目录）、`--selftest-source`（下载源逐项验证）、`--mkpatch <旧exe> <新exe> <补丁>`（生成增量补丁）、`--shot <页签> <查询> <输出.png>`（离屏渲染界面图）。

## 从源码构建

需要 .NET 8 SDK：

```powershell
dotnet build -c Release

# 单文件自包含（发布用）
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false -p:DebugType=None
```

> 单文件包特意**不启用内压缩**：启用后任何一处代码改动都会让整包的字节布局变化，
> 增量补丁就退化回整包下载。代价是 exe 体积变大（约 138 MB），但 zip 与压缩版基本一致。

发布流程见 `release.ps1`：改版本号 → publish → 打包 → 生成增量补丁（CDC + Brotli）→ 分片并发上传 → 写更新清单 → 发 GitHub Release → 部署站点 → 提交。

## 已知限制

- **微软正版登录**：Minecraft 服务只接受微软批准的应用身份。自建 Azure 应用调用 `login_with_xbox` 会返回 403 `Invalid app registration`。链路代码完整，取得授权前该入口不可用。
- **外置登录令牌刷新**未实现：token 过期后需要重新输入密码。
- **删除客户端**只删该版本目录；若有别的客户端 `inheritsFrom` 指向它，删除后那些客户端会因缺少父版本无法启动（删除前会列出依赖者并警告）。
- **Forge / NeoForge** 依赖各自官方安装器，安装器行为不受本程序控制；安装器超过 30 分钟未结束会被中止。
- **32 位系统**未适配：Java 自动获取只提供 x64 / aarch64 包。
- **数据来源**：版本清单与文件来自 Mojang；模组来自 Modrinth；镜像可选 BMCLAPI。程序不做任何破解、不绕过正版验证、不提供加速器。

## 项目结构

```
Core/
  AppConfig.cs       配置（客户端文件夹 / 账号 / 设置）+ 载入、保存、归一化
  Paths.cs           便携路径（启动器目录，回退 AppData）
  Workspace.cs       旧数据迁移（多目录合并、搬家）
  Versions.cs        版本扫描、继承链合并、版本号归一化
  Installer.cs       官方文件补齐、资源与库下载、natives 解压、virtual 资源
  Loaders.cs         加载器版本列表与安装（Fabric / Quilt / Forge / NeoForge）
  ClientCreator.cs   客户端创建、改名、依赖检测
  GameLauncher.cs    启动参数装配、进程与日志管道
  GameLog.cs         日志批量写入
  Http.cs            下载（并发、分段、重试、去重）
  DownloadSources.cs 官方 / BMCLAPI 下载源与测速
  Modrinth.cs        模组检索、下载、图标缓存
  Accounts.cs        离线 / 外置 / 微软登录
  JavaFinder.cs      Java 查找与主版本探测（带缓存）
  JavaProvisioner.cs 从 Adoptium 获取 JRE
  Updates.cs         自动更新与增量补丁应用
  Patcher.cs         CDC 分块 + Brotli 补丁
  CrashReporter.cs   崩溃日志
MainWindow.xaml(.cs) 界面与交互
Themes/Styles.xaml   自绘控件样式
```

## 许可与声明

Minecraft 是 Mojang Studios 与 Microsoft 的商标及版权作品，本项目与其无隶属关系。
程序不包含、不分发任何游戏文件；所有游戏内容都由启动器按官方清单中的地址下载。
