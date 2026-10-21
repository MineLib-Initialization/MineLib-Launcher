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
    private bool _loadingLoaderCombos;
    private List<LoaderVersion> _loaderVersions = new();
    private List<ModHit> _modHits = new();
    private List<ModFile> _modFiles = new();
    private long _modTotal;
    private int _modOffset;
    private bool _modInited;


    public sealed record OfficialVersion(string Id, string Type, string ReleaseTime);

    public MainWindow()
    {
        InitializeComponent();
        VersionList.ItemsSource = _view;
        TxtMemory.Text = _cfg.MemoryMb.ToString();
        TxtJvm.Text = _cfg.JvmArgs;
        ChkIsolation.IsChecked = _cfg.Isolation;

        ValGameDir.Text = _cfg.GameDir;
        NewLoaderCombo.ItemsSource = new[] { "不装（纯原版）", "Fabric", "Quilt", "Forge", "NeoForge" };
        NewLoaderCombo.SelectedIndex = 1;
        NewDirBox.Text = Path.Combine(AppConfig.Dir, "instances");
        NewNameBox.Text = "我的客户端";
        RefreshInstanceCombo();
        ThreadsCombo.ItemsSource = new[] { 4, 8, 16, 32 };
        var savedThreads = Math.Clamp(_cfg.DownloadThreads, 4, 32);
        ThreadsCombo.SelectedItem = new[] { 4, 8, 16, 32 }.FirstOrDefault(t => t >= savedThreads);
        if (ThreadsCombo.SelectedItem is null) ThreadsCombo.SelectedIndex = 1;
        LblStatusRight.Text = "MineLib 0.1";
        Bar.Visibility = Visibility.Collapsed;
        // 调试：MINELIB_TAB=1/2/3 直接打开账号/日志/下载页，便于离屏核对
        if (int.TryParse(Environment.GetEnvironmentVariable("MINELIB_TAB"), out var startTab))
            Tabs.SelectedIndex = Math.Clamp(startTab, 0, 4);
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
        if (Tabs.SelectedIndex == 4) InitModTab();
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = TxtSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

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
    private void OnThreadsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThreadsCombo.SelectedItem is int n)
        {
            _cfg.DownloadThreads = n;
            _cfg.Save();
        }
    }

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
            await GameLauncher.PrepareAsync(_cfg.GameDir, _current.Id, _merged, _cfg.DownloadThreads,
                progress, _cts.Token);
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

    // ---------------------------------------------------------------- 客户端（独立实例）
    private List<InstanceRef> _instanceItems = new();

    private void RefreshInstanceCombo()
    {
        if (_cfg.Instances.Count == 0)
            _cfg.RegisterInstance("默认客户端", _cfg.GameDir);

        _loadingLoaderCombos = true;
        try
        {
            _instanceItems = _cfg.Instances.ToList();
            InstanceCombo.ItemsSource = null;
            InstanceCombo.ItemsSource = _instanceItems;
            var cur = _instanceItems.FirstOrDefault(i =>
                string.Equals(i.Dir, _cfg.GameDir, StringComparison.OrdinalIgnoreCase));
            if (cur is not null) InstanceCombo.SelectedItem = cur;
            else if (_instanceItems.Count > 0) InstanceCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingLoaderCombos = false;
        }
    }

    private void OnInstanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLoaderCombos) return;
        if (InstanceCombo.SelectedItem is not InstanceRef inst) return;
        if (string.Equals(inst.Dir, _cfg.GameDir, StringComparison.OrdinalIgnoreCase)) return;

        _cfg.GameDir = inst.Dir;
        _cfg.CurrentInstance = inst.Dir;
        _cfg.Version = "";
        _cfg.Save();
        ValGameDir.Text = _cfg.GameDir;
        RefreshVersions();
        _ = DetectJavaAsync();
        SetStatus("已切换到客户端「" + inst.Name + "」", false);
    }

    private void OnNewInstanceClick(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedIndex = 3;
        SetStatus("在右边填名字选位置，点「创建新客户端」。", false);
    }

    private void OnPickInstanceParentClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择安装位置（客户端会建在这个目录下）" };
        if (Directory.Exists(NewDirBox.Text)) dlg.InitialDirectory = NewDirBox.Text;
        if (dlg.ShowDialog() == true) NewDirBox.Text = dlg.FolderName;
    }

    // ---------------------------------------------------------------- mods 目录
    private string ModsDir()
    {
        var dir = _cfg.Isolation && _current is not null
            ? Path.Combine(_current.Dir, "mods")
            : Path.Combine(_cfg.GameDir, "mods");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void OpenModsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", '"' + ModsDir() + '"'));
        }
        catch (Exception ex)
        {
            SetStatus("打开 mods 目录失败：" + ex.Message, false);
        }
    }

    private void OnOpenModsDirClick(object sender, RoutedEventArgs e) => OpenModsFolder();

    // ---------------------------------------------------------------- 模组页
    private string SelectedSortKey =>
        ModSortCombo.SelectedIndex >= 0 ? Modrinth.Sorts[ModSortCombo.SelectedIndex].Key : "relevance";

    private string SelectedModLoader =>
        ModLoaderCombo.SelectedIndex >= 0 ? Modrinth.LoaderFilters[ModLoaderCombo.SelectedIndex].Key : "";

    private void InitModTab()
    {
        if (ModSortCombo.ItemsSource is null)
        {
            ModSortCombo.ItemsSource = Modrinth.Sorts.Select(s => s.Label).ToList();
            ModSortCombo.SelectedIndex = 0;
            ModLoaderCombo.ItemsSource = Modrinth.LoaderFilters.Select(s => s.Label).ToList();
            ModLoaderCombo.SelectedIndex = 0;
        }
        // 首次进入时用当前版本的 MC 版本和加载器预填筛选
        if (string.IsNullOrWhiteSpace(ModMcBox.Text) && _current is not null)
        {
            ModMcBox.Text = _current.McVersion;
            ModLoaderCombo.SelectedIndex = _current.Loader switch
            {
                "Fabric" => 1,
                "Quilt" => 2,
                "Forge" => 3,
                "NeoForge" => 4,
                _ => 0
            };
            _modInited = true;
            _ = RunModSearchAsync(0);
            return;
        }
        if (!_modInited)
        {
            _modInited = true;
            _ = RunModSearchAsync(0);
        }
    }

    private async Task RunModSearchAsync(int offset)
    {
        _modOffset = Math.Max(0, offset);
        LblModStatus.Text = "检索中…";
        try
        {
            var (hits, total) = await Modrinth.SearchAsync(ModSearchBox.Text.Trim(), ModMcBox.Text.Trim(),
                SelectedModLoader, SelectedSortKey, _modOffset, 20, CancellationToken.None);
            _modHits = hits;
            _modTotal = total;
            ModList.ItemsSource = null;
            ModList.ItemsSource = _modHits;
            LblModStatus.Text = $"共 {total} 个结果 · 第 {_modOffset / 20 + 1} / {Math.Max(1, (total + 19) / 20)} 页";
            ModVersionList.ItemsSource = null;
            _modFiles = new List<ModFile>();
        }
        catch (Exception ex)
        {
            LblModStatus.Text = "检索失败：" + ex.Message;
        }
    }

    private void OnModSearchClick(object sender, RoutedEventArgs e) => _ = RunModSearchAsync(0);

    private void OnModSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) _ = RunModSearchAsync(0);
    }

    private void OnModPrevClick(object sender, RoutedEventArgs e)
    {
        if (_modOffset > 0) _ = RunModSearchAsync(_modOffset - 20);
    }

    private void OnModNextClick(object sender, RoutedEventArgs e)
    {
        if (_modOffset + 20 < _modTotal) _ = RunModSearchAsync(_modOffset + 20);
    }

    private async void OnModSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ModList.SelectedItem is not ModHit hit) return;
        ModDetailTitle.Text = hit.Title;
        ModDetailDesc.Text = hit.Description;
        ModVersionList.ItemsSource = null;
        _modFiles = new List<ModFile>();
        LblModDownload.Text = "读取版本列表…";
        try
        {
            _modFiles = await Modrinth.FilesAsync(hit.ProjectId, ModMcBox.Text.Trim(), SelectedModLoader,
                CancellationToken.None);
            ModVersionList.ItemsSource = _modFiles;
            if (_modFiles.Count > 0) ModVersionList.SelectedIndex = 0;
            LblModDownload.Text = _modFiles.Count == 0
                ? "没有匹配当前版本/加载器的文件，换个筛选试试。"
                : "共 " + _modFiles.Count + " 个可用文件";
        }
        catch (Exception ex)
        {
            LblModDownload.Text = "读取失败：" + ex.Message;
        }
    }

    private async void OnModDownloadClick(object sender, RoutedEventArgs e)
    {
        var file = ModVersionList.SelectedItem as ModFile ?? _modFiles.FirstOrDefault();
        if (file is null)
        {
            LblModDownload.Text = "先选一个文件。";
            return;
        }
        var dir = ModsDir();
        BtnModDownload.IsEnabled = false;
        ModBar.Visibility = Visibility.Visible;
        ModBar.Value = 0;
        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total > 0) ModBar.Value = p.done * 100.0 / p.total;
                LblModDownload.Text = string.Format("{0} / {1} KB", p.done / 1024, p.total / 1024);
            });
            var ok = await Modrinth.DownloadAsync(file, dir, progress, CancellationToken.None);
            LblModDownload.Text = ok ? "已下载到 " + dir : "下载失败（网络或文件已被作者删除）";
            if (ok) SetStatus("已下载 " + file.Name, false);
        }
        catch (Exception ex)
        {
            LblModDownload.Text = "下载失败：" + ex.Message;
        }
        finally
        {
            BtnModDownload.IsEnabled = true;
            ModBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex == 3 && _official.Count == 0)
            await LoadOfficialAsync();
        if (Tabs.SelectedIndex == 4) InitModTab();
    }

    private async Task LoadOfficialAsync()
    {
        LblCreateStatus.Text = "读取官方版本清单…";
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
            LblCreateStatus.Text = "列出最近 " + _official.Count + " 个版本";
        }
        catch (Exception ex)
        {
            LblCreateStatus.Text = "读取清单失败：" + ex.Message;
        }
    }

    private async void OnCreateInstanceClick(object sender, RoutedEventArgs e)
    {
        if (OfficialList.SelectedItem is not OfficialVersion v)
        {
            LblCreateStatus.Text = "先在左边选一个游戏版本。";
            return;
        }

        var name = NewNameBox.Text.Trim();
        if (name.Length == 0)
        {
            LblCreateStatus.Text = "给客户端起个名字。";
            return;
        }
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        var parent = NewDirBox.Text.Trim();
        if (parent.Length == 0)
        {
            LblCreateStatus.Text = "选一个安装位置。";
            return;
        }

        var dir = Path.Combine(parent, name);
        if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
        {
            LblCreateStatus.Text = "目录 " + dir + " 里已经有东西了。换个名字或位置——不会往已有客户端里塞东西。";
            return;
        }

        var loaderLabel = NewLoaderCombo.SelectedItem as string ?? "不装（纯原版）";
        var kind = loaderLabel switch
        {
            "Fabric" => (LoaderKind?)LoaderKind.Fabric,
            "Quilt" => LoaderKind.Quilt,
            "Forge" => LoaderKind.Forge,
            "NeoForge" => LoaderKind.NeoForge,
            _ => null
        };

        string javaExe = "";
        if (kind is not null && Loaders.NeedsJava(kind.Value))
        {
            if (string.IsNullOrEmpty(_javaExe)) await DetectJavaAsync();
            if (string.IsNullOrEmpty(_javaExe))
            {
                LblCreateStatus.Text = "Forge / NeoForge 要用 Java 跑官方安装器，先装 Java 21。";
                return;
            }
            javaExe = _javaExe;
        }

        BtnCreate.IsEnabled = false;
        BtnCreateCancel.Visibility = Visibility.Visible;
        CreateBar.Visibility = Visibility.Visible;
        CreateBar.Value = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            LblCreateStatus.Text = p.Stage;
            CreateBar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
        });

        try
        {
            Directory.CreateDirectory(dir);
            LblCreateStatus.Text = "创建 " + dir + " …";
            await Installer.InstallVersionAsync(dir, v.Id, _cfg.DownloadThreads, progress, _cts.Token);

            var note = v.Id;
            var installedId = v.Id;
            if (kind is not null)
            {
                var versions = await Loaders.VersionsAsync(kind.Value, v.Id, _cts.Token);
                var lv = versions.FirstOrDefault(x => x.Channel == "稳定") ?? versions.FirstOrDefault();
                if (lv is null)
                    throw new InvalidOperationException("这个游戏版本没有可用的 " + loaderLabel + " 构建。");
                var madeId = await Loaders.InstallAsync(kind.Value, v.Id, lv.Version, dir, javaExe, progress,
                    line => Dispatcher.BeginInvoke(() => AppendLog(line)), _cts.Token);
                if (!string.IsNullOrEmpty(madeId)) installedId = madeId;
                note = v.Id + " · " + loaderLabel;
            }

            var inst = _cfg.RegisterInstance(name, dir, note);
            ValGameDir.Text = _cfg.GameDir;
            RefreshInstanceCombo();
            InstanceCombo.SelectedItem = _instanceItems.FirstOrDefault(x => x.Dir == dir) ?? inst;
            RefreshVersions();
            var made = _all.FirstOrDefault(x => x.Id == installedId);
            if (made is not null)
            {
                VersionList.SelectedItem = made;
                ShowVersion(made);
            }
            LblCreateStatus.Text = "创建完成：" + name + "（" + note + "）。已切到这个客户端。";
            OpenFolder(dir);
        }
        catch (OperationCanceledException)
        {
            LblCreateStatus.Text = "已取消（这个目录里已下载的文件会保留，下次同名创建会跳过它们）。";
        }
        catch (Exception ex)
        {
            LblCreateStatus.Text = "创建失败：" + ex.Message;
            AppendLog("创建客户端失败：" + ex.Message);
        }
        finally
        {
            BtnCreate.IsEnabled = true;
            BtnCreateCancel.Visibility = Visibility.Collapsed;
            CreateBar.Visibility = Visibility.Collapsed;
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