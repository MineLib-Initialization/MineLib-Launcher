using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>一个独立客户端：自己的目录、自己的 versions / mods / 存档。</summary>
public sealed class InstanceRef
{
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Note { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Note) ? Name : Name + "  ·  " + Note;
}

/// <summary>启动器配置，存 %APPDATA%\MineLib\config.json。</summary>
public sealed class AppConfig
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MineLib");

    public static string FilePath => Path.Combine(Dir, "config.json");

    public string GameDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    /// <summary>下载并发数。资源文件几千个小文件，串行会慢到不可用。</summary>
    public int DownloadThreads { get; set; } = 8;
    public string JvmArgs { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Isolation { get; set; } = true;
    public string AzureClientId { get; set; } = "";
    public string LastAccountKey { get; set; } = "";
    /// <summary>客户端（游戏实例）列表：每个实例是一个独立的 .minecraft 目录。</summary>
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
                    cfg.Instances ??= new List<InstanceRef>();
                    return cfg;
                }
            }
        }
        catch (Exception)
        {
            // 配置损坏就当默认值，避免启动即崩
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception)
        {
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public InstanceRef? CurrentInstanceRef =>
        Instances.FirstOrDefault(i => i.Dir == GameDir) ?? Instances.FirstOrDefault();

    /// <summary>注册一个客户端目录；已存在就复用。</summary>
    public InstanceRef RegisterInstance(string name, string dir, string note = "")
    {
        var found = Instances.FirstOrDefault(i => string.Equals(i.Dir, dir, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            found.Name = name;
            found.Note = note;
        }
        else
        {
            found = new InstanceRef { Name = name, Dir = dir, Note = note };
            Instances.Add(found);
        }
        GameDir = dir;
        CurrentInstance = dir;
        Save();
        return found;
    }

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
