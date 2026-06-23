using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace EzGetBmcIp.Legacy
{
    public partial class App : Application
    {
        public static readonly string LogFilePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ezgetBMCIP.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!IsAdministrator())
            {
                RestartAsAdministrator();
                Shutdown();
                return;
            }

            NetworkConfigManager.Logger = LogError;
            LogError("Legacy App started");
            base.OnStartup(e);
        }

        private static void LogError(string message)
        {
            try
            {
            File.AppendAllText(
                LogFilePath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void RestartAsAdministrator()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? "";
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? ""
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "\u7a0b\u5e8f\u9700\u8981\u7ba1\u7406\u5458\u6743\u9650\u624d\u80fd\u914d\u7f6e\u7f51\u5361\u548c\u542f\u52a8 DHCP \u670d\u52a1\u3002\r\n\r\n" + ex.Message,
                    "\u9700\u8981\u7ba1\u7406\u5458\u6743\u9650",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
