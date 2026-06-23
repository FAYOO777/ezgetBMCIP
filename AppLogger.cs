using System;
using System.IO;

namespace EzGetBmcIp;

internal static class AppLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ezgetBMCIP");
    private static readonly string LogPath = Path.Combine(LogDir, "ezgetBMCIP.log");
    private static readonly object _sync = new object();

    public static string LogFilePath => LogPath;

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            lock (_sync)
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}
