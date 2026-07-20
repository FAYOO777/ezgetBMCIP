using System;
using System.IO;
using System.Text;

namespace EzGetBmcIp;

internal static class AppLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ezgetBMCIP");
    private static readonly string LogPath = Path.Combine(LogDir, "ezgetBMCIP.log");
    private static readonly object _sync = new object();
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly UTF8Encoding Utf8WithBom = new UTF8Encoding(true, true);
    private static bool _encodingPrepared;

    public static string LogFilePath => LogPath;

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            lock (_sync)
            {
                EnsureUtf8Bom();
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                    Utf8NoBom);
            }
        }
        catch { }
    }

    private static void EnsureUtf8Bom()
    {
        if (_encodingPrepared)
            return;

        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
        {
            File.WriteAllBytes(LogPath, Utf8WithBom.GetPreamble());
        }
        else
        {
            var bytes = File.ReadAllBytes(LogPath);
            var preamble = Utf8WithBom.GetPreamble();
            var hasBom = bytes.Length >= preamble.Length
                && bytes.Take(preamble.Length).SequenceEqual(preamble);
            if (!hasBom)
            {
                var existingText = Utf8WithBom.GetString(bytes);
                File.WriteAllText(LogPath, existingText, Utf8WithBom);
            }
        }

        _encodingPrepared = true;
    }
}
