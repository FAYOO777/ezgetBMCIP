using System.Diagnostics;
using System.Security.Principal;

namespace EzGetBmcIp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!IsAdministrator())
        {
            RestartAsAdministrator();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
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
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
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
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
