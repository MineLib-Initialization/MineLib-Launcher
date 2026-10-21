using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using MineLib.Core;

namespace MineLib;

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly ObservableCollection<VersionInfo> _view = new();
    private List<VersionInfo> _all = new();
    private List<OfficialVersion> _official = new();
    private VersionInfo? _current;
    private JsonObject? _merged;
    private string _javaExe = "";
    private int _javaMajor;
    private string _javaNote = "未检测";
    private GameProcess? _proc;
    private CancellationTokenSource? _cts;
    private string _logPath = "";


    public sealed record OfficialVersion(string Id, string Type, string ReleaseTime);

    public MainWindow()
    {
        InitializeComponent();
        VersionList.ItemsSource = _view;
        TxtMemory.Text = _cfg.MemoryMb.ToString();
        TxtJvm.Text = _cfg.JvmArgs;
        ChkIsolation.IsChecked = _cfg.Isolation;

        ValGameDir.Text = _cfg.GameDir;
        LblStatusRight.Text = "MineLib 0.1";
        Bar.Visibility = Visibility.Collapsed;
        // 调试：MINELIB_TAB=1/2/3 直接打开账号/日志/下载页，便于离屏核对
        if (int.TryParse(Environment.GetEnvironmentVariable("MINELIB_TAB"), out var startTab))
            Tabs.SelectedIndex = Math.Clamp(startTab, 0, 3);
        switch (Environment.GetEnvironmentVariable("MINELIB_ACCOUNT"))
        {
            case "ms": RbMicrosoft.IsChecked = true; break;
            case "ygg": RbYggdrasil.IsChecked = true; break;
        }
        Loaded += (_, _) =>
        {
            RefreshVersions();
            RefreshAccounts();
            _ = DetectJavaAsync();
        };
        Closing += (_, _) =>
        {
            SaveSettings();
            _proc?.Stop();
        };
    }

    // ---------------------------------------------------------------- 版本列表
    private void RefreshVersions()
    {
        _all = Versions.Scan(_cfg.GameDir);
        ApplyFilter();
        LblVersionCount.Text = _all.Count == 0 ? "版本（没有找到）" : $"版本（{_all.Count}）";

        if (_all.Count == 0)
        {
            SetStatus("游戏目录里没有版本。先在「下载」页安装一个，或把整合包解压到 versions 里。", false);
            LblVersionName.Text = "未选择版本";
            LblVersionSub.Text = _cfg.GameDir;
            return;
        }

        var keep = _cfg.Version;
        _current = _all.FirstOrDefault(v => v.Id == keep) ?? _all[0];
        VersionList.SelectedItem = _current;
        ShowVersion(_current);
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        _view.Clear();
        foreach (var v in _all)
        {
            if (q.Length > 0 &&
                v.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                v.McVersion.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _view.Add(v);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshVersions();
        _ = DetectJavaAsync();
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is VersionInfo v) ShowVersion(v);
    }

    private void ShowVersion(VersionInfo v)
    {
        _current = v;
        _cfg.Version = v.Id;
        LblVersionName.Text = v.Id;
        LblVersionSub.Text = v.Subtitle;
        ValMcVersion.Text = string.IsNullOrEmpty(v.McVersion) ? "-" : v.McVersion;
        ValLoader.Text = v.Loader;
        ValMods.Text = "· " + v.Mods + " 个模组";
        ValDir.Text = v.Dir;
        var jar = Path.Combine(v.Dir, v.Id + ".jar");
        ValJar.Text = File.Exists(jar) ? jar : "缺失（启动时会从官方源补齐）";

        try
        {
            _merged = Versions.Resolve(_cfg.GameDir, v.Id);
        }
        catch (Exception ex)
        {
            _merged = null;
            SetStatus("读取版本 json 失败：" + ex.Message, false);
        }

        if (_merged is not null)
        {
            var major = _merged["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 0;
            if (major > 0 && _javaMajor > 0 && major != _javaMajor)
                SetStatus($"这个版本需要 Java {major}，当前选中的是 Java {_javaMajor}。", false);
            else if (!string.IsNullOrEmpty(_javaNote))
                SetStatus("就绪 · " + _javaNote, false);
        }
        RefreshAccountLabels();
    }

    // ---------------------------------------------------------------- Java
    private async Task DetectJavaAsync()
    {
        var needMajor = _merged?["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21;
        var gameDir = _cfg.GameDir;
        var cfgJava = _cfg.JavaPath;
        var result = await Task.Run(() => JavaFinder.Find(cfgJava, gameDir, needMajor));
        _javaExe = result.exe;
        _javaMajor = result.major;
        _javaNote = result.note;
        ValJava.Text = string.IsNullOrEmpty(result.exe) ? "未找到，请安装 Java 21" : result.exe + "  ·  " + result.note;
        LblStatusRight.Text = string.IsNullOrEmpty(result.exe)
            ? "Java：未找到"
            : $"Java {result.major} · 内存 {_cfg.MemoryMb} MB";
        if (_current is not null)
            SetStatus(string.IsNullOrEmpty(result.exe) ? "没有找到 Java，请安装 Java 21" : "就绪", false);
    }

    private void OnPickJavaClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 javaw.exe",
            Filter = "javaw.exe|javaw.exe;java.exe|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _cfg.JavaPath = dlg.FileName;
            _cfg.Save();
            _ = DetectJavaAsync();
        }
    }

    private void OnPickGameDirClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏目录（.minecraft）" };
        if (Directory.Exists(_cfg.GameDir)) dlg.InitialDirectory = _cfg.GameDir;
        if (dlg.ShowDialog() == true)
        {
            _cfg.GameDir = dlg.FolderName;
            _cfg.Save();
            ValGameDir.Text = _cfg.GameDir;
            RefreshVersions();
            _ = DetectJavaAsync();
        }
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e) => OpenFolder(_cfg.GameDir);

    private void OnOpenVersionDirClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetStatus("先选一个版本。", false); return; }
        OpenFolder(_current.Dir);
    }

    private void OnGoAccountClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    private void OnGoLogClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 2;

    private void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", '"' + path + '"'));
            else SetStatus("目录不存在：" + path, false);
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message, false);
        }
    }

    // ---------------------------------------------------------------- 设置项
    private void OnIsolationChanged(object sender, RoutedEventArgs e)
    {
        _cfg.Isolation = ChkIsolation.IsChecked == true;
        _cfg.Save();
    }

    private void OnJvmChanged(object sender, RoutedEventArgs e)
    {
        _cfg.JvmArgs = TxtJvm.Text;
        _cfg.Save();
    }

    private void SaveSettings()
    {
        _cfg.MemoryMb = int.TryParse(TxtMemory.Text, out var mb) ? Math.Clamp(mb, 512, 32768) : _cfg.MemoryMb;
        _cfg.JvmArgs = TxtJvm.Text;
        _cfg.Isolation = ChkIsolation.IsChecked == true;
        _cfg.Save();
        LblStatusRight.Text = $"Java {_javaMajor} · 内存 {_cfg.MemoryMb} MB";
    }

    // ---------------------------------------------------------------- 启动
    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_proc is { Running: true }) return;
        if (_current is null || _merged is null)
        {
            SetStatus("先选一个版本。", false);
            return;
        }

        var account = _cfg.CurrentAccount;
        if (account is null)
        {
            Tabs.SelectedIndex = 1;
            SetStatus("还没有账号。在「账号」页选一种登录方式后再启动。", false);
            return;
        }

        SaveSettings();
        BtnLaunch.IsEnabled = false;
        SetStatus("准备启动…", true);
        _cts = new CancellationTokenSource();

        try
        {
            if (string.IsNullOrEmpty(_javaExe))
            {
                await DetectJavaAsync();
                if (string.IsNullOrEmpty(_javaExe))
                    throw new LaunchException("找不到 Java。请安装 Java 21，或在概览页手动指定 javaw.exe。");
            }

            account = await MicrosoftAuth.EnsureFreshAsync(account, _cts.Token);
            if (account.Type == "microsoft") _cfg.UseAccount(account);
            RefreshAccounts();

            var progress = new Progress<InstallProgress>(p =>
            {
                LblStatus.Text = p.Stage;
                Bar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
            });
            await GameLauncher.PrepareAsync(_cfg.GameDir, _current.Id, _merged, progress, _cts.Token);
            Bar.Value = 0;

            var runDir = _cfg.Isolation ? _current.Dir : _cfg.GameDir;
            var (args, _) = GameLauncher.BuildCommand(_cfg.GameDir, _current.Id, _merged, account,
                _javaExe, _cfg.MemoryMb, _cfg.JvmArgs, runDir);

            _logPath = Path.Combine(_cfg.GameDir, "logs",
                "minelib-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            LblLogPath.Text = _logPath;
            AppendLog("启动参数：" + string.Join(" ", args.Take(Math.Min(8, args.Count))) + " …");
            AppendLog("日志文件：" + _logPath);

            _proc = new GameProcess();
            _proc.LineReceived += line => Dispatcher.BeginInvoke(() => AppendLog(line));
            _proc.Exited += code => Dispatcher.BeginInvoke(() =>
            {
                BtnLaunch.IsEnabled = true;
                BtnStop.Visibility = Visibility.Collapsed;
                SetStatus(code == 0 ? "游戏已退出" : "游戏异常退出，退出码 " + code, false);
                Tabs.SelectedIndex = 2;
            });
            _proc.Start(_javaExe, args, runDir, _logPath);

            BtnStop.Visibility = Visibility.Visible;
            SetStatus("游戏已启动", false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
            AppendLog("启动失败：" + ex.Message);
            Tabs.SelectedIndex = 2;
        }
        finally
        {
            BtnLaunch.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
            Bar.Value = 0;
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _proc?.Stop();
        _cts?.Cancel();
        SetStatus("正在结束游戏进程…", false);
    }

    // ---------------------------------------------------------------- 日志
    private readonly Queue<string> _recent = new();

    private void AppendLog(string line)
    {
        if (TxtLog.Text.Length > 400_000) TxtLog.Text = TxtLog.Text[^200_000..];
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();

        _recent.Enqueue(line.Length > 160 ? line[..160] + "…" : line);
        while (_recent.Count > 6) _recent.Dequeue();
        TxtHomeLog.Text = string.Join(Environment.NewLine, _recent);
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => TxtLog.Clear();

    private void OnOpenLogFileClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath))
        {
            SetStatus("还没有日志文件（启动一次游戏后生成）", false);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", '"' + _logPath + '"'));
        }
        catch (Exception ex)
        {
            SetStatus("打开日志失败：" + ex.Message, false);
        }
    }

    // ---------------------------------------------------------------- 账号
    private void RefreshAccounts()
    {
        AccountList.ItemsSource = null;
        AccountList.ItemsSource = _cfg.Accounts;
        var acct = _cfg.CurrentAccount;
        ValAccount.Text = acct?.Summary ?? "未登录";
        ValAccountBig.Text = acct?.Summary ?? "未登录";
        ValAccountNote.Text = acct is null
            ? "没有账号时无法启动，请先选一种登录方式。"
            : acct.Type switch
            {
                "microsoft" => "微软账号，令牌到期会自动续期。",
                "yggdrasil" => "外置登录：" + acct.Authlib,
                _ => "离线：不向 Mojang 认证，服务器需开启离线模式。"
            };
        if (acct is not null) TxtOfflineName.Text = acct.Type == "offline" ? acct.Name : TxtOfflineName.Text;
        if (ValAccountNote2 is not null) ValAccountNote2.Text = ValAccountNote.Text;
    }

    private void RefreshAccountLabels() => RefreshAccounts();

    private void OnLoginModeChanged(object sender, RoutedEventArgs e)
    {
        if (PanelMicrosoft is null) return;
        PanelMicrosoft.Visibility = RbMicrosoft.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelYggdrasil.Visibility = RbYggdrasil.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelOffline.Visibility = RbOffline.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnMsLoginClick(object sender, RoutedEventArgs e)
    {
        string clientId;
        try
        {
            clientId = MicrosoftAuth.ResolveClientId(_cfg);
        }
        catch (Exception ex)
        {
            LblMsStatus.Text = ex.Message;
            return;
        }
        BtnMsLogin.IsEnabled = false;
        LblMsStatus.Text = "正在获取验证码…";
        try
        {
            var account = await MicrosoftAuth.LoginAsync(clientId, flow =>
            {
                // verification_uri_complete 里已经带上了验证码，浏览器打开即可确认，用户不用手输
                var complete = flow["verification_uri_complete"]?.ToString();
                var plain = flow["verification_uri"]?.ToString() ?? "https://microsoft.com/link";
                var code = flow["user_code"]?.ToString() ?? "";
                var target = string.IsNullOrWhiteSpace(complete) ? plain : complete;
                Dispatcher.BeginInvoke(() =>
                {
                    LblMsCode.Text = "验证码 " + code;
                    LblMsStatus.Text = string.IsNullOrWhiteSpace(complete)
                        ? "浏览器已打开，输入上面的验证码完成登录。"
                        : "浏览器已打开，确认登录即可（验证码已自动填好）。";
                    try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { }
                });
            }, msg => Dispatcher.BeginInvoke(() => LblMsStatus.Text = msg).Task);

            _cfg.UseAccount(account);
            RefreshAccounts();
            LblMsCode.Text = "";
            LblMsStatus.Text = "登录成功：" + account.Name;
            SetStatus("已登录微软账号 " + account.Name, false);
        }
        catch (Exception ex)
        {
            LblMsStatus.Text = ex.Message;
        }
        finally
        {
            BtnMsLogin.IsEnabled = true;
        }
    }

    private async void OnYggLoginClick(object sender, RoutedEventArgs e)
    {
        BtnYggLogin.IsEnabled = false;
        LblYggStatus.Text = "正在登录…";
        try
        {
            var account = await YggdrasilAuth.LoginAsync(TxtYggApi.Text.Trim(), TxtYggUser.Text.Trim(),
                TxtYggPass.Password);
            _cfg.UseAccount(account);
            RefreshAccounts();
            LblYggStatus.Text = "登录成功：" + account.Name;
            SetStatus("已登录外置账号 " + account.Name, false);
            try
            {
                var jar = await YggdrasilAuth.EnsureInjectorAsync();
                AppendLog("authlib-injector 就绪：" + jar);
            }
            catch (Exception ex2)
            {
                LblYggStatus.Text += "（注入器下载失败：" + ex2.Message + "）";
            }
        }
        catch (Exception ex)
        {
            LblYggStatus.Text = ex.Message;
        }
        finally
        {
            BtnYggLogin.IsEnabled = true;
        }
    }

    private void OnOfflineClick(object sender, RoutedEventArgs e)
    {
        var name = TxtOfflineName.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("先输入一个玩家名。", false);
            return;
        }
        var account = OfflineAuth.Create(name);
        _cfg.UseAccount(account);
        RefreshAccounts();
        SetStatus("已切换为离线账号 " + account.Name, false);
    }

    private void OnUseAccountClick(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is Account a)
        {
            _cfg.UseAccount(a);
            RefreshAccounts();
            SetStatus("已切换到 " + a.Summary, false);
        }
    }

    private void OnDeleteAccountClick(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account a) return;
        _cfg.Accounts.RemoveAll(x => x.Key == a.Key);
        if (_cfg.LastAccountKey == a.Key) _cfg.LastAccountKey = "";
        _cfg.Save();
        RefreshAccounts();
        SetStatus("已删除账号 " + a.Name, false);
    }

    // ---------------------------------------------------------------- 下载
    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex == 3 && _official.Count == 0)
            await LoadOfficialAsync();
    }

    private async Task LoadOfficialAsync()
    {
        LblInstallStatus.Text = "读取官方版本清单…";
        try
        {
            var manifest = await Http.GetJsonAsync(Installer.ManifestUrl);
            var list = new List<OfficialVersion>();
            if (manifest["versions"] is JsonArray arr)
            {
                foreach (var node in arr)
                {
                    if (node is not JsonObject v) continue;
                    var when = v["releaseTime"]?.ToString() ?? "";
                    if (DateTimeOffset.TryParse(when, out var dto))
                        when = dto.ToLocalTime().ToString("yyyy-MM-dd");
                    list.Add(new OfficialVersion(
                        v["id"]?.ToString() ?? "",
                        v["type"]?.ToString() ?? "",
                        when));
                    if (list.Count >= 80) break;
                }
            }
            _official = list;
            OfficialList.ItemsSource = _official;
            OfficialList.SelectedIndex = 0;
            LblInstallStatus.Text = "列出最近 " + _official.Count + " 个版本";
        }
        catch (Exception ex)
        {
            LblInstallStatus.Text = "读取清单失败：" + ex.Message;
        }
    }

    private async void OnDoInstallClick(object sender, RoutedEventArgs e)
    {
        if (OfficialList.SelectedItem is not OfficialVersion v) return;
        BtnDoInstall.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                LblInstallStatus.Text = p.Stage;
                Bar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
            });
            await Installer.InstallVersionAsync(_cfg.GameDir, v.Id, progress, _cts.Token);
            LblInstallStatus.Text = "已安装 " + v.Id;
            RefreshVersions();
        }
        catch (OperationCanceledException)
        {
            LblInstallStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            LblInstallStatus.Text = "安装失败：" + ex.Message;
        }
        finally
        {
            BtnDoInstall.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 3;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _proc?.Stop();
    }

    // ---------------------------------------------------------------- 状态
    private void SetStatus(string text, bool busy)
    {
        LblStatus.Text = text;
        Bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LblStatusRight.Text = string.IsNullOrEmpty(_javaExe)
            ? "Java：未找到"
            : $"Java {_javaMajor} · 内存 {_cfg.MemoryMb} MB";
    }
}
