# 电池显示 · Battery Display

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)]()

**中文** | [English](README.en.md)

一个常驻托盘的 Windows 小工具，在通知区域显示 **2.4G 无线接收器** 与
**蓝牙外设**（鼠标 / 键盘 / 耳机）的电量和连接状态。

![托盘图标状态](docs/screenshots/tray-icon.png)

---

## 功能特性

### 托盘图标实时显示电量

- Win11 风格竖条电池，32×32 绘制：填充高度 = 电量百分比，数字画在电池壳内
- 配色三档：绿色（>20%）/ 橙色（≤20%）/ 红色（报警或 ≤10%）
- 字号自适应：`100%` 时自动缩小，不会贴到边框
- 最小填充 4.5px，电量再低也看得出是电池而不是空框
- 读不到电量显示灰色问号，**绝不显示 0%**
- **多设备**：图标显示「最需要关注」的一台（报警优先，其次电量最低），
  右下角红色角标表示还有几台在线设备

### 设备列表窗口

- Material 卡片列表，深浅色跟随系统
- 按设备稳定身份保存别名，重连或换 USB 口后依然有效
- 已配对但离线的设备仍会列出，电量标注「上次已知 N%」
- 读不到电量显示「未知」，不显示 0%

### 低电量报警

- 阈值可调（默认 20%），重置阈值可调（默认 25%）
- **滞回防打扰**：跌破阈值只提醒一次，回升到重置值或重连后才会再次提醒
- 充电中不报警
- 报警动作：系统提示音 + 托盘图标变红 + 界面数字变红
- 可静音但保留视觉提醒
- 点窗口右上角齿轮即可调整，无需手动改配置文件

### 快捷面板

- 左键点托盘图标，贴着图标上方弹出在线设备面板
- 不抢焦点，点击别处自动收起

---

## 系统要求

- Windows 10 1809+ / Windows 11
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)

## 构建与运行

```powershell
dotnet build WirelessBatteryTray.sln -c Release
.\src\WirelessBatteryTray.App\bin\Release\net8.0-windows10.0.19041.0\WirelessBatteryTray.App.exe
```

启动后最小化到托盘：左键点图标弹出快捷面板，右键打开菜单。

## 配置文件

配置与日志位于 `%LOCALAPPDATA%\WirelessBatteryTray\`。

---

## 关于 2.4G 电量

**多数接收器在不发送厂商指令的情况下不会上报电量。**

本项目刻意**不发送厂商指令**——错误报文可能让设备进入异常状态。

HID 提供程序以只读方式打开每个顶层集合，解析设备主动推送的内容。若接收器
从不汇报电量（常见情况），则降级为「已连接，电量未知」。蓝牙设备改用标准
GATT Battery Service（`0x180F`），多数 BLE 外设可用。

适配新的接收器只需实现一个方法：

```csharp
// Providers/Hid24GhzProvider.cs
public static bool TryParseBatteryFromInputReport(byte[] report, out int percent)
```

---

## 截图

| 主界面 | 鲸鱼娘气泡（可选集成） |
|---|---|
| ![主界面](docs/screenshots/main-window.png) | ![气泡](docs/screenshots/whale-bubble.png) |

## 项目结构

```
src/
  WirelessBatteryTray.Core/   设备抽象层（不依赖 UI）
    Contracts.cs              IDeviceProvider / IDeviceService 契约
    DeviceService.cs          聚合、缓存、刷新策略
    Providers/                HID（2.4GHz）与蓝牙实现
    LowBatteryMonitor.cs      滞回报警逻辑
    TaskbarHelper.cs          任务栏几何与 DPI 处理
    WhaleBridge.cs            可选的浏览器挂件 JSON 桥
  WirelessBatteryTray.App/    WPF 托盘程序与界面
  BatteryProbe/               只读 HID 诊断 CLI
```

## 诊断工具

`BatteryProbe` 为只读工具，**绝不向设备写入数据**。

```powershell
# 列出 HID 接口并采样输入报文
dotnet run --project src/BatteryProbe -c Release -- --duration 10

# 运行全部自检（离线降级 / 报警滞回 / 任务栏几何）
dotnet run --project src/BatteryProbe -c Release -- --diagnose
```

## 许可

MIT — 见 [LICENSE](LICENSE)。Copyright (c) 2026 1250717553-glitch.
