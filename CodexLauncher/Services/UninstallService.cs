using System.Diagnostics;
using System.Text;

namespace CodexLauncher.Services;

internal static class UninstallService
{
    private const string UninstallRegistryKey = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexDesktopLauncher";

    public static void StartAndExit()
    {
        var installDir = GetInstallDir();
        GuardInstallDir(installDir);

        var scriptPath = Path.Combine(Path.GetTempPath(), "codex-uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, BuildScript(), new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-InstallDir");
        psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add("-LauncherPid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        Process.Start(psi);
        Environment.Exit(0);
    }

    private static string GetInstallDir()
    {
        var launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = new DirectoryInfo(launcherDir);
        if (string.Equals(dir.Name, "Launcher", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
        {
            return dir.Parent.FullName;
        }

        return launcherDir;
    }

    private static void GuardInstallDir(string installDir)
    {
        var fullPath = Path.GetFullPath(installDir);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || (!File.Exists(Path.Combine(fullPath, "Launcher", "Codex 启动.exe"))
                && !File.Exists(Path.Combine(fullPath, "Launcher", "CodexLauncher.exe"))))
        {
            throw new InvalidOperationException("当前目录不像有效的 Codex 安装目录，已取消卸载。");
        }
    }

    private static string BuildScript()
    {
        return """
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][int]$LauncherPid
)

try {
    Wait-Process -Id $LauncherPid -Timeout 20 -ErrorAction SilentlyContinue
} catch { }

$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$startMenu = [IO.Path]::Combine([Environment]::GetFolderPath('StartMenu'), 'Programs')

@(
    [IO.Path]::Combine($desktop, 'Codex 启动.lnk'),
    [IO.Path]::Combine($desktop, 'Codex 启动器.lnk'),
    [IO.Path]::Combine($desktop, 'Codex Desktop.lnk'),
    [IO.Path]::Combine($startMenu, 'Codex 启动.lnk'),
    [IO.Path]::Combine($startMenu, 'Codex 启动器.lnk'),
    [IO.Path]::Combine($startMenu, 'Codex Desktop.lnk')
) | ForEach-Object {
    Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue
}

Remove-Item -LiteralPath '__UNINSTALL_REGISTRY_KEY__' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""".Replace("__UNINSTALL_REGISTRY_KEY__", UninstallRegistryKey, StringComparison.Ordinal);
    }
}
