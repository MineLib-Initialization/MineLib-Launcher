# MineLibLauncher

《我的世界》Java 版启动器。自己写的 WPF 界面（不用任何第三方启动器的 UI 资源），核心只是一件事：
读版本 json、装配官方文件、把游戏跑起来。

- 目标框架：.NET 8（WPF），单进程，无数据库
- 界面：标准窗口 + 原生控件，中性灰配色 + 一个钢蓝强调色；无渐变、无阴影堆叠、无图标字体
- 数据来源：Mojang 官方 CDN（version_manifest_v2 / piston-data / resources）与 mod 作者的官方接口

## 功能

| 模块 | 状态 |
|---|---|
| 版本扫描与切换 | 读 `versions/*/*.json`，解析 inheritsFrom 链，识别 Fabric / Forge / NeoForge / OptiFine / 原版 |
| 启动 | 合并继承链 → 过滤 rules → 拼 classpath → 解压老版本 natives → 替换启动参数 → 起进程并实时收日志 |
| 文件装配 | 缺客户端 jar / 依赖库 / 资源索引时，按 json 里的官方地址补齐 |
| 安装版本 | 「下载」页列出官方清单，可安装任意正式版/快照（首次会下全部 libraries 与 assets） |
| 安装加载器 | Fabric / Quilt / Forge / NeoForge / LiteLoader，装完自动打开 mods 文件夹 |
| 模组下载 | 「模组」页直连 Modrinth 检索，按加载器/游戏版本筛选，下载直接落到 mods |
| 登录 | 微软正版（设备码 OAuth）、外置登录（Yggdrasil + authlib-injector）、离线 |
| 设置 | 游戏目录、Java 路径、内存上限、版本隔离、额外 JVM 参数、Azure client id |

## 三种登录的区别

- **微软正版**：设备码流程（device code），界面上只有一个「用 Microsoft 账号登录」按钮。
  client id 由启动器自带（见下），**终端用户不需要配置任何东西**。

### 微软登录：维护者一次性配置（用户无感）

一条命令搞定（会弹浏览器让你登录，脚本不碰你的密码）：

	powershell -ExecutionPolicy Bypass -File .\setup-azure-clientid.ps1

脚本做四件事：检查/安装 Azure CLI → 让你在浏览器里登录 → 创建名为 \`MineLib Launcher\` 的应用
（sign-in audience 为 PersonalMicrosoftAccount，并打开 isFallbackPublicClient，即设备码必需的「允许公共客户端流」）
→ 把 client id 写进 \`clientid.txt\` 和 \`Core/Accounts.cs\` 的 \`DefaultClientId\`。
跑完重新 \`dotnet publish\` 即可，终端用户零配置。

手动做也可以（门户路径）：

微软要求请求方必须是一个已注册的应用，所以 client id 只能由发布者带在程序里 —— HMCL、Prism 都是这么做的，
这个 id 是公开信息，不是密钥。

1. 打开 portal.azure.com → App registrations → New registration；
2. Supported account types 选「仅个人 Microsoft 账户」（Accounts in this organizational directory only 以外的
   **Personal Microsoft accounts only**）；
3. 注册完在 Authentication 里把 **Allow public client flows** 打开（设备码流程必须）；
4. 复制 Application (client) ID，三选一填进去：

| 方式 | 操作 | 适用 |
|---|---|---|
| 写死 | 填 `Core/Accounts.cs` 的 `MicrosoftAuth.DefaultClientId` 后重新构建 | 正式发布，推荐 |
| 旁挂文件 | 在 exe 同目录放 `clientid.txt`，内容就是 id | 不重新编译先试 |
| 环境变量 | 设 `MINELIB_CLIENT_ID` | 临时调试 |

优先级：`config.json` 的 `AzureClientId` → `MINELIB_CLIENT_ID` → `clientid.txt` → `DefaultClientId`。
没配置时点登录会给出提示，而不是弹一个让用户填 id 的框。
- **外置登录**：Yggdrasil 协议（LittleSkin、Blessing Skin 等）。登录后启动时注入 authlib-injector，
  注入器从作者官方 meta 按需下载，不随本项目分发。皮肤/披风来自该站点。
- **离线**：本地起一个名字，UUID 用 Java 的 `OfflinePlayer:` 算法算（与 vanilla 一致），不向 Mojang 发任何请求。
  服务器必须开着 `online-mode=false` 才进得去。不做任何伪造令牌或改客户端的事。

## 编译与运行

	dotnet build -c Release
	dotnet run -c Release

产物：`bin/Release/net8.0-windows/MineLibLauncher.exe`（自包含需要在 csproj 里加 RuntimeIdentifier 后 publish）。

配置存 `%APPDATA%\MineLib\config.json`；账号（含 token）也在里面，注意别同步到公开位置。

## 目录

	Core/AppConfig.cs     配置读写（含账号列表）
	Core/Http.cs          带 UA 的请求、带 sha1 校验的下载
	Core/Versions.cs      版本扫描、继承合并、加载器识别
	Core/JavaFinder.cs    Java 探测（配置 → 官方 runtime → PATH → 常见安装目录）
	Core/Accounts.cs      三种登录
	Core/Installer.cs     官方源装配（client / libraries / assets / natives）
	Core/GameLauncher.cs  启动参数构建 + 进程与日志
	MainWindow.xaml(.cs) 界面

## 已知不足

- 只支持 Fabric / Forge 的「已安装版本」；从零安装 Forge/NeoForge 需要跑它们自己的 installer（未实现）。
- 资源文件首次安装约 400 MB，按当前网络可能要很久；中断后重进会跳过已下载的文件。
- 不做模组下载/更新、不做整合包导入向导、没有皮肤预览。
- 微软登录需要自备 Azure client id，这是账号体系决定的，不是可以绕过的步骤。
## 界面结构

    ┌ 工具栏：搜索 / 刷新 / 安装新版本 / 打开游戏目录
    ├ 左 272px：版本列表（选中项左侧 2px 强调色竖条）
    ├ 右上：版本名 + 副标题 + 启动/停止
    └ 标签页
        启动   双列卡片：游戏版本 / 账号 / 最近日志｜快捷入口 / 运行环境（含目录）
        账号   三种登录方式 + 已保存账号
        日志   运行时输出（只读等宽）+ 打开日志文件
        下载   官方版本清单 + 安装进度

配色只有中性灰加一个钢蓝：`#F4F4F5` 底、白面板、`#DCDCE0` 1px 分隔、`#35618E` 强调。
没有渐变、阴影堆叠、圆角胶囊和图标字体——所有控件都是 WPF 原生渲染，样式集中在 `Themes/Styles.xaml`。
## 加载器安装

