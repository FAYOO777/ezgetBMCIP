using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Wpf.Ui.Appearance;

namespace EzGetBmcIp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("未处理异常：" + ex.Exception,
                "ezgetBMCIP", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        if (!IsAdministrator())
        {
            RestartAsAdministrator();
            Shutdown();
            return;
        }

        // 跟随系统亮/暗主题
        ApplicationThemeManager.ApplySystemTheme();

        base.OnStartup(e);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "程序需要管理员权限才能配置网卡和启动 DHCP 服务。\r\n\r\n" + ex.Message,
                "需要管理员权限",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
