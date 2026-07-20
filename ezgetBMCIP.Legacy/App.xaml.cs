using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace EzGetBmcIp.Legacy
{
    public partial class App : Application
    {
        public static readonly string LogFilePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ezgetBMCIP.log");
        private static readonly object LogSync = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly UTF8Encoding Utf8WithBom = new UTF8Encoding(true, true);
        private static bool _logEncodingPrepared;

        protected override async void OnStartup(StartupEventArgs e)
        {
            NetworkConfigManager.Logger = LogError;

            int ownerProcessId;
            string recoverySessionId;
            if (NetworkRecoveryStore.TryParseWatchdogArguments(
                e.Args, out ownerProcessId, out recoverySessionId))
            {
                StartupUri = null;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);

                if (!IsAdministrator())
                {
                    LogError("Recovery watchdog is not elevated; recovery cannot run.");
                    Shutdown(1);
                    return;
                }

                var exitCode = await NetworkRecoveryStore.RunWatchdogAsync(
                    ownerProcessId, recoverySessionId, message => LogError("[Recovery] " + message));
                Shutdown(exitCode);
                return;
            }

            if (!IsAdministrator())
            {
                RestartAsAdministrator();
                Shutdown();
                return;
            }

            LogError("Legacy App started");
            base.OnStartup(e);
        }

        private static void LogError(string message)
        {
            try
            {
                lock (LogSync)
                {
                    EnsureLogUtf8Bom();
                    File.AppendAllText(
                        LogFilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                        Utf8NoBom);
                }
            }
            catch { }
        }

        private static void EnsureLogUtf8Bom()
        {
            if (_logEncodingPrepared)
                return;

            if (!File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length == 0)
            {
                File.WriteAllBytes(LogFilePath, Utf8WithBom.GetPreamble());
            }
            else
            {
                var bytes = File.ReadAllBytes(LogFilePath);
                var preamble = Utf8WithBom.GetPreamble();
                var hasBom = bytes.Length >= preamble.Length;
                for (var i = 0; hasBom && i < preamble.Length; i++)
                    hasBom = bytes[i] == preamble[i];

                if (!hasBom)
                {
                    var existingText = Utf8WithBom.GetString(bytes);
                    File.WriteAllText(LogFilePath, existingText, Utf8WithBom);
                }
            }

            _logEncodingPrepared = true;
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
