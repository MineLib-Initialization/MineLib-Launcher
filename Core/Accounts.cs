using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>账号模型。三种登录（微软正版 / 外置登录 / 离线）产出同一种结构。</summary>
public sealed class Account
{
    public string Type { get; set; } = "offline";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserType { get; set; } = "legacy";
    public string Xuid { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>外置登录的 Yggdrasil API 根地址；启动时注入 authlib-injector。</summary>
    public string Authlib { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public double ExpiresAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Type + ":" + (string.IsNullOrEmpty(Uuid) ? Name : Uuid);

    [System.Text.Json.Serialization.JsonIgnore]
    public string TypeLabel => Type switch
    {
        "microsoft" => "微软正版",
        "yggdrasil" => "外置登录",
        _ => "离线"
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => string.IsNullOrEmpty(Name) ? "未登录" : Name + "（" + TypeLabel + "）";
}

public static class OfflineAuth
{
    /// <summary>等价于 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + name)。</summary>
    public static string UuidFor(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static Account Create(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        if (name.Length > 16) name = name[..16];
        var uuid = UuidFor(name);
        return new Account
        {
            Type = "offline",
            Name = name,
            Uuid = uuid.Replace("-", ""),
            AccessToken = new string('0', 32),
            UserType = "legacy"
        };
    }
}

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

public static class MicrosoftAuth
{
    /// <summary>
    /// 启动器自己的 Azure 应用 client id（个人 Microsoft 账户类型、启用「允许公共客户端流」）。
    /// 这是**维护者一次性**要填的东西，不是终端用户要填的：
    /// 把它填在这里重新发布即可，所有用户点一下「用 Microsoft 账号登录」就完事。
    /// 优先级：config.json 的 AzureClientId → 环境变量 MINELIB_CLIENT_ID → exe 同目录 clientid.txt → 这里的常量。
    /// </summary>
    public const string DefaultClientId = "4332323f-3c36-4dac-9d45-03e46139e4fa";

    public static string ResolveClientId(AppConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.AzureClientId)) return cfg.AzureClientId.Trim();

        var env = Environment.GetEnvironmentVariable("MINELIB_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            var file = System.IO.Path.Combine(AppContext.BaseDirectory, "clientid.txt");
            if (System.IO.File.Exists(file))
            {
                var text = System.IO.File.ReadAllText(file).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch (Exception)
        {
        }

        if (!string.IsNullOrWhiteSpace(DefaultClientId)) return DefaultClientId;

        throw new AuthException(
            "启动器还没有接入微软登录：维护者需要在 Azure 注册一个应用，把 client id 填进 MicrosoftAuth.DefaultClientId 后重新发布（详见 README）。");
    }

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string Scope = "XboxLive.signin offline_access";

    public static async Task<JsonObject> StartDeviceCodeAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new AuthException("还没填 Azure 应用的 client id。到 Azure 门户免费注册一个应用（个人账户，启用「允许公共客户端流」），把 Application (client) ID 填到设置页。");
        var node = await Http.PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope
        }, ct).ConfigureAwait(false);
        return (JsonObject)node;
    }

    public static async Task<JsonObject> PollTokenAsync(string clientId, string deviceCode, int interval,
        Func<string, Task>? onTick = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return (JsonObject)await Http.PostFormAsync(TokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode
                }, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("authorization_pending"))
            {
                if (onTick is not null) await onTick("等待你在浏览器里完成登录…").ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("slow_down"))
            {
                interval += 5;
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            }
        }
        throw new AuthException("登录超时，请重试。");
    }

    private static AuthException Explain(HttpRequestException ex, string step)
    {
        var text = ex.Message;
        if (step == "mc" && text.Contains("403"))
            return new AuthException(
                "Xbox 拒绝了 Minecraft 登录（403）。常见原因是这个微软账号还没有 Xbox 档案：" +
                "先用同一账号打开 xbox.com 登录一次、建好档案并接受条款；另外也需要拥有 Minecraft Java 版。");
        if (step == "mc" && text.Contains("401"))
            return new AuthException("Minecraft 登录被拒绝（401），这个微软账号可能没有 Minecraft Java 版。");
        if (step == "xsts")
            return new AuthException("Xbox 安全令牌获取失败：" + text +
                                     "。儿童账号需要家长在 microsoft.com/family 里同意。");
        return new AuthException(text);
    }

    public static async Task<Account> ExchangeAsync(string msaToken, string clientId, CancellationToken ct = default)
    {
        var xbl = (JsonObject)await Http.PostJsonAsync(XblUrl, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msaToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        }, null, ct).ConfigureAwait(false);

        var xblToken = xbl["Token"]?.ToString() ?? throw new AuthException("Xbox Live 认证失败");
        JsonObject xsts;
        try
        {
            xsts = (JsonObject)await Http.PostJsonAsync(XstsUrl, new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JsonArray { xblToken }
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            }, null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Explain(ex, "xsts");
        }

        var xui = xsts["DisplayClaims"]?["xui"]?[0]
                  ?? throw new AuthException("XSTS 未返回用户信息");
        var uhs = xui["uhs"]?.ToString() ?? "";
        var xid = xui["xid"]?.ToString() ?? "";
        var xstsToken = xsts["Token"]?.ToString() ?? "";

        JsonObject login;
        try
        {
            login = (JsonObject)await Http.PostJsonAsync(McLoginUrl, new JsonObject
            {
                ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}"
            }, null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Explain(ex, "mc");
        }

        var token = login["access_token"]?.ToString() ?? throw new AuthException("Minecraft 认证失败");
        var profile = await GetProfileAsync(token, ct).ConfigureAwait(false);

        var name = profile["name"]?.ToString();
        var id = profile["id"]?.ToString();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            throw new AuthException("这个微软账号下没有 Minecraft Java 版档案（未购买或未创建名字）。");

        return new Account
        {
            Type = "microsoft",
            Name = name,
            Uuid = id,
            AccessToken = token,
            UserType = "msa",
            Xuid = xid,
            ClientId = clientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(login["expires_in"]?.GetValue<int>() ?? 86400).ToUnixTimeSeconds()
        };
    }

    private static async Task<JsonObject> GetProfileAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Add("Authorization", "Bearer " + token);
        using var resp = await new HttpClient().SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AuthException("读取账号档案失败：" + Http.Trim(text));
        return (JsonObject)(JsonNode.Parse(text) ?? new JsonObject());
    }

    public static async Task<Account> LoginAsync(string clientId, Action<JsonObject> onCode,
        Func<string, Task>? onTick, CancellationToken ct = default)
    {
        var flow = await StartDeviceCodeAsync(clientId, ct).ConfigureAwait(false);
        onCode(flow);
        var interval = flow["interval"]?.GetValue<int>() ?? 5;
        var token = await PollTokenAsync(clientId, flow["device_code"]!.ToString(), interval, onTick, ct)
            .ConfigureAwait(false);
        var account = await ExchangeAsync(token["access_token"]!.ToString(), clientId, ct).ConfigureAwait(false);
        account.RefreshToken = token["refresh_token"]?.ToString() ?? "";
        return account;
    }

    public static async Task<Account> EnsureFreshAsync(Account account, CancellationToken ct = default)
    {
        if (account.Type != "microsoft" || string.IsNullOrEmpty(account.RefreshToken)) return account;
        if (account.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 300) return account;
        try
        {
            var token = (JsonObject)await Http.PostFormAsync(TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = account.ClientId,
                ["refresh_token"] = account.RefreshToken,
                ["scope"] = Scope
            }, ct).ConfigureAwait(false);
            var fresh = await ExchangeAsync(token["access_token"]!.ToString(), account.ClientId, ct)
                .ConfigureAwait(false);
            fresh.RefreshToken = token["refresh_token"]?.ToString() ?? account.RefreshToken;
            return fresh;
        }
        catch (Exception)
        {
            return account;
        }
    }
}

