using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using Wpf.Ui.Appearance;

namespace EzGetBmcIp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        WireLogger();

        DispatcherUnhandledException += (_, ex) =>
        {
            AppLogger.Log("Unhandled exception: " + ex.Exception);
            MessageBox.Show("\u672a\u5904\u7406\u5f02\u5e38\uff1a" + ex.Exception,
                "IPMI/BMC 直连助手", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        if (!IsAdministrator())
        {
            AppLogger.Log("Not running as admin, restarting elevated");
            RestartAsAdministrator();
            Shutdown();
            return;
        }

        AppLogger.Log("Running as administrator");
        ApplicationThemeManager.ApplySystemTheme();
        base.OnStartup(e);
    }

    private static void WireLogger()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        AppLogger.Log("=== ezgetBMCIP startup ===");
        AppLogger.Log("Version: " + version);
        AppLogger.Log("OS: " + Environment.OSVersion + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
        AppLogger.Log("Process: " + (Environment.Is64BitProcess ? "x64" : "x86"));
        AppLogger.Log(".NET: " + Environment.Version);
        AppLogger.Log("Log path: " + AppLogger.LogFilePath);

        NetworkConfigManager.Logger = msg => AppLogger.Log("[Core] " + msg);
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
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Admin restart failed: " + ex.Message);
            MessageBox.Show(
                "\u7a0b\u5e8f\u9700\u8981\u7ba1\u7406\u5458\u6743\u9650\u624d\u80fd\u914d\u7f6e\u7f51\u5361\u548c\u542f\u52a8 DHCP \u670d\u52a1\u3002\r\n\r\n" + ex.Message,
                "\u9700\u8981\u7ba1\u7406\u5458\u6743\u9650",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
