using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MineLib.Core;

/// <summary>
/// 窗口外观里必须交给系统的部分：圆角、暗色标题栏。
///
/// 以前用 `AllowsTransparency=True` 自己画圆角，代价是窗口变成 layered window：
/// 整窗走一遍位图合成，文字在高 DPI 下容易发虚，而且自己裁的圆角和系统的阴影/贴边
/// 经常对不齐（就是"衔接"处看着别扭的来源）。现在改成普通窗口 + DWM 圆角。
/// </summary>
public static class WindowFx
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;

    /// <summary>Windows 11（含）以上，DWM 才支持圆角。</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>系统是不是深色模式（只影响我们画的标题栏配色）。</summary>
    public static bool SystemUsesDarkTheme
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return (key?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// 应用系统级圆角 + 暗色标题栏。返回 true 表示圆角交给 DWM，
    /// 调用方就不该再自己裁剪窗口（否则两套圆角会互相咬出缝）。
    /// </summary>
    public static bool ApplyDwmChrome(Window window)
    {
        var rounded = false;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            if (IsWindows11)
            {
                var round = 2;   // DWMWCP_ROUND
                rounded = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round,
                    Marshal.SizeOf<int>()) == 0;
            }
            var dark = SystemUsesDarkTheme ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, Marshal.SizeOf<int>());
        }
        catch (Exception) { }
        return rounded;
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
