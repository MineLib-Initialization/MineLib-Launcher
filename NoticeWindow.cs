using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MineLib;

/// <summary>
/// 启动阶段就出问题时的兜底提示窗口。自己画，不用系统对话框（系统那个大叹号太突兀）。
/// </summary>
internal static class NoticeWindow
{
    public static void ShowError(string title, string body, string? logPath)
    {
        try
        {
            var bg = Brush("Bg", Color.FromRgb(0xF5, 0xF6, 0xF8));
            var panel = Brush("Panel", Colors.White);
            var borderStrong = Brush("BorderStrong", Color.FromRgb(0xD5, 0xD8, 0xDE));
            var text = Brush("Text", Color.FromRgb(0x1F, 0x23, 0x29));
            var dim = Brush("TextDim", Color.FromRgb(0x5A, 0x62, 0x6D));
            var accent = Brush("Accent", Color.FromRgb(0x35, 0x61, 0x8E));

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = text
            });
            stack.Children.Add(new TextBlock
            {
                Text = body, FontSize = 12.5, Foreground = dim, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0), LineHeight = 20
            });
            if (!string.IsNullOrEmpty(logPath))
                stack.Children.Add(new TextBlock
                {
                    Text = "日志：" + logPath, FontSize = 11.5, Foreground = dim,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
                });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var copy = MakeButton("复制日志路径", panel, dim);
            var close = MakeButton("关闭", accent, Brushes.White);
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            stack.Children.Add(buttons);

            var card = new Border
            {
                Background = panel,
                BorderBrush = borderStrong,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 18, 20, 18),
                Child = stack
            };

            var win = new Window
            {
                Title = "MineLib",
                Width = 470,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = true,
                Content = new Border { Background = bg, Child = card, Padding = new Thickness(12) }
            };

            close.Click += (_, _) => win.Close();
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(logPath ?? body); } catch (Exception) { }
            };
            win.MouseLeftButtonDown += (_, e) =>
            {
                try { win.DragMove(); } catch (Exception) { }
            };
            win.ShowDialog();
        }
        catch (Exception)
        {
            // 连兜底窗口都起不来就安静退出，日志里已经有记录
        }
    }

    private static Button MakeButton(string text, Brush background, Brush foreground)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = background,
            Foreground = foreground,
            BorderBrush = background,
            BorderThickness = new Thickness(1),
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        if (Application.Current?.TryFindResource("Primary") is Style primary && background is not null)
        {
            // 只有主按钮用主题样式，次要按钮保持白底
        }
        return b;
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