| 加载器 | 做法 | 备注 |
|---|---|---|
| Fabric | 取官方 meta 的 profile/json 写进 versions/fabric-loader-<loader>-<mc>/ | 纯文本，秒装，不需要 Java |
| Quilt | 同上，数据来自 meta.quiltmc.org | 同上 |
| Forge | 下载 forge-<mc>-<ver>-installer.jar，跑 java -jar ... --installClient <gameDir> | 需要 Java，会顺带准备 launcher_profiles.json |
| NeoForge | 下载 neoforge-<ver>-installer.jar，同样走 --installClient | 版本号按 MC 映射（1.21.1 → 21.1.x） |
| LiteLoader | 只有图形界面安装器，下载后交给它自己跑 | 仅支持到 1.12.2 这类老版本 |

装完会选中新版本并打开 mods 目录（开了版本隔离就是版本目录里的 mods）。

## 模组页

左侧检索走 Modrinth 官方 API，用 project_type:mod + 加载器 + 游戏版本三个 facet 过滤，
列表显示图标、简介、许可证和下载量；右侧显示选中项目的可用版本文件，点「下载并放到 mods」写进 mods 目录。
不镜像、不改文件，拿的就是作者的 CDN 链接。

自检：MineLibLauncher.exe --selftest 会把加载器版本列表、Modrinth 检索、一次 Fabric 安装的结果
写到 %TEMP%\minelib-selftest.txt，不开界面。
## 微软正版登录：当前不可用（微软侧限制）

自己注册的 Azure 应用无法登录 Minecraft：`/authentication/login_with_xbox` 会返回

```
HTTP 403  {"path":"/authentication/login_with_xbox",
           "errorMessage":"Invalid app registration, see https://aka.ms/AppRegInfo for more information"}
```

即使应用配置完全正确（个人账户受众 + 允许公共客户端流 + 设备码流程）也一样。
微软现在只给获批的应用开这个口子，HMCL / Prism / PCL 用的是它们多年前获批的应用身份。
整条链路的前三步（MSA 令牌 → Xbox Live → XSTS）在本项目里是通的，卡在最后一步。

因此本启动器实际可用的是：**外置登录（authlib-injector + Yggdrasil）** 与 **离线**。
