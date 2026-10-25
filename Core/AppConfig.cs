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

/// <summary>一个客户端文件夹（游戏根目录）：里面可以并排放多个客户端。</summary>
public sealed class RootEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<ClientEntry> Clients { get; set; } = new();

    public string Display => string.IsNullOrEmpty(Name) ? Path : Name;
}

/// <summary>启动器配置，默认存 &lt;启动器目录&gt;\MineLibLauncher\config.json（便携）。</summary>
public sealed class AppConfig
{
    public static string Dir => Paths.ConfigDir;

    public static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>当前生效的游戏根目录（= 当前客户端文件夹）。</summary>
    public string GameDir { get; set; } = Paths.DefaultGameRoot;

    /// <summary>所有客户端文件夹。每个文件夹里可以并排放多个客户端。</summary>
    public List<RootEntry> Roots { get; set; } = new();

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
    /// <summary>旧版字段：单根目录时代的客户端列表；载入时会迁到 Roots 里。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("Clients")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ClientEntry>? LegacyClients { get; set; }

    /// <summary>当前客户端文件夹里的客户端。代码里沿用 Clients 这个名字。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ClientEntry> Clients => CurrentRoot.Clients;
    /// <summary>是否已经把旧的「一个客户端一个目录」合并成单根目录。</summary>
    public bool SingleRootMigrated { get; set; }
    /// <summary>旧版模型遗留：旧客户端目录登记表，迁移后清空。</summary>
    public List<InstanceRef> Instances { get; set; } = new();
    public string CurrentInstance { get; set; } = "";
    public List<Account> Accounts { get; set; } = new();

    /// <summary>当前客户端文件夹；没有就建一个默认的。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RootEntry CurrentRoot
    {
        get
        {
            Roots ??= new List<RootEntry>();
            var hit = Roots.FirstOrDefault(r => SamePath(r.Path, GameDir));
            if (hit is not null)
            {
                hit.Clients ??= new List<ClientEntry>();
                return hit;
            }
            hit = new RootEntry { Name = RootName(GameDir), Path = GameDir, Clients = new List<ClientEntry>() };
            Roots.Add(hit);
            return hit;
        }
    }

    /// <summary>比较路径用：去掉尾部反斜杠、统一大小写不敏感比较。</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim()).TrimEnd('\\'); }
        catch (Exception) { return path.Trim().TrimEnd('\\'); }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>清理客户端文件夹列表：去空、去重、路径归一。</summary>
    private void NormalizeRoots()
    {
        Roots ??= new List<RootEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<RootEntry>();
        foreach (var r in Roots)
        {
            if (r is null) continue;
            var p = NormalizePath(r.Path);
            if (string.IsNullOrEmpty(p)) continue;
            r.Path = p;
            r.Clients ??= new List<ClientEntry>();
            if (!seen.Add(p)) continue;      // 同一个目录只留一条
            if (string.IsNullOrWhiteSpace(r.Name)) r.Name = RootName(p);
            cleaned.Add(r);
        }
        Roots = cleaned;
        GameDir = NormalizePath(GameDir);
        if (string.IsNullOrEmpty(GameDir)) GameDir = Paths.DefaultGameRoot;
    }

    /// <summary>载入后的统一整理：旧字段迁移 + 根目录清理。任何返回路径都必须走一遍。</summary>
    private void AfterLoad()
    {
        NormalizeRoots();
        if (LegacyClients is { Count: > 0 })
        {
            var rootEntry = CurrentRoot;
            foreach (var c in LegacyClients)
                if (rootEntry.Clients.All(x => !string.Equals(x.VersionId, c.VersionId, StringComparison.OrdinalIgnoreCase)))
                    rootEntry.Clients.Add(c);
            LegacyClients = null;
        }
    }

    private static string RootName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed;
            return name;
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>登记一个客户端文件夹；已存在就复用。</summary>
    public RootEntry RegisterRoot(string path, string? name = null)
    {
        Roots ??= new List<RootEntry>();
        path = NormalizePath(path);
        var hit = Roots.FirstOrDefault(r => SamePath(r.Path, path));
        if (hit is null)
        {
            hit = new RootEntry { Name = string.IsNullOrWhiteSpace(name) ? RootName(path) : name!.Trim(), Path = path };
            Roots.Add(hit);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            hit.Name = name!.Trim();
        }
        hit.Clients ??= new List<ClientEntry>();
        GameDir = path;
        Save();
        return hit;
    }

    /// <summary>从列表里移除一个客户端文件夹（不删磁盘文件）。</summary>
    public void RemoveRoot(string path)
    {
        path = NormalizePath(path);
        // 先把当前根切走再删，否则 CurrentRoot 下次访问又会把这条加回来
        if (SamePath(GameDir, path))
        {
            var next = Roots?.FirstOrDefault(r => !SamePath(r.Path, path))?.Path ?? Paths.DefaultGameRoot;
            GameDir = next;
        }
        Roots?.RemoveAll(r => SamePath(r.Path, path));
        Save();
    }

    public static AppConfig Load()
    {
        try
        {
            // 首次便携化：把旧的 %APPDATA%\MineLib\config.json 导过来，别丢账号和设置
            if (!File.Exists(FilePath) && Paths.Portable && File.Exists(Paths.LegacyConfigPath))
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.Copy(Paths.LegacyConfigPath, FilePath, true);
                }
                catch (Exception) { }
            }
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
                    cfg.Roots ??= new List<RootEntry>();
                    cfg.Instances ??= new List<InstanceRef>();
                    cfg.AfterLoad();
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
                        cfg.Roots ??= new List<RootEntry>();
                        cfg.Instances ??= new List<InstanceRef>();
                        cfg.AfterLoad();
                        return cfg;
                    }
                }
            }
            catch (Exception) { }
        }
        var fresh = new AppConfig();
        fresh.AfterLoad();
        return fresh;
    }

    /// <summary>自检里别把临时配置写进用户真实配置。</summary>
    public static bool SuppressSave { get; set; }

    private static readonly object SaveLock = new();

    public void Save()
    {
        if (SuppressSave) return;
        lock (SaveLock)
        {
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
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
            // 老配置里的令牌是明文：迁移这一次别把明文留在 .bak 里
            try
            {
                var bak = FilePath + ".bak";
                if (json.Contains(Secrets.Prefix, StringComparison.Ordinal) && File.Exists(bak) &&
                    !File.ReadAllText(bak).Contains(Secrets.Prefix, StringComparison.Ordinal))
                    File.WriteAllText(bak, json);
            }
            catch (Exception) { }
        }
        catch (Exception)
        {
        }
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