public static class YggdrasilAuth
{
    private const string InjectorMeta = "https://authlib-injector.yushi.moe/artifact/latest.json";

    public static string LibDir => Path.Combine(AppConfig.Dir, "libs");

    public static async Task<Account> LoginAsync(string apiRoot, string username, string password,
        CancellationToken ct = default)
    {
        var root = apiRoot.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(root))
            throw new AuthException("外置登录需要填 API 地址，例如 https://littleskin.cn/api/yggdrasil");
        JsonNode resp;
        try
        {
            resp = await Http.PostJsonAsync(root + "/authserver/authenticate", new JsonObject
            {
                ["username"] = username,
                ["password"] = password,
                ["clientToken"] = Guid.NewGuid().ToString("N"),
                ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
                ["requestUser"] = false
            }, null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AuthException("外置登录失败：" + ex.Message);
        }

        var profile = resp["selectedProfile"];
        if (profile is null)
            throw new AuthException("该账号下没有可用角色。");

        return new Account
        {
            Type = "yggdrasil",
            Name = profile["name"]?.ToString() ?? username,
            Uuid = profile["id"]?.ToString() ?? "",
            AccessToken = resp["accessToken"]?.ToString() ?? "",
            UserType = "mojang",
            Authlib = root,
            ClientToken = resp["clientToken"]?.ToString() ?? ""
        };
    }

    /// <summary>拉 authlib-injector（作者官方 meta，按需下载到本地，不随项目分发）。</summary>
    public static async Task<string> EnsureInjectorAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(LibDir);
        var meta = await Http.GetJsonAsync(InjectorMeta, ct).ConfigureAwait(false);
        var version = meta["version"]?.ToString() ?? "";
        var url = meta["download_url"]?.ToString();
        if (string.IsNullOrEmpty(url)) throw new AuthException("authlib-injector 元数据缺少下载地址");
        var dest = Path.Combine(LibDir, $"authlib-injector-{version}.jar");
        if (File.Exists(dest)) return dest;
        if (!await Http.DownloadAsync(url, dest, null, null, ct).ConfigureAwait(false))
            throw new AuthException("authlib-injector 下载失败");
        return dest;
    }
}
