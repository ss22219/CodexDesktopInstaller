using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexInstaller.Core;

public class InstallEngine
{
    private const string LauncherExeName = "Codex 启动.exe";
    private const string FreeProviderBaseUrl = "http://127.0.0.1:17631/v1";
    private const string FreeDefaultModel = "deepseek-v4-flash-free";
    private static readonly string[] FreeModels =
    [
        "deepseek-v4-flash-free",
        "north-mini-code-free",
        "mimo-v2.5-free",
        "nemotron-3-ultra-free"
    ];

    private readonly string _targetDir;
    private readonly string _bundleDir;
    private readonly ProgressCallback _onProgress;

    public delegate Task ProgressCallback(InstallProgress progress);

    public InstallEngine(string targetDir, string bundleDir, ProgressCallback onProgress)
    {
        _targetDir = targetDir;
        _bundleDir = bundleDir;
        _onProgress = onProgress;
    }

    public async Task InstallAsync()
    {
        try
        {
            await ReportAsync(0, "正在准备安装...");

            var codexBundleDir = Path.Combine(_bundleDir, "CodexDesktop");
            var codexArchive = Path.Combine(_bundleDir, "Archives", "CodexDesktop.7z");
            if (!File.Exists(codexArchive) && !Directory.Exists(codexBundleDir))
                throw new DirectoryNotFoundException($"未找到 Codex Desktop 资源包: {codexArchive}");

            await ReportAsync(1, "正在部署 Codex Desktop...");
            await DeployCodexDesktopAsync(codexBundleDir, codexArchive);
            NormalizeCuaNodePackageNames();

            await ReportAsync(70, "正在部署配置文件...");
            await DeployConfigAsync();

            await ReportAsync(80, "正在部署 Codex Skills...");
            await DeploySkillsAsync();

            await ReportAsync(86, "正在部署 HyperFrames 插件...");
            await DeployPluginsAsync();
            DeployBundledOpenAiPluginsToCache();
            EnsureDefaultPluginConfig();

            await ReportAsync(90, "正在部署工具运行时...");
            await DeployToolsAsync();
            EnsureDefaultFreeModelConfig();
            EnsureNodeReplMcpConfig();

            await ReportAsync(92, "正在部署启动器...");
            await DeployLauncherAsync();

            await ReportAsync(96, "正在创建快捷方式...");
            CreateShortcuts();

            await ReportAsync(98, "正在注册卸载入口...");
            CreateUninstallEntry();

            await ReportAsync(100, "安装完成!");
        }
        catch (Exception ex)
        {
            await _onProgress(new InstallProgress
            {
                Percent = 0,
                Status = "安装失败",
                IsCompleted = true,
                ErrorMessage = ex.Message
            });
            throw;
        }
    }

    private async Task DeployCodexDesktopAsync(string codexBundleDir, string codexArchive)
    {
        if (File.Exists(codexArchive))
        {
            var sevenZipExe = Path.Combine(_bundleDir, "Tools", "7zip", "7z.exe");
            if (!File.Exists(sevenZipExe))
                throw new FileNotFoundException("未找到 7-Zip 解压工具", sevenZipExe);

            await ExtractCodexDesktopArchiveAsync(codexArchive, sevenZipExe);
            return;
        }

        await DeployDirectoryAsync(codexBundleDir, _targetDir, 1, 70, "正在部署 Codex Desktop");
    }

    private async Task ExtractCodexDesktopArchiveAsync(string archivePath, string sevenZipExe)
    {
        Directory.CreateDirectory(_targetDir);

        var psi = new ProcessStartInfo
        {
            FileName = sevenZipExe,
            WorkingDirectory = Path.GetDirectoryName(sevenZipExe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add($"-o{_targetDir}");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-aoa");
        psi.ArgumentList.Add("-bsp1");
        psi.ArgumentList.Add("-bso1");
        psi.ArgumentList.Add("-bse2");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 7-Zip 解压工具");

        var progressTask = ReadSevenZipProgressAsync(process.StandardOutput);
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        await progressTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error)
                ? $"7-Zip 解压失败，退出码 {process.ExitCode}"
                : error.Trim();
            throw new InvalidOperationException(message);
        }

        await ReportAsync(70, "Codex Desktop 解压完成");
    }

