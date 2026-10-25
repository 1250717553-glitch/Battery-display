using System;
using System.Linq;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace WirelessBatteryTray.App;

/// <summary>跟随系统深浅色：同时切换本应用色板与 MDIX 的 BaseTheme。</summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>App.xaml 中本应用色板的字典索引（0=BundledTheme, 1=MDIX Defaults, 2=Our Palette）。</summary>
    private const int ThemeSlot = 2;

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int light)
            {
                return light == 0;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // 读不到就按浅色处理，不抛。
        }

        return false;
    }

    public static void ApplyFromSystem() => Apply(IsSystemDark());

    public static void Apply(bool dark)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Resources.MergedDictionaries.Count <= ThemeSlot)
        {
            return;
        }

        // MDIX 的 BaseTheme 决定控件（按钮/输入框/滚动条/阴影）的观感，
        // 我们的色板决定卡片与文字。两者必须一起切，否则会出现「深色卡片配浅色控件」。
        ApplyMaterialBaseTheme(dark);

        var source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var slot = app.Resources.MergedDictionaries[ThemeSlot];
        if (slot.Source == source)
        {
            return;
        }

        slot.Source = source;
    }

    private static void ApplyMaterialBaseTheme(bool dark)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var theme = app.Resources.MergedDictionaries.OfType<BundledTheme>().FirstOrDefault();
        if (theme is null)
        {
            return;
        }

        theme.BaseTheme = dark ? BaseTheme.Dark : BaseTheme.Light;
    }
}
