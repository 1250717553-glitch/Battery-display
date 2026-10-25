using System;
using System.Linq;
using System.Reflection;
using WirelessBatteryTray.Core;

namespace WirelessBatteryTray.App;

/// <summary>
/// 正式启动路径：不 new 任何具体 provider。
/// 只在已加载程序集里发现 IDeviceService 的无参实现并装配到 AppServices.Device。
/// Core 侧 provider 未就绪时返回 null，界面以空列表运行（绝不伪造数据）。
/// </summary>
internal static class ServiceBootstrap
{
    public static IDeviceService? TryAttach(ILog log)
    {
        // AppDomain.GetAssemblies() 只包含「已加载」的程序集：Core 若尚未被任何成员引用，
        // 它就不在其中，DeviceService 会永远发现不了。因此这里对 Core 做一次显式类型触达。
        var coreAssembly = typeof(IDeviceService).Assembly;
        _ = coreAssembly.GetTypes();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Concat(new[] { coreAssembly })
            .Distinct()
            .ToList();

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch (Exception ex) when (ex is NotSupportedException or System.IO.FileNotFoundException)
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || !type.IsClass || !typeof(IDeviceService).IsAssignableFrom(type))
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    log.Warn("发现 IDeviceService 实现但没有无参构造，跳过：" + type.FullName);
                    continue;
                }

                try
                {
                    // 只装配，不启动：生命周期由 App 统一负责（避免 Start 被调用两次、跑出重复刷新循环）。
                    var service = (IDeviceService)Activator.CreateInstance(type)!;
                    log.Info("已装配设备服务：" + type.FullName);
                    return service;
                }
                catch (Exception ex)
                {
                    log.Error("装配设备服务失败：" + type.FullName, ex);
                }
            }
        }

        return null;
    }
}
