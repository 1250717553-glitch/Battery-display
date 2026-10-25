using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WirelessBatteryTray.Core;

namespace WirelessBatteryTray.App;

/// <summary>
/// 设备列表窗口（Win11 小组件风格卡片列表）。
/// 关闭只隐藏，托盘仍在；顶部空白可拖动窗口。
/// </summary>
public partial class DeviceListWindow : Window
{
    private readonly ObservableCollection<DeviceRow> _rows = new();
    private AppConfig _config;
    private bool _loading;

    /// <summary>查询「该设备是否处于低电量报警状态」，由 App 注入。</summary>
    private Func<string, bool>? _alertSource;

    public void SetAlertSource(Func<string, bool> source) => _alertSource = source;

    public DeviceListWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        DeviceList.ItemsSource = _rows;
        Loaded += async (_, _) => await ReloadAsync(readBatteries: true).ConfigureAwait(true);

        // 尺寸必须按「当前显示器的逻辑工作区」算，不能写死。
        // 本机是 1440x900 物理像素 + 200% 缩放 => 逻辑只有 720x450 DIP，
        // 写死 800 宽会把窗口顶出屏幕，右侧内容被裁掉。
        FitToWorkArea();
    }

    /// <summary>
    /// 可能被托盘（后台线程）间接调用，因此显式编组到本窗口的 Dispatcher。
    /// DependencyObject 只能在拥有它的线程上访问。
    /// </summary>
    public void UpdateConfig(AppConfig config)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => UpdateConfig(config));
            return;
        }

        _config = config;
        Rebuild(_rows.Select(r => r.Snapshot).ToArray());
    }

    /// <summary>D4：用户主动查看时读一次真值；其余时间用缓存。</summary>
    public async Task ReloadAsync(bool readBatteries)
    {
        if (_loading)
        {
            return;
        }

        var device = AppServices.Device;
        if (device is null)
        {
            StatusLine.Text = "设备服务尚未装配";
            SubStatusLine.Text = "未发现可用的 IDeviceService 实现";
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await device.GetSnapshotAsync(readBatteries).ConfigureAwait(true);
            Rebuild(snapshot);
            StatusLine.Text = "共 " + snapshot.Count + " 个设备";
            SubStatusLine.Text = DateTime.Now.ToString("HH:mm:ss")
                + (readBatteries ? " · 已主动读取" : " · 缓存");
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("设备列表读取快照失败", ex);
            StatusLine.Text = "读取失败";
            SubStatusLine.Text = ex.Message;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private static int Rank(DeviceSnapshot device) => device.Connection switch
    {
        ConnectionStatus.Connected => BatteryDisplay.HasPercent(device) ? 0 : 1,
        _ => 2,
    };

    private void Rebuild(IReadOnlyList<DeviceSnapshot> snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Rebuild(snapshot));
            return;
        }

        var now = DateTimeOffset.Now;
        var selected = (DeviceList.SelectedItem as DeviceRow)?.StableId;

        _rows.Clear();
        // 排序：有电量的在前（低电量更前），其次在线未知，最后离线。
        foreach (var device in snapshot
                     .OrderBy(Rank)
                     .ThenBy(d => BatteryDisplay.HasPercent(d) ? 0 : 1)
                     .ThenBy(d => d.Battery?.Percent ?? int.MaxValue)
                     .ThenBy(d => d.SystemName, StringComparer.CurrentCulture))
        {
            var row = new DeviceRow(device, _config, now);

            // 报警状态由 TrayIconController 统一持有（它负责滞回与去重），
            // 窗口只负责把它映射到视觉。
            row.IsLowBatteryAlerted = _alertSource?.Invoke(device.StableId) ?? false;
            _rows.Add(row);
        }

        if (selected is not null)
        {
            DeviceList.SelectedItem = _rows.FirstOrDefault(r => r.StableId == selected);
        }
    }

    // ---------- 窗口外观与拖动 ----------

    /// <summary>把窗口尺寸限制在当前显示器的逻辑工作区内，并居中。</summary>
    private void FitToWorkArea()
    {
        TaskbarInfo taskbar = TaskbarHelper.Probe();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        // Monitor.Bounds 是**物理像素**；WPF 的 Width/Height/Left/Top 是设备无关单位(DIP)。
        // 必须先除以 DPI 缩放得到逻辑工作区，否则窗口会按物理像素尺寸被放大一倍，顶出屏幕。
        PhysicalRect mon = taskbar.Monitor.Bounds;
        double logicalWidth = mon.Width / scale;
        double logicalHeight = mon.Height / scale;
        double logicalLeft = mon.Left / scale;
        double logicalTop = mon.Top / scale;

        // 留出边距，并夹在逻辑工作区内。
        double targetWidth = Math.Max(MinWidth, Math.Min(logicalWidth - 48, logicalWidth * 0.80));
        double targetHeight = Math.Max(MinHeight, Math.Min(logicalHeight - 64, logicalHeight * 0.82));

        Width = targetWidth;
        Height = targetHeight;
        Left = logicalLeft + (logicalWidth - targetWidth) / 2;
        Top = logicalTop + (logicalHeight - targetHeight) / 2;

        MaxWidth = logicalWidth;
        MaxHeight = logicalHeight;

        AppServices.Log.Info("窗口尺寸适配 scale=" + scale
            + " 物理=" + mon.Width + "x" + mon.Height
            + " 逻辑工作区=" + logicalWidth.ToString("F0") + "x" + logicalHeight.ToString("F0")
            + " -> window=" + targetWidth.ToString("F0") + "x" + targetHeight.ToString("F0"));
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            // 无边框窗口的自定义拖动：DragMove 必须由鼠标左键按下触发。
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    // ---------- 报警设置 ----------

    private void OnToggleSettings(object sender, RoutedEventArgs e)
    {
        bool show = SettingsPanel.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            LoadSettingsIntoUi();
        }
    }

    /// <summary>把当前配置灌进设置控件（不触发业务逻辑）。</summary>
    private void LoadSettingsIntoUi()
    {
        var config = AppServices.Config.Load();

        // 保证合法范围：重置阈值必须大于报警阈值
        int low = Math.Clamp(config.LowBatteryThreshold, 1, 99);
        int reset = Math.Clamp(config.LowBatteryResetThreshold, low + 1, 100);

        LowThresholdSlider.Value = low;
        ResetThresholdSlider.Value = reset;
        SoundToggle.IsChecked = config.LowBatterySoundEnabled;

        UpdateThresholdLabels();
    }

    private void UpdateThresholdLabels()
    {
        int low = (int)LowThresholdSlider.Value;
        int reset = (int)ResetThresholdSlider.Value;

        LowThresholdText.Text = low + "%";
        ResetThresholdText.Text = reset + "%";

        // 重置阈值必须严格大于报警阈值，否则永远无法重置「已报警」状态。
        ResetThresholdSlider.Minimum = low + 1;
        if (ResetThresholdSlider.Value <= low)
        {
            ResetThresholdSlider.Value = low + 1;
        }
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 控件尚未初始化完成时会被调用，忽略即可
        if (LowThresholdText is null || ResetThresholdText is null)
        {
            return;
        }

        UpdateThresholdLabels();
    }

    private void OnTestSound(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("试听提示音失败：" + ex.Message);
        }
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var config = AppServices.Config.Load();

        int low = (int)LowThresholdSlider.Value;
        int reset = (int)ResetThresholdSlider.Value;
        if (reset <= low)
        {
            reset = low + 1;
        }

        config.LowBatteryThreshold = low;
        config.LowBatteryResetThreshold = reset;
        config.LowBatterySoundEnabled = SoundToggle.IsChecked == true;

        AppServices.Config.Save(config);

        AppServices.Log.Info("报警设置已保存：阈值=" + low + "% 重置=" + reset
            + "% 提示音=" + (config.LowBatterySoundEnabled ? "开" : "关"));

        // 让托盘立即用新配置重新评估（不必等下一轮刷新）
        AppServices.Device?.RequestRefresh();
        RefreshTray?.Invoke();

        HideSettingsWithToast();
    }

    private void HideSettingsWithToast()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SubStatusLine.Text = "报警设置已保存 · " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>由 App 注入：让托盘用新配置立即重算一次。</summary>
    public Action? RefreshTray;

    // ---------- 交互 ----------

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await ReloadAsync(readBatteries: true).ConfigureAwait(true);

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceRow)
        {
            RenameSelected();
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && DeviceList.SelectedItem is DeviceRow)
        {
            RenameSelected();
            e.Handled = true;
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameSelected();

    private void OnResetNameClick(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow row)
        {
            return;
        }

        // key 就是 StableId 原值：不缩短、不格式化、不哈希。
        if (!_config.Aliases.Remove(row.StableId))
        {
            return;
        }

        AppServices.Config.Save(_config);
        AppServices.Log.Info("清除别名 StableId=" + row.StableId);
        row.DisplayName = row.SystemName;
        Rebuild(_rows.Select(r => r.Snapshot).ToArray());
    }

    private void RenameSelected()
    {
        if (DeviceList.SelectedItem is not DeviceRow row)
        {
            return;
        }

        var dialog = new PromptDialog("重命名设备", "本机别名（留空则恢复系统名）：", row.DisplayName)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var alias = dialog.Value;
        if (string.IsNullOrWhiteSpace(alias) || alias == row.SystemName)
        {
            _config.Aliases.Remove(row.StableId);
        }
        else
        {
            // 持久化 key = StableId 原值，重连后由 Core 重新生成同一 StableId 即可命中。
            _config.Aliases[row.StableId] = alias;
        }

        AppServices.Config.Save(_config);
        AppServices.Log.Info("写入别名 StableId=" + row.StableId + " alias=" + alias);

        row.DisplayName = string.IsNullOrWhiteSpace(alias) ? row.SystemName : alias;
        Rebuild(_rows.Select(r => r.Snapshot).ToArray());
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭窗口不退出程序：托盘仍在。
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
