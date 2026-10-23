using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>旧版模型：一个客户端 = 一个独立游戏目录。迁移用，迁移完清空。</summary>
public sealed class InstanceRef
{
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Note { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Note) ? Name : Name + "  ·  " + Note;
}

/// <summary>
/// 新版模型：客户端就是游戏根目录 versions/ 下的一个版本。
/// 只有一个游戏根目录，所有客户端并排放在它的 versions 里，libraries/assets 全局共享。
/// </summary>
public sealed class ClientEntry
{
    /// <summary>versions/ 下的目录名，也是启动时用的版本 id。</summary>
    public string VersionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "原版";
    public string CreatedAt { get; set; } = "";

    public string Display => string.IsNullOrEmpty(McVersion)
        ? Name
        : Name + "  ·  " + McVersion + (Loader == "原版" ? "" : " · " + Loader);
}

/// <summary>启动器配置，存 %APPDATA%\MineLib\config.json。</summary>
public sealed class AppConfig
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MineLib");

    public static string FilePath => Path.Combine(Dir, "config.json");

    public string GameDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>启动器标题（窗口标题与标题栏显示的文字）。</summary>
    public string WindowTitle { get; set; } = "MineLib 启动器";
    /// <summary>下载源 id：official / bmclapi。</summary>
    public string DownloadSourceId { get; set; } = "official";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    /// <summary>下载并发数。资源文件几千个小文件，串行会慢到不可用。</summary>
    public int DownloadThreads { get; set; } = 32;
    public string JvmArgs { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Isolation { get; set; } = true;
    public string AzureClientId { get; set; } = "";
    public string LastAccountKey { get; set; } = "";
    /// <summary>客户端列表：每项对应 versions/ 下的一个版本，所有客户端共用一个游戏根目录。</summary>
    public List<ClientEntry> Clients { get; set; } = new();
    /// <summary>是否已经把旧的「一个客户端一个目录」合并成单根目录。</summary>
    public bool SingleRootMigrated { get; set; }
    /// <summary>旧版模型遗留：旧客户端目录登记表，迁移后清空。</summary>
    public List<InstanceRef> Instances { get; set; } = new();
    public string CurrentInstance { get; set; } = "";
    public List<Account> Accounts { get; set; } = new();

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOpts);
                if (cfg != null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.GameDir))
                        cfg.GameDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
                    cfg.Accounts ??= new List<Account>();
                    cfg.Clients ??= new List<ClientEntry>();
                    cfg.Instances ??= new List<InstanceRef>();
                    return cfg;
                }
            }
        }
        catch (Exception)
        {
            // 配置损坏：退回上一份备份，再不行才用默认值，避免启动即崩
            try
            {
                var bak = FilePath + ".bak";
                if (File.Exists(bak))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(bak), JsonOpts);
                    if (cfg is not null)
                    {
                        cfg.Accounts ??= new List<Account>();
                        cfg.Clients ??= new List<ClientEntry>();
                        cfg.Instances ??= new List<InstanceRef>();
                        return cfg;
                    }
                }
            }
            catch (Exception) { }
        }
        return new AppConfig();
    }

    /// <summary>自检里别把临时配置写进用户真实配置。</summary>
    public static bool SuppressSave { get; set; }

    public void Save()
    {
        if (SuppressSave) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            try
            {
                // 覆盖前留一份上次的配置，配置写坏时还能捞回来
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
            }
            catch (Exception) { }
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>登记一个客户端（对应 versions/ 下的版本）。同名版本会覆盖显示信息。</summary>
    public ClientEntry RegisterClient(string versionId, string name, string mcVersion, string loader)
    {
        var found = Clients.FirstOrDefault(c => string.Equals(c.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            found = new ClientEntry { VersionId = versionId, CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            Clients.Add(found);
        }
        if (!string.IsNullOrWhiteSpace(name)) found.Name = name.Trim();
        found.McVersion = mcVersion;
        found.Loader = loader;
        Version = versionId;
        Save();
        return found;
    }

    public ClientEntry? FindClient(string versionId) =>
        Clients.FirstOrDefault(c => string.Equals(c.VersionId, versionId, StringComparison.OrdinalIgnoreCase));

    public void RemoveClient(string versionId) =>
        Clients.RemoveAll(c => string.Equals(c.VersionId, versionId, StringComparison.OrdinalIgnoreCase));

    [System.Text.Json.Serialization.JsonIgnore]
    public Account? CurrentAccount =>
        Accounts.FirstOrDefault(a => a.Key == LastAccountKey) ?? Accounts.FirstOrDefault();

    public void UseAccount(Account account)
    {
        var idx = Accounts.FindIndex(a => a.Key == account.Key);
        if (idx >= 0) Accounts[idx] = account;
        else Accounts.Add(account);
        LastAccountKey = account.Key;
        Save();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