    private async Task ReadSevenZipProgressAsync(TextReader reader)
    {
        var buffer = new char[1024];
        var recent = new StringBuilder();
        var lastArchivePercent = -1;

        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            recent.Append(buffer, 0, read);
            if (recent.Length > 200)
            {
                recent.Remove(0, recent.Length - 200);
            }

            var archivePercent = FindLastPercent(recent.ToString());
            if (archivePercent is null || archivePercent.Value == lastArchivePercent)
            {
                continue;
            }

            lastArchivePercent = archivePercent.Value;
            var overallPercent = 1 + (int)(archivePercent.Value / 100.0 * 69);
            await ReportAsync(overallPercent, $"正在解压 Codex Desktop... {archivePercent.Value}%");
        }
    }

    private static int? FindLastPercent(string text)
    {
        int? result = null;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '%')
            {
                continue;
            }

            var end = i - 1;
            while (end >= 0 && char.IsWhiteSpace(text[end]))
            {
                end--;
            }

            var start = end;
            while (start >= 0 && char.IsDigit(text[start]))
            {
                start--;
            }

            if (start == end)
            {
                continue;
            }

            if (int.TryParse(text.AsSpan(start + 1, end - start), out var percent)
                && percent is >= 0 and <= 100)
            {
                result = percent;
            }
        }

        return result;
    }

    private async Task DeployConfigAsync()
    {
        var configBundle = Path.Combine(_bundleDir, "Config");
        if (!Directory.Exists(configBundle)) return;

        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        await DeployDirectoryAsync(
            Path.Combine(configBundle, "codex"),
            codexDir,
            70,
            80,
            "正在部署 Codex 配置");

        await ReportAsync(80, "Codex 配置部署完成");
    }

    private async Task DeploySkillsAsync()
    {
        var skillsBundle = Path.Combine(_bundleDir, "Skills");
        if (!Directory.Exists(skillsBundle)) return;

        var skillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "skills");
        await DeployDirectoryAsync(
            skillsBundle,
            skillsDir,
            80,
            86,
            "正在部署 Codex Skills");
    }

    private async Task DeployPluginsAsync()
    {
        var pluginsBundle = Path.Combine(_bundleDir, "Plugins");
        if (!Directory.Exists(pluginsBundle)) return;

        var pluginsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "plugins",
            "cache");
        await DeployDirectoryAsync(
            pluginsBundle,
            pluginsDir,
            86,
            90,
            "正在部署 Codex 插件");
        StripUtf8BomFromSkillFiles(pluginsDir);
    }

    private void DeployBundledOpenAiPluginsToCache()
    {
        var sourceRoot = Path.Combine(_targetDir, "resources", "plugins", "openai-bundled", "plugins");
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "plugins",
            "cache",
            "openai-bundled");

        foreach (var pluginDir in Directory.EnumerateDirectories(sourceRoot))
        {
            var pluginName = Path.GetFileName(pluginDir);
            var version = ReadPluginVersion(pluginDir);
            if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            MergeDirectory(pluginDir, Path.Combine(cacheRoot, pluginName, version));
        }

        StripUtf8BomFromSkillFiles(cacheRoot);
    }

    private static string ReadPluginVersion(string pluginDir)
    {
        var manifestPath = Path.Combine(pluginDir, ".codex-plugin", "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            return document.RootElement.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private async Task DeployLauncherAsync()
    {
        var launcherBundle = Path.Combine(_bundleDir, "Launcher");
        if (!Directory.Exists(launcherBundle))
        {
            throw new DirectoryNotFoundException($"未找到 Codex 启动资源包: {launcherBundle}");
        }

        await DeployDirectoryAsync(
            launcherBundle,
            Path.Combine(_targetDir, "Launcher"),
            92,
            96,
            "正在部署 Codex 启动");
    }

    private async Task DeployToolsAsync()
    {
        var toolsBundle = Path.Combine(_bundleDir, "Tools");
        if (!Directory.Exists(toolsBundle))
        {
            return;
        }

        await DeployDirectoryAsync(
            toolsBundle,
            Path.Combine(_targetDir, "Tools"),
            90,
            92,
            "正在部署工具运行时");

        EnsureNodeOnUserPath();
    }

    private void EnsureNodeOnUserPath()
    {
        var nodeDir = Path.Combine(_targetDir, "Tools", "Node");
        if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
        {
            return;
        }

        AddDirectoryToPath(nodeDir, EnvironmentVariableTarget.User);
        AddDirectoryToPath(nodeDir, EnvironmentVariableTarget.Process);
    }

    private static void AddDirectoryToPath(string directory, EnvironmentVariableTarget target)
    {
        var current = Environment.GetEnvironmentVariable("Path", target) ?? "";
        var parts = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (parts.Any(part => SameDirectory(part, directory)))
        {
            return;
        }

        parts.Add(directory);
        Environment.SetEnvironmentVariable("Path", string.Join(';', parts), target);
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(Environment.ExpandEnvironmentVariables(right)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void NormalizeCuaNodePackageNames()
    {
        var nodeModulesRoot = Path.Combine(
            _targetDir,
            "resources",
            "cua_node",
            "bin",
            "node_modules");
        if (!Directory.Exists(nodeModulesRoot))
        {
            return;
        }

        var encodedDirectories = Directory
            .EnumerateDirectories(nodeModulesRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                DecodePackageName(Path.GetFileName(path)),
                StringComparison.Ordinal))
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var source in encodedDirectories)
        {
            if (!Directory.Exists(source))
            {
                continue;
            }

            var name = Path.GetFileName(source);
            var parent = Path.GetDirectoryName(source);
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }

            var target = Path.Combine(parent, DecodePackageName(name));
            if (!Directory.Exists(target))
            {
                Directory.Move(source, target);
                continue;
            }

            MergeDirectory(source, target);
            Directory.Delete(source, recursive: true);
        }

        var encodedFiles = Directory
            .EnumerateFiles(nodeModulesRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                DecodePackageName(Path.GetFileName(path)),
                StringComparison.Ordinal))
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var source in encodedFiles)
        {
            if (!File.Exists(source))
            {
                continue;
            }

            var name = Path.GetFileName(source);
            var parent = Path.GetDirectoryName(source);
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }

            var target = Path.Combine(parent, DecodePackageName(name));
            if (!File.Exists(target))
            {
                File.Move(source, target);
                continue;
            }

            File.Copy(source, target, overwrite: true);
            File.Delete(source);
        }
    }

    private static string DecodePackageName(string name)
    {
        return name
            .Replace("%40", "@", StringComparison.OrdinalIgnoreCase)
            .Replace("%2B", "+", StringComparison.OrdinalIgnoreCase)
            .Replace("%24", "$", StringComparison.OrdinalIgnoreCase);
    }

    private static void MergeDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private static void StripUtf8BomFromSkillFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var skillFile in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
        {
            StripUtf8Bom(skillFile);
        }
    }

    private static void StripUtf8Bom(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF)
            {
                return;
            }

            File.WriteAllBytes(path, bytes[3..]);
        }
        catch
        {
        }
    }

    private void EnsureNodeReplMcpConfig()
    {
        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        Directory.CreateDirectory(codexDir);

        var configPath = Path.Combine(codexDir, "config.toml");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : "";
        var updated = CodexRuntimeConfigurator.EnsureDefaultPluginsEnabled(
            CodexRuntimeConfigurator.EnsureToolFeatureFlags(
                CodexRuntimeConfigurator.EnsureNodeReplMcpConfig(existing, _targetDir)));
        if (!string.Equals(existing, updated, StringComparison.Ordinal))
        {
            AtomicWrite(configPath, updated);
        }
    }

    private static void EnsureDefaultFreeModelConfig()
    {
        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        Directory.CreateDirectory(codexDir);

        var configPath = Path.Combine(codexDir, "config.toml");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : "";
        CodexModelCatalog.Write(codexDir, FreeModels);
        var updated = CodexRuntimeConfigurator.EnsureDefaultPluginsEnabled(
            CodexRuntimeConfigurator.EnsureToolFeatureFlags(
                MergeDefaultFreeModelConfig(existing, Path.Combine(codexDir, CodexModelCatalog.FileName))));
        if (!string.Equals(existing, updated, StringComparison.Ordinal))
        {
            AtomicWrite(configPath, updated);
        }
    }

    private static string MergeDefaultFreeModelConfig(string existing, string modelCatalogPath)
    {
        var topLevelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model_provider",
            "model",
            "default_model",
            "available_models",
            "use_hidden_models",
            "model_catalog_json",
            "model_reasoning_effort",
            "disable_response_storage",
            "web_search"
        };

        var topLevel = new List<string>();
        var sections = new List<string>();
        var currentSection = "";
        var inTopLevel = true;
        var skipCustomProvider = false;

        if (!string.IsNullOrWhiteSpace(existing))
        {
            foreach (var line in existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inTopLevel = false;
                    currentSection = trimmed.Trim('[', ']').Trim();
                    skipCustomProvider =
                        string.Equals(currentSection, "model_providers.custom", StringComparison.OrdinalIgnoreCase) ||
                        currentSection.StartsWith("model_providers.custom.", StringComparison.OrdinalIgnoreCase);
                }

                if (skipCustomProvider)
                {
                    continue;
                }

                if (inTopLevel && TryReadTomlKey(trimmed, out var key) && topLevelKeys.Contains(key))
                {
                    continue;
                }

                if (inTopLevel)
                {
                    topLevel.Add(line);
                }
                else
                {
                    sections.Add(line);
                }
            }
        }

        var output = new List<string>();
        output.AddRange(TrimBlankLines(topLevel));
        if (output.Count > 0)
        {
            output.Add("");
        }

        output.Add("model_provider = \"custom\"");
        output.Add($"model = {TomlString(FreeDefaultModel)}");
        output.Add($"default_model = {TomlString(FreeDefaultModel)}");
        output.Add("model_reasoning_effort = \"none\"");
        output.Add($"available_models = {TomlStringArray(FreeModels)}");
        output.Add($"model_catalog_json = {TomlString(modelCatalogPath)}");
        output.Add("use_hidden_models = true");
        output.Add("disable_response_storage = true");
        output.Add("web_search = \"disabled\"");

        var preservedSections = TrimBlankLines(sections);
        if (preservedSections.Count > 0)
        {
            output.Add("");
            output.AddRange(preservedSections);
        }

        output.Add("");
        output.Add("[model_providers.custom]");
        output.Add("name = \"free_models\"");
        output.Add($"base_url = {TomlString(FreeProviderBaseUrl)}");
        output.Add("wire_api = \"responses\"");
        output.Add("requires_openai_auth = false");

        return string.Join(Environment.NewLine, TrimBlankLines(output)) + Environment.NewLine;
    }

    private static string TomlString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string TomlStringArray(IEnumerable<string> values)
    {
        return "[" + string.Join(", ", values.Select(TomlString)) + "]";
    }

    private static void EnsureDefaultPluginConfig()
    {
        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        Directory.CreateDirectory(codexDir);

        var configPath = Path.Combine(codexDir, "config.toml");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : "";
        var updated = CodexRuntimeConfigurator.EnsureDefaultPluginsEnabled(existing);
        if (!string.Equals(existing, updated, StringComparison.Ordinal))
        {
            AtomicWrite(configPath, updated);
        }
    }

    private static bool TryReadTomlKey(string line, out string key)
    {
        key = "";
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return false;
        }

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        key = line[..equalsIndex].Trim();
        return key.Length > 0;
    }

    private static List<string> TrimBlankLines(IEnumerable<string> source)
    {
        var result = source.ToList();
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[0]))
        {
            result.RemoveAt(0);
        }

        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private void CreateShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

        var launcherExe = Path.Combine(_targetDir, "Launcher", LauncherExeName);
        if (!File.Exists(launcherExe))
        {
            throw new FileNotFoundException("未找到 Codex 启动", launcherExe);
        }

        var launcherDir = Path.GetDirectoryName(launcherExe)!;
        CreateShortcutAt(Path.Combine(desktop, "Codex 启动.lnk"), launcherExe, launcherDir);
        CreateShortcutAt(Path.Combine(startMenu, "Codex 启动.lnk"), launcherExe, launcherDir);
    }

    private void CreateUninstallEntry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launcherExe = Path.Combine(_targetDir, "Launcher", LauncherExeName);
        if (!File.Exists(launcherExe))
        {
            throw new FileNotFoundException("未找到 Codex 启动", launcherExe);
        }

        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexDesktopLauncher");
        if (key is null)
        {
            return;
        }

        key.SetValue("DisplayName", "Codex Desktop");
        key.SetValue("DisplayVersion", "1.0");
        key.SetValue("Publisher", "Codex Desktop");
        key.SetValue("InstallLocation", _targetDir);
        key.SetValue("DisplayIcon", launcherExe);
        key.SetValue("UninstallString", $"\"{launcherExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{launcherExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private async Task DeployDirectoryAsync(string source, string dest, int startPercent, int endPercent, string status)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(dest);

        var entries = Directory.GetFileSystemEntries(source, "*", SearchOption.AllDirectories);
        if (entries.Length == 0)
        {
            await ReportAsync(endPercent, $"{status}... (0/0)");
            return;
        }

        var done = 0;
        var lastReport = startPercent - 1;
        var progressRange = endPercent - startPercent;

        foreach (var entry in entries)
        {
            var rel = Path.GetRelativePath(source, entry);
            var target = Path.Combine(dest, rel);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                var dir = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(dir);
                File.Copy(entry, target, overwrite: true);
            }

            done++;
            var pct = startPercent + (int)((double)done / entries.Length * progressRange);
            if (pct > lastReport)
            {
                lastReport = pct;
                await ReportAsync(pct, $"{status}... ({done}/{entries.Length})");
            }
        }
    }

    private static void CreateShortcutAt(string shortcutPath, string targetPath, string workingDir)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ps1");
            File.WriteAllText(tmp, $"""
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}')
$s.TargetPath = '{targetPath.Replace("'", "''")}'
$s.WorkingDirectory = '{workingDir.Replace("'", "''")}'
$s.Save()
""");
            var psi = new ProcessStartInfo("powershell", $"-ExecutionPolicy Bypass -File \"{tmp}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            try { File.Delete(tmp); } catch { }
        }
        catch
        {
        }
    }

    private async Task ReportAsync(int percent, string status)
    {
        await _onProgress(new InstallProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Status = status
        });
    }
}
