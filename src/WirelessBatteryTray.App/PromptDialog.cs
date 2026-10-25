using System.Windows;
using System.Windows.Controls;

namespace WirelessBatteryTray.App;

/// <summary>一个最小的文本输入对话框（重命名用），不引入任何第三方 UI 库。</summary>
public sealed class PromptDialog : Window
{
    private readonly TextBox _textBox;

    public PromptDialog(string title, string prompt, string initialValue)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");

        _textBox = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 6, 0, 14),
            Padding = new Thickness(6, 4, 6, 4),
        };
        _textBox.SelectAll();

        var ok = new Button { Content = "确定", Width = 78, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 78, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(_textBox);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _textBox.Focus();
    }

    public string Value => _textBox.Text.Trim();
}
