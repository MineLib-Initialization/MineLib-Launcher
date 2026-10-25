# 申请 Minecraft Services 应用授权（app registration approval）

目标：让微软把我们的 Azure 应用 client id 加入 Minecraft Services 的白名单，
使第三方启动器可以用用户自己的微软账号登录（`XboxLive.signin`）。

- 我们的 client id：`4332323f-3c36-4dac-9d45-03e46139e4fa`
- 目录（tenant）ID：`2771d4bd-d669-4f4c-a3a7-1a368fb89e4b`
- 应用类型：个人 Microsoft 账户受众 + 允许公共客户端流（设备码流程）
- 卡点：`POST https://api.minecraftservices.com/authentication/login_with_xbox` → HTTP 403

## 先说结论

这不是缺配置。链路前三步全通（MSA 令牌 → Xbox Live → XSTS），只有最后一步被拒，响应体是：

```json
{ "path": "/authentication/login_with_xbox",
  "errorMessage": "Invalid app registration, see https://aka.ms/AppRegInfo for more information" }
```

已知三个独立启动器项目（Minelys、HyperKraft、SH Launcher）在微软官方问答里报告了完全相同的现象；
ID@Xbox 回复「Minecraft 不归 ID 团队管，请继续找支持团队」；`aka.ms/AppRegInfo` 指向 help.minecraft.net，
而 ID@Xbox 的入驻表单要求 D-U-N-S 编号（面向企业发行），个人开发者不适用。
目前没有公开的、面向个人开发者的正式通道——所以只能用支持工单去问，并把话说清楚。

## 提交入口（按推荐顺序）

| 顺序 | 入口 | 说明 |
|---|---|---|
| 1 | https://help.minecraft.net/hc/en-us/requests/new | Minecraft 官方帮助中心工单，选 Minecraft: Java Edition → 账号/技术支持，正文用下面的英文 |
| 2 | https://learn.microsoft.com/answers/ | 在 Microsoft Q&A 提一个公开问题（注明 Entra / Minecraft Services），其他开发者就是在这拿到人工跟进的 |
| 3 | https://www.xbox.com/developers/ID | ID@Xbox 入驻表单，仅在你确实能提供企业主体时使用 |

提交时附上（不要放任何 token）：

- client id 和 tenant id
- 完整请求/响应（**把 access_token / xsts token 全部删掉**）
- 失败请求的 trace id / correlation id（响应头或响应体里，形如 `Trace ID: xxxx ... Correlation ID: yyyy`）
- 开源仓库地址与构建产物下载页

## 英文申请正文（可直接粘贴）

> Subject: Request for Minecraft Services app registration authorization — MineLib Launcher (client id 4332323f-3c36-4dac-9d45-03e46139e4fa)
>
> Hello,
>
> I am the maintainer of **MineLib Launcher**, a free, open-source, non-commercial launcher for **Minecraft: Java Edition** on Windows (C# / .NET 8 / WPF).
> Repository: https://github.com/MineLib-Initialization/MineLib-Launcher
> Public build: https://launcher.endercraft.top/launcher/MineLibLauncher-win-x64.zip
>
> I am asking for the official process to authorize **my own** Entra application registration to call Minecraft Services. I do not want to reuse another launcher's client id, spoof an application identity, or bypass authentication in any way.
>
> **Current status.** The authentication chain completes up to the last step:
>
> 1. Microsoft OAuth 2.0 device code flow (scope `XboxLive.signin offline_access`) — OK
> 2. Xbox Live user authentication (`user.auth.xboxlive.com`) — OK
> 3. XSTS authorization (`xsts.auth.xboxlive.com`, relying party `rp://api.minecraftservices.com/`) — OK
> 4. `POST https://api.minecraftservices.com/authentication/login_with_xbox` — **HTTP 403**
>
> ```json
> { "path": "/authentication/login_with_xbox",
>   "errorMessage": "Invalid app registration, see https://aka.ms/AppRegInfo for more information" }
> ```
>
> The Entra application is configured as follows: supported account types = **Personal Microsoft accounts only**; **Allow public client flows = Yes** (required for the device code flow); no client secret; no redirect URI is used. Client ID: `4332323f-3c36-4dac-9d45-03e46139e4fa`; tenant: `2771d4bd-d669-4f4c-a3a7-1a368fb89e4b`.
>
> **What the launcher does.** It manages game installations and launches them. It never hosts, mirrors or relays game files: every download is fetched by the launcher directly from Mojang's own CDN using the URLs in the official `version_manifest_v2.json`. It does not modify the game client, does not patch any license or integrity check, and does not share, proxy or resell accounts or tokens. Account credentials are entered by the user into Microsoft's own web sign-in page; the launcher only receives the resulting tokens and stores them locally (DPAPI-protected user profile).
>
> **Why this matters right now, specifically in mainland China.** On **2026-10-20** the two most widely used mainland launchers, PCL2 (release 2.99.0.0) and HMCL (PR #7017), removed the in-game Minecraft download capability for mainland China in order to comply with local requirements. Millions of players — a very large share of them students — were left with no straightforward way to obtain or manage a Java Edition installation. Because the official launcher is difficult to use there (slow CDN, account and payment friction, frequent network problems), the practical result is that players fall back to cracked launchers that bypass authentication entirely, which is worse for Microsoft, worse for server operators and worse for the players.
>
> MineLib exists as the responsible alternative: it is free, ad-free, open source, and it deliberately does **not** distribute game files or provide any way to bypass authentication. Online play requires the user's own Microsoft account; a clearly-labelled local profile mode exists only for offline/LAN play on servers that explicitly run in offline mode, and it never authenticates as anyone else. Users are told, in the application and on the download page, to buy the game through official channels.
>
> **My questions.**
>
> 1. Which team currently owns Minecraft Services client id authorization / allowlisting?
> 2. Is `aka.ms/AppRegInfo` still an active approval process, and what does it require for an individual, non-commercial open-source project?
> 3. If ID@Xbox (and its D-U-N-S requirement for business entities) is not the right channel — as ID@Xbox itself has stated to other independent launcher developers — where should a client id be submitted for review?
>
> I can provide the client id, full request/response logs with all tokens redacted, and trace/correlation ids on request. I am not asking for any exemption from authentication; I am asking to be allowed to use the official flow with my own application identity.
>
> Thank you,
> [你的名字 / 邮箱 / 仓库地址]

## 需要你自己补的三件事

1. 申请正文末尾换成你的真实姓名、联系邮箱（建议用注册 Azure 应用的那个账号的邮箱）
2. 从失败那次请求里取 `Trace ID` 和 `Correlation ID`（响应头 `x-ms-trace-id` / 响应体里的 traceId），或者重新跑一次链路脚本抓
3. 如果仓库还是 private，先改成 public 再提交——审核方会点进去看

## 期望值

说实话：三个同类项目提交后都被踢皮球，目前没有任何公开的成功案例。
所以这条线和「外置登录 / 离线 / 从官方启动器导入账号」应该并行，不要只等审批。
