using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    // ---------------------------------------------------------------- 窗口
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowChrome();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (Exception) { }
        }
    }

    private const double ShellRadius = 10;

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border 的圆角只裁自己的背景，不裁子元素；子元素（标题栏、侧栏）是直角矩形，
        // 所以这里显式给一层圆角矩形裁剪，否则四个角会被子元素盖成直角。
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Clip = null;
            return;
        }
        RootBorder.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), ShellRadius, ShellRadius);
    }

    private void UpdateWindowChrome()
    {
        // 无边框窗口最大化时会盖住任务栏、边缘也会溢出屏幕，用一层边距补回来
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        var maxed = WindowState == WindowState.Maximized;
        RootBorder.CornerRadius = maxed ? new CornerRadius(0) : new CornerRadius(ShellRadius);
        RootBorder.Clip = maxed || RootBorder.ActualWidth <= 0
            ? null
            : new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), ShellRadius, ShellRadius);
        GlyphMax.Visibility = maxed ? Visibility.Collapsed : Visibility.Visible;
        GlyphRestore.Visibility = maxed ? Visibility.Visible : Visibility.Collapsed;
        BtnMax.ToolTip = maxed ? "还原" : "最大化";
    }


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
        NewNameBox.Text = "我的客户端";
        var threadOptions = new[] { 8, 16, 32, 64 };
        ThreadsCombo.ItemsSource = threadOptions;
        var savedThreads = Math.Clamp(_cfg.DownloadThreads, 4, 64);
        ThreadsCombo.SelectedItem = threadOptions.FirstOrDefault(t => t >= savedThreads);
        if (ThreadsCombo.SelectedItem is null) ThreadsCombo.SelectedIndex = 1;
        DownloadSources.Apply(_cfg.DownloadSourceId);
        LblStatusRight.Text = "MineLib " + Updates.Current;
        TxtWindowTitle.Text = _cfg.WindowTitle;
        LblConfigDir.Text = AppConfig.FilePath;
        LblAboutVersion.Text = "MineLib 启动器 " + Updates.Current;
        LblThreadsHint.Text = _cfg.DownloadThreads.ToString();
        ApplyWindowTitle();
        _loadingLoaderCombos = true;
        try
        {
            if (DownloadSources.Current.Id == DownloadSource.Bmclapi.Id) RbSrcBmclapi.IsChecked = true;
            else RbSrcOfficial.IsChecked = true;
        }
        finally { _loadingLoaderCombos = false; }
        Bar.Visibility = Visibility.Collapsed;
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
        StateChanged += (_, _) => UpdateWindowChrome();
        // 调试：MINELIB_TAB=1/2/3 直接打开账号/日志/下载页，便于离屏核对
        if (int.TryParse(Environment.GetEnvironmentVariable("MINELIB_TAB"), out var startTab))
            ApplyTabIndex(startTab);
        switch (Environment.GetEnvironmentVariable("MINELIB_ACCOUNT"))
        {
            case "ms": RbMicrosoft.IsChecked = true; break;
            case "ygg": RbYggdrasil.IsChecked = true; break;
        }
        // 宣传用：--shot <tab> <query> <out> 自渲染一张界面图（不依赖屏幕截图）
        var shotArgs = Environment.GetCommandLineArgs();
        var shotIdx = Array.IndexOf(shotArgs, "--shot");
        if (shotIdx > 0 && shotArgs.Length > shotIdx + 3)
        {
            var tabIdx = int.Parse(shotArgs[shotIdx + 1]);
            var query = shotArgs[shotIdx + 2];
            var outPath = shotArgs[shotIdx + 3];
            Loaded += async (_, _) =>
            {
                ApplyTabIndex(tabIdx);
                if (tabIdx == 4)
                {
                    await Task.Delay(600);
                    if (shotArgs.Length > shotIdx + 4 && shotArgs[shotIdx + 4] == "any") ModMcBox.Text = "";
                    ModSearchBox.Text = query;
                    await RunModSearchAsync(0);
                    await Task.Delay(1500);
                }
                else if (tabIdx == 3)
                {
                    // 下载页要等官方版本清单拉回来，不然截到的是空列表
                    await Task.Delay(6000);
                }
                else
                {
                    await Task.Delay(1800);
                }
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)ActualWidth, (int)ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(this);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(outPath)) enc.Save(fs);
                Close();
            };
        }

        Loaded += async (_, _) =>
        {
            // 旧版「一个客户端一个目录」→ 合并成单根目录（只做一次，失败不动原目录）
            try
            {
                var report = await Task.Run(() => Workspace.EnsureSingleRoot(_cfg,
                    line => Dispatcher.BeginInvoke(() => AppendLog(line))));
                if (report.Changed)
                {
                    _cfg.Save();
                    SetStatus("已把旧的客户端目录合并进 " + _cfg.GameDir, false);
                    ValGameDir.Text = _cfg.GameDir;
                }
            }
            catch (Exception ex)
            {
                AppendLog("目录迁移失败（原数据保持不动）：" + ex.Message);
            }
            await RefreshVersionsAsync();
            RefreshAccounts();
            _ = DetectJavaAsync();
            _ = CheckUpdatesAsync(false);
            ShowAppliedNotes();
        };
        Closing += (_, _) =>
        {
            _logTimer.Stop();
            SaveSettings();
            _proc?.Stop();
        };
    }

    // ---------------------------------------------------------------- 版本列表
    private int _versionScanSeq;

    private async Task RefreshVersionsAsync()
    {
        // 版本扫描要读一堆 json、数 mods；客户端多了以后在 UI 线程做就是整窗卡住
        var seq = ++_versionScanSeq;
        var gameDir = _cfg.GameDir;
        List<VersionInfo> list;
        try
        {
            list = await Task.Run(() => Versions.Scan(gameDir));
        }
        catch (Exception ex)
        {
            SetStatus("读取版本列表失败：" + ex.Message, false);
            return;
        }
        if (seq != _versionScanSeq) return;

        // 根目录里已有版本但还没登记成客户端时补登记（加载器的基础版本不登记）
        if (_cfg.Clients.Count == 0 && list.Count > 0)
            await Task.Run(() => Workspace.RegisterClientsFromDisk(_cfg));

        _all = list;
        ApplyFilter();

        if (_view.Count == 0)
        {
            SetStatus(_all.Count == 0
                ? "游戏根目录里还没有版本。去「下载」页创建一个客户端。"
                : "没有匹配的客户端，换个关键词试试。", false);
            LblVersionName.Text = "未选择客户端";
            LblVersionSub.Text = _cfg.GameDir;
            _current = null;
            return;
        }

        var keep = _cfg.Version;
        _current = _view.FirstOrDefault(v => v.Id == keep) ?? _view[0];
        VersionList.SelectedItem = _current;
        ShowVersion(_current);
        if (DownloadTabs.SelectedIndex == 1) InitModTab();
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        _view.Clear();

        // 客户端 = 配置里登记的版本；磁盘上没有的顺手清掉
        var alive = new HashSet<string>(_all.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
        if (_cfg.Clients.RemoveAll(c => !alive.Contains(c.VersionId)) > 0) _cfg.Save();

        foreach (var client in _cfg.Clients)
        {
            var v = _all.FirstOrDefault(x => string.Equals(x.Id, client.VersionId, StringComparison.OrdinalIgnoreCase));
            if (v is null) continue;
            v.DisplayName = string.IsNullOrWhiteSpace(client.Name) ? null : client.Name;
            if (q.Length > 0 &&
                v.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                v.McVersion.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _view.Add(v);
        }

        LblVersionCount.Text = "客户端（" + _view.Count + "）";
        LblRootHint.Text = Path.GetFileName(_cfg.GameDir.TrimEnd('\\'));
        LblCreateRoot.Text = _cfg.GameDir;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshVersionsAsync();
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
        var client = _cfg.FindClient(v.Id);
        LblVersionName.Text = string.IsNullOrWhiteSpace(client?.Name) ? v.Id : client!.Name;
        LblTitleClient.Text = LblVersionName.Text;
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

    private async void OnFetchJavaClick(object sender, RoutedEventArgs e)
    {
        var needMajor = _merged?["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21;
        BtnFetchJava.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var jp = new Progress<InstallProgress>(p =>
        {
            SetStatus(p.Stage, true);
            Bar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
        });
        try
        {
            var got = await JavaProvisioner.EnsureAsync(needMajor, jp,
                AppendLog, _cts.Token);
            _javaExe = got.exe;
            _javaMajor = got.major;
            ValJava.Text = got.exe + "  ·  Java " + got.major + "（MineLib 自带）";
            SetStatus("Java " + got.major + " 已就绪，放在程序目录的 MineLib\\java 下", false);
        }
        catch (Exception ex)
        {
            SetStatus("获取 Java 失败：" + ex.Message, false);
            AppendLog("获取 Java 失败：" + ex.Message);
        }
        finally
        {
            BtnFetchJava.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
        }
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

    private async void OnPickGameDirClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏目录（.minecraft）" };
        if (Directory.Exists(_cfg.GameDir)) dlg.InitialDirectory = _cfg.GameDir;
        if (dlg.ShowDialog() == true)
        {
            _cfg.GameDir = dlg.FolderName;
            _cfg.Clients.Clear();      // 新根目录：重新按磁盘上的版本登记
            _cfg.Version = "";
            _cfg.Save();
            ValGameDir.Text = _cfg.GameDir;
            await RefreshVersionsAsync();
            _ = DetectJavaAsync();
        }
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e) => OpenFolder(_cfg.GameDir);

    private void OnOpenVersionDirClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetStatus("先选一个版本。", false); return; }
        OpenFolder(_current.Dir);
    }

    private void OnGoAccountClick(object sender, RoutedEventArgs e) => ShowPane("account");

    private void OnGoLogClick(object sender, RoutedEventArgs e) { ShowPane("client"); Tabs.SelectedIndex = 1; }

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
            LblThreadsHint.Text = n.ToString();
        }
    }

    // ---------------------------------------------------------------- 设置页
    /// <summary>兼容旧的页签编号：0 启动 / 1 账号 / 2 日志 / 3 下载 / 4 模组 / 5 设置。</summary>
    private void ApplyTabIndex(int idx)
    {
        switch (Math.Clamp(idx, 0, 5))
        {
            case 1: ShowPane("account"); break;
            case 2: ShowPane("client"); Tabs.SelectedIndex = 1; break;
            case 3: ShowPane("download"); DownloadTabs.SelectedIndex = 0; break;
            case 4: ShowPane("download"); DownloadTabs.SelectedIndex = 1; break;
            case 5: ShowPane("settings"); break;
            default: ShowPane("client"); Tabs.SelectedIndex = 0; break;
        }
    }

    private void ApplyWindowTitle()
    {
        var t = string.IsNullOrWhiteSpace(_cfg.WindowTitle) ? "MineLib 启动器" : _cfg.WindowTitle.Trim();
        Title = t;
        LblTitleBrand.Text = t;
    }

    private void OnTitleChanged(object sender, RoutedEventArgs e)
    {
        var t = TxtWindowTitle.Text.Trim();
        if (t == _cfg.WindowTitle) return;
        _cfg.WindowTitle = t;
        _cfg.Save();
        ApplyWindowTitle();
        SetStatus(t.Length == 0 ? "标题已清空，使用默认标题" : "标题已改为「" + t + "」", false);
    }

    private void OnDownloadSourceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingLoaderCombos) return;
        var id = RbSrcBmclapi.IsChecked == true ? DownloadSource.Bmclapi.Id : DownloadSource.Official.Id;
        if (id == _cfg.DownloadSourceId) return;
        _cfg.DownloadSourceId = id;
        _cfg.Save();
        DownloadSources.Apply(id);
        SetStatus("下载源已切换到「" + DownloadSources.Current.Name + "」", false);
    }

    private async void OnSpeedTestClick(object sender, RoutedEventArgs e)
    {
        BtnSpeedTest.IsEnabled = false;
        BtnSpeedTest.Content = "测速中…";
        LblSpeedOfficial.Text = "正在测…";
        LblSpeedBmclapi.Text = "正在测…";
        try
        {
            var results = await Task.WhenAll(DownloadSource.All.Select(async s =>
            {
                var r = await s.ProbeAsync(CancellationToken.None);
                return (s, r);
            }));
            foreach (var (s, r) in results)
            {
                var text = (r.Ok ? "" : "失败 · ") + r.Detail;
                if (s.Id == DownloadSource.Official.Id) LblSpeedOfficial.Text = text;
                else LblSpeedBmclapi.Text = text;
            }
            var best = results.Where(x => x.r.Ok)
                .OrderByDescending(x => x.r.Kbps)
                .Select(x => x.s.Name).FirstOrDefault();
            SetStatus(best is null ? "两个下载源都没测通，检查网络" : "测速完成，当前更快的是「" + best + "」", false);
        }
        catch (Exception ex)
        {
            SetStatus("测速失败：" + ex.Message, false);
        }
        finally
        {
            BtnSpeedTest.Content = "测速";
            BtnSpeedTest.IsEnabled = true;
        }
    }

    private void OnOpenConfigDirClick(object sender, RoutedEventArgs e) => OpenFolder(AppConfig.Dir);

    private void OnOpenRepoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/MineLib-Initialization/MineLib-Launcher")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("打不开浏览器：" + ex.Message, false);
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
            ShowPane("account");
            SetStatus("还没有账号。在「账号」页选一种登录方式后再启动。", false);
            return;
        }

        SaveSettings();
        BtnLaunch.IsEnabled = false;
        SetStatus("准备启动…", true);
        _cts = new CancellationTokenSource();

        try
        {
            var needMajor = _merged["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21;
            if (string.IsNullOrEmpty(_javaExe) || _javaMajor != needMajor)
            {
                if (string.IsNullOrEmpty(_javaExe)) await DetectJavaAsync();
                if (string.IsNullOrEmpty(_javaExe) || _javaMajor != needMajor)
                {
                    var jp = new Progress<InstallProgress>(p =>
                    {
                        LblStatus.Text = p.Stage;
                        Bar.Visibility = Visibility.Visible;
                        Bar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
                    });
                    var got = await JavaProvisioner.EnsureAsync(needMajor, jp,
                        AppendLog, _cts.Token);
                    _javaExe = got.exe;
                    _javaMajor = got.major;
                    ValJava.Text = got.exe + "  ·  Java " + got.major + "（MineLib 自带）";
                    LblStatusRight.Text = "Java " + got.major + " · 内存 " + _cfg.MemoryMb + " MB";
                }
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

            if (!string.IsNullOrEmpty(account.Authlib))
            {
                SetStatus("准备 authlib-injector…", true);
                try { await YggdrasilAuth.EnsureInjectorAsync(_cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppendLog("authlib-injector 准备失败：" + ex.Message); }
            }

            var runDir = _cfg.Isolation ? _current.Dir : _cfg.GameDir;
            var (args, _) = GameLauncher.BuildCommand(_cfg.GameDir, _current.Id, _merged, account,
                _javaExe, _cfg.MemoryMb, _cfg.JvmArgs, runDir);

            _logPath = Path.Combine(_cfg.GameDir, "logs",
                "minelib-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            LblLogPath.Text = _logPath;
            AppendLog("启动参数：" + string.Join(" ", args.Take(Math.Min(8, args.Count))) + " …");
            AppendLog("日志文件：" + _logPath);

            _proc = new GameProcess();
            _proc.LineReceived += AppendLog;
            _proc.Exited += code => Dispatcher.BeginInvoke(() =>
            {
                BtnLaunch.IsEnabled = true;
                BtnStop.Visibility = Visibility.Collapsed;
                SetStatus(code == 0 ? "游戏已退出" : "游戏异常退出，退出码 " + code, false);
                ShowPane("client");
                Tabs.SelectedIndex = 1;
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
            ShowPane("client");
            Tabs.SelectedIndex = 1;
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
    // 游戏和安装器一秒能吐上千行。以前每行都直接 AppendText + ScrollToEnd，还要读一次
    // TxtLog.Text 判断长度；行一多、日志一长，UI 线程就一直在排版和拷贝字符串，窗口表现为
    // 卡顿甚至「无响应」。现在任意线程只把日志塞进队列，UI 线程每 100ms 合并刷一次。
    private readonly object _logLock = new();
    private readonly Queue<string> _logPending = new();
    private readonly Queue<string> _recent = new();
    private readonly DispatcherTimer _logTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    private int _logDropped;
    private int _logChars;
    private const int MaxPendingLogLines = 2000;
    private const int LogTrimAt = 400_000;
    private const int LogKeepAfterTrim = 200_000;

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_logLock)
        {
            if (_logPending.Count >= MaxPendingLogLines)
            {
                _logPending.Dequeue();
                _logDropped++;
            }
            _logPending.Enqueue(line);
            _recent.Enqueue(line.Length > 160 ? line[..160] + "…" : line);
            while (_recent.Count > 6) _recent.Dequeue();
        }
    }

    private void FlushLog()
    {
        string batch;
        string recent;
        lock (_logLock)
        {
            if (_logPending.Count == 0) return;
            var sb = new StringBuilder(_logPending.Count * 80 + 64);
            if (_logDropped > 0)
            {
                sb.Append("…（日志过多，已省略 ").Append(_logDropped).AppendLine(" 行）");
                _logDropped = 0;
            }
            while (_logPending.Count > 0) sb.AppendLine(_logPending.Dequeue());
            batch = sb.ToString();
            recent = string.Join(Environment.NewLine, _recent);
        }

        TxtLog.AppendText(batch);
        _logChars += batch.Length;
        if (_logChars > LogTrimAt)
        {
            // 只在越过阈值时整体裁一次；以前每来一行都要读一遍 TxtLog.Text 并重建控件
            var text = TxtLog.Text;
            var cut = Math.Max(0, text.Length - LogKeepAfterTrim);
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            var nl = text.IndexOf('\n', Math.Min(cut, text.Length - 1));
            if (nl >= 0) cut = nl + 1;
            TxtLog.Text = text[cut..];
            _logChars = text.Length - cut;
        }
        TxtLog.ScrollToEnd();
        TxtHomeLog.Text = recent;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        lock (_logLock)
        {
            _logPending.Clear();
            _recent.Clear();
            _logDropped = 0;
        }
        TxtLog.Clear();
        _logChars = 0;
        TxtHomeLog.Text = "";
    }

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

    // ---------------------------------------------------------------- 更新
    private void ShowAppliedNotes()
    {
        var applied = Updates.TakeAppliedNotes();
        if (applied is null) return;
        var body = string.IsNullOrWhiteSpace(applied.Notes)
            ? "已更新到 v" + applied.Version + "。"
            : "本次更新内容：" + Environment.NewLine + Environment.NewLine + applied.Notes;
        AppendLog("已更新到 v" + applied.Version + "：" + applied.Notes);
        LblUpdateState.Text = "v" + applied.Version + " 已生效";
        MessageBox.Show(body, "已更新到 v" + applied.Version + " (" + Updates.Current + ")",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private Updates.UpdateInfo? _pendingUpdate;

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (manual) SetStatus("检查更新…", true);
        try
        {
            var info = await Updates.CheckAsync(CancellationToken.None);
            if (info is null)
            {
                LblUpdateState.Text = "已是最新（" + Updates.Current + "）";
                if (manual) SetStatus("当前已是最新版本 " + Updates.Current, false);
                return;
            }

            _pendingUpdate = info;
            LblUpdateState.Text = "发现新版本 " + info.Version + "，后台下载中…";
            SetStatus("发现新版本 " + info.Version + "，正在后台下载", false);

            var progress = new Progress<InstallProgress>(p =>
            {
                LblUpdateState.Text = p.Stage;
                if (manual) Bar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
            });
            await Updates.StageAsync(info, progress,
                AppendLog, CancellationToken.None);
            LblUpdateState.Text = "新版本 " + info.Version + " 已就绪，下次启动自动更新";
            SetStatus("更新已下载，" + info.Version + " 将在下次启动时生效", false);
            BtnCheckUpdate.Content = "重启并更新";
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Split((char)10)[0];
            LblUpdateState.Text = "检查更新失败：" + msg;
            if (manual) SetStatus("检查更新失败：" + msg, false);
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (Updates.HasStagedUpdate)
        {
            if (Updates.TryApplyStagedAndExit())
            {
                Close();
                return;
            }
        }
        BtnCheckUpdate.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        try { await CheckUpdatesAsync(true); }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- 客户端（= versions 下的版本）
    /// <summary>新建客户端：切到下载页，装进当前游戏根目录。</summary>
    private void OnNewInstanceClick(object sender, RoutedEventArgs e)
    {
        ShowPane("download");
        SetStatus("在右边填名字、选版本和加载器，点「创建新客户端」——会装进 " + _cfg.GameDir, false);
    }

    /// <summary>删除选中的客户端：只删这个版本目录，共用文件（libraries/assets）不动。</summary>
    private async void OnDeleteClientClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            SetStatus("没有选中的客户端。", false);
            return;
        }
        var client = _cfg.FindClient(_current.Id);
        var name = client?.Name ?? _current.Id;
        if (_proc is { Running: true })
        {
            SetStatus("游戏正在运行，先关掉再删。", false);
            return;
        }

        var vdir = _current.Dir;
        var first = MessageBox.Show(
            "从列表里移除客户端「" + name + "」？" + Environment.NewLine + Environment.NewLine +
            "版本：" + _current.Id + Environment.NewLine + "目录：" + vdir + Environment.NewLine + Environment.NewLine +
            "点「是」先移除，之后会再问要不要删磁盘文件。libraries 与 assets 是共用的，不会被删。",
            "删除客户端", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;

        _cfg.RemoveClient(_current.Id);
        if (string.Equals(_cfg.Version, _current.Id, StringComparison.OrdinalIgnoreCase)) _cfg.Version = "";
        _cfg.Save();
        await RefreshVersionsAsync();
        SetStatus("已移除客户端「" + name + "」", false);

        if (!Directory.Exists(vdir)) return;
        var second = MessageBox.Show(
            "连磁盘上的版本文件一起删掉吗？" + Environment.NewLine + Environment.NewLine + vdir +
            Environment.NewLine + Environment.NewLine + "这个版本的存档、模组、配置都会没有，不可恢复。",
            "删除版本文件？", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
        if (second != MessageBoxResult.Yes) return;
        try
        {
            Directory.Delete(vdir, true);
            await RefreshVersionsAsync();
            SetStatus("已删除 " + vdir, false);
        }
        catch (Exception ex)
        {
            SetStatus("删除失败（文件可能被占用）：" + ex.Message, false);
        }
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

    private int _modSearchSeq;

    private async Task RunModSearchAsync(int offset)
    {
        // 连续触发（切页签 + 手动搜索）时，只让最后一次的结果落地，否则慢的那次会盖掉新的
        var seq = ++_modSearchSeq;
        _modOffset = Math.Max(0, offset);
        LblModStatus.Text = "检索中…";
        try
        {
            var (hits, total) = await Modrinth.SearchAsync(ModSearchBox.Text.Trim(), ModMcBox.Text.Trim(),
                SelectedModLoader, SelectedSortKey, _modOffset, 20, CancellationToken.None);
            if (seq != _modSearchSeq) return;
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
            if (seq == _modSearchSeq) LblModStatus.Text = "检索失败：" + ex.Message;
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

    // ---------------------------------------------------------------- 全局页签
    private string _pane = "client";

    private void OnGlobalTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id) ShowPane(id);
    }

    private void ShowPane(string id)
    {
        _pane = id;
        var pane = id switch
        {
            "download" => (FrameworkElement)PaneDownload,
            "account" => PaneAccount,
            "settings" => PaneSettings,
            _ => PaneClient
        };
        var button = id switch
        {
            "download" => BtnTabDownload,
            "account" => BtnTabAccount,
            "settings" => BtnTabSettings,
            _ => BtnTabClient
        };

        foreach (var p in new FrameworkElement[] { PaneClient, PaneDownload, PaneAccount, PaneSettings })
            if (!ReferenceEquals(p, pane)) p.Visibility = Visibility.Collapsed;

        pane.Visibility = Visibility.Visible;
        AnimateIn(pane);
        AnimateIndicator(button);

        foreach (var b in new[] { BtnTabClient, BtnTabDownload, BtnTabAccount, BtnTabSettings })
            b.Tag = ReferenceEquals(b, button) ? "active" : "";

        if (id == "download" && _official.Count == 0) _ = LoadOfficialAsync();
    }

    /// <summary>页面切换：淡入 + 轻推，Material 风格的那种过渡。</summary>
    private static void AnimateIn(FrameworkElement pane)
    {
        // 基准值先设成动画的终点：即使动画没跑（比如离屏渲染），界面也是最终状态，不会停在透明
        pane.Opacity = 1;
        var shift = new System.Windows.Media.TranslateTransform(0, 0);
        pane.RenderTransform = shift;
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        pane.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1,
            TimeSpan.FromMilliseconds(190)) { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240))
            { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }

    /// <summary>底部指示条滑到选中的页签下面。</summary>
    private void AnimateIndicator(FrameworkElement target)
    {
        try
        {
            var to = target.TransformToAncestor(TabStripRoot).Transform(new Point(0, 0)).X;
            TabIndicator.Width = target.ActualWidth;
            var ease = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            };
            TabIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(240))
                { EasingFunction = ease });
        }
        catch (Exception) { }
    }

    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DownloadTabs))
        {
            if (DownloadTabs.SelectedIndex == 0 && _official.Count == 0) await LoadOfficialAsync();
            if (DownloadTabs.SelectedIndex == 1) InitModTab();
        }
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
                }
            }
            _official = list;
            if (VerTypeCombo.ItemsSource is null)
            {
                VerTypeCombo.ItemsSource = new[] { "正式版", "快照", "全部" };
                VerTypeCombo.SelectedIndex = 0;
            }
            ApplyOfficialFilter();
            LblCreateStatus.Text = "官方清单共 " + _official.Count + " 个版本（含快照）";
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

        var loaderLabel = NewLoaderCombo.SelectedItem as string ?? "不装（纯原版）";
        var kind = ClientCreator.ParseLoader(loaderLabel);
        var javaExe = "";
        if (kind is not null && Loaders.NeedsJava(kind.Value))
        {
            var needMajor = 21;
            try { needMajor = Versions.Resolve(_cfg.GameDir, v.Id)["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(_javaExe) || _javaMajor != needMajor)
            {
                LblCreateStatus.Text = "准备 Java " + needMajor + "…";
                var jp = new Progress<InstallProgress>(p => LblCreateStatus.Text = p.Stage);
                try
                {
                    var got = await JavaProvisioner.EnsureAsync(needMajor, jp,
                        AppendLog, CancellationToken.None);
                    _javaExe = got.exe;
                    _javaMajor = got.major;
                }
                catch (Exception ex)
                {
                    LblCreateStatus.Text = "获取 Java 失败：" + ex.Message;
                    return;
                }
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
            var result = await ClientCreator.CreateAsync(_cfg, NewNameBox.Text, v.Id,
                loaderLabel, _cfg.DownloadThreads, javaExe, progress,
                AppendLog, _cts.Token);

            ValGameDir.Text = _cfg.GameDir;
            await RefreshVersionsAsync();
            var made = _all.FirstOrDefault(x => x.Id == result.VersionId);
            if (made is not null)
            {
                VersionList.SelectedItem = made;
                ShowVersion(made);
            }
            LblCreateStatus.Text = "创建完成：「" + result.Name + "」（" + result.Note + "），已加到客户端列表。";
            SetStatus("客户端「" + result.Name + "」已创建", false);
        }
        catch (OperationCanceledException)
        {
            LblCreateStatus.Text = "已取消。已下载的文件会保留，同名重建会跳过它们。";
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

    private void OnVersionFilterChanged(object? sender, EventArgs e) => ApplyOfficialFilter();

    private void ApplyOfficialFilter()
    {
        var type = (VerTypeCombo.SelectedItem as string) ?? "正式版";
        var q = (VerSearchBox?.Text ?? "").Trim();
        var view = _official.Where(v =>
        {
            if (type == "正式版" && v.Type != "release") return false;
            if (type == "快照" && v.Type == "release") return false;
            if (q.Length > 0 && v.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }).ToList();

        OfficialList.ItemsSource = null;
        OfficialList.ItemsSource = view;
        if (view.Count > 0) OfficialList.SelectedIndex = 0;
        LblVerCount.Text = "共 " + view.Count + " 个";
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => ShowPane("download");

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