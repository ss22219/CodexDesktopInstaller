using Avalonia;
using CodexInstaller.Core;
using System;
using System.Text;

namespace CodexInstaller.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (TryGetOption(args, "--silent-install", out var installDir))
        {
            RunSilentInstall(args, installDir);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static void RunSilentInstall(string[] args, string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Codex");
        }

        TryGetOption(args, "--log", out var logPath);
        var logLock = new object();
        void Log(string message)
        {
            lock (logLock)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                try { Console.WriteLine(line); } catch { }
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
                    File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
        }

        try
        {
            var bundleDir = Path.Combine(AppContext.BaseDirectory, "Bundle");
            var engine = new InstallEngine(installDir, bundleDir, progress =>
            {
                Log($"{progress.Percent}% {progress.Status}{(string.IsNullOrWhiteSpace(progress.ErrorMessage) ? "" : " - " + progress.ErrorMessage)}");
                return Task.CompletedTask;
            });

            Log($"开始静默安装: {installDir}");
            engine.InstallAsync().GetAwaiter().GetResult();
            Log("静默安装完成");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log("静默安装失败: " + ex);
            Environment.Exit(1);
        }
    }

    private static bool TryGetOption(string[] args, string name, out string value)
    {
        value = "";
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                value = i + 1 < args.Length ? args[i + 1] : "";
                return true;
            }

            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(name.Length + 1)..];
                return true;
            }
        }

        return false;
    }
}
