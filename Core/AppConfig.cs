using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineLib.Core;

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
    public string JvmArgs { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Isolation { get; set; } = true;
    public string AzureClientId { get; set; } = "";
    public string LastAccountKey { get; set; } = "";
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
