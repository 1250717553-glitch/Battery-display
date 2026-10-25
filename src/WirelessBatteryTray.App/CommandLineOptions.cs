using System;

namespace WirelessBatteryTray.App;

/// <summary>最小命令行解析：唯一启用假数据的开关是 --demo（或 WBT_DEMO=1）。</summary>
public sealed class CommandLineOptions
{
    public bool Demo { get; private set; }

    /// <summary>demo 设备的序列号段；改变它会改变 StableId（用于证明「别名按 StableId 存」）。</summary>
    public string? DemoSerial { get; private set; }

    /// <summary>demo 设备的易变段（模拟重插/换口）；只改系统名，不改 StableId。</summary>
    public string? DemoReplug { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Equals("--demo", StringComparison.OrdinalIgnoreCase))
            {
                options.Demo = true;
            }
            else if (TryValue(arg, "--demo-serial", out var serial))
            {
                options.DemoSerial = serial;
            }
            else if (TryValue(arg, "--demo-replug", out var replug))
            {
                options.DemoReplug = replug;
            }
        }

        if (!options.Demo && IsTruthy(Environment.GetEnvironmentVariable("WBT_DEMO")))
        {
            options.Demo = true;
        }

        options.DemoSerial ??= Environment.GetEnvironmentVariable("WBT_DEMO_SERIAL");
        options.DemoReplug ??= Environment.GetEnvironmentVariable("WBT_DEMO_REPLUG");
        return options;
    }

    private static bool TryValue(string arg, string prefix, out string? value)
    {
        value = null;
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = arg.Substring(prefix.Length);
        if (rest.StartsWith('=') || rest.StartsWith(':'))
        {
            value = rest.Substring(1).Trim();
            return true;
        }

        return false;
    }

    private static bool IsTruthy(string? text) =>
        text is not null && (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase));
}
