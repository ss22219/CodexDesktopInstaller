using System.Security.Cryptography;
using System.Text;

namespace CodexInstaller.Core;

public static class CodexRuntimeConfigurator
{
    private const string CodexAppVersion = "26.623.81905";
    private static readonly string[] ToolFeatureKeys =
    [
        "js_repl",
        "tool_search"
    ];
    private static readonly string[] DefaultPluginSections =
    [
        "plugins.\"browser@openai-bundled\"",
        "plugins.\"computer-use@openai-bundled\"",
        "plugins.\"hyperframes@openai-curated-remote\""
    ];

    public static string EnsureNodeReplMcpConfig(string existing, string codexInstallDir)
    {
        var runtimeRoot = Path.Combine(codexInstallDir, "resources", "cua_node");
        var binDir = Path.Combine(runtimeRoot, "bin");
        var nodePath = Path.Combine(binDir, "node.exe");
        var nodeReplPath = Path.Combine(binDir, "node_repl.exe");
        var nodeModules = Path.Combine(binDir, "node_modules");

        if (!File.Exists(nodePath) || !File.Exists(nodeReplPath) || !Directory.Exists(nodeModules))
        {
            return existing;
        }

        var codexHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        Directory.CreateDirectory(codexHome);
        var trustedCodePaths = BuildTrustedCodePaths(codexInstallDir, codexHome);

        var normalized = NormalizeMcpServerNames(existing);
        var cleaned = RemoveNodeReplMcpSections(normalized);
        var lines = string.IsNullOrWhiteSpace(cleaned)
            ? new List<string>()
            : cleaned.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        lines = TrimBlankLines(lines);

        if (lines.Count > 0)
        {
            lines.Add("");
        }

        if (!ContainsSection(lines, "mcp_servers"))
        {
            lines.Add("[mcp_servers]");
            lines.Add("");
        }

        lines.Add("[mcp_servers.node_repl]");
        lines.Add("args = []");
        lines.Add($"command = {TomlString(nodeReplPath)}");
        lines.Add("startup_timeout_sec = 120");
        lines.Add("");
        lines.Add("[mcp_servers.node_repl.env]");
        lines.Add("NODE_REPL_NATIVE_PIPE_CONNECT_TIMEOUT_MS = \"1000\"");
        lines.Add($"NODE_REPL_NODE_MODULE_DIRS = {TomlString(nodeModules)}");
        lines.Add($"NODE_REPL_NODE_PATH = {TomlString(nodePath)}");
        lines.Add($"NODE_REPL_TRUSTED_CODE_PATHS = {TomlString(string.Join(Path.PathSeparator, trustedCodePaths))}");
        lines.Add($"CODEX_HOME = {TomlString(codexHome)}");

        var browserClientHashes = GetBrowserClientHashes(codexInstallDir);
        if (browserClientHashes.Count > 0)
        {
            lines.Add($"NODE_REPL_TRUSTED_BROWSER_CLIENT_SHA256S = {TomlString(string.Join(',', browserClientHashes))}");
        }

        lines.Add("BROWSER_USE_AVAILABLE_BACKENDS = \"chrome,iab\"");
        lines.Add("BROWSER_USE_CODEX_APP_BUILD_FLAVOR = \"prod\"");
        lines.Add($"BROWSER_USE_CODEX_APP_VERSION = {TomlString(CodexAppVersion)}");
        lines.Add("SKY_CUA_NATIVE_PIPE = \"1\"");
        lines.Add("NODE_REPL_INSTRUCTIONS_USE_CASE_BROWSER = \"Control the in-app browser in conjunction with the Browser Plugin.\"");
        lines.Add("NODE_REPL_INSTRUCTIONS_USE_CASE_CHROME = \"Control the Chrome browser in conjunction with the Chrome Plugin. Prefer this method of controlling Chrome over alternatives (such as Computer Use) unless the user explicitly mentions an alternative.\"");

        return string.Join(Environment.NewLine, TrimBlankLines(lines)) + Environment.NewLine;
    }

    public static string EnsureToolFeatureFlags(string existing)
    {
        var lines = string.IsNullOrWhiteSpace(existing)
            ? new List<string>()
            : existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        var output = new List<string>();
        var inFeatures = false;
        var foundFeatures = false;
        var writtenInCurrentFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMissingFeatureFlags()
        {
            foreach (var key in ToolFeatureKeys)
            {
                if (writtenInCurrentFeatures.Add(key))
                {
                    output.Add($"{key} = true");
                }
            }
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inFeatures)
                {
                    AddMissingFeatureFlags();
                }

                var section = trimmed.Trim('[', ']').Trim();
                inFeatures = string.Equals(section, "features", StringComparison.OrdinalIgnoreCase);
                foundFeatures |= inFeatures;
                writtenInCurrentFeatures.Clear();
                output.Add(line);
                continue;
            }

            if (inFeatures && TryReadTomlKey(trimmed, out var key)
                && ToolFeatureKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                if (writtenInCurrentFeatures.Add(key))
                {
                    output.Add($"{key} = true");
                }

                continue;
            }

            output.Add(line);
        }

        if (inFeatures)
        {
            AddMissingFeatureFlags();
        }

        output = TrimBlankLines(output);
        if (!foundFeatures)
        {
            if (output.Count > 0)
            {
                output.Add("");
            }

            output.Add("[features]");
            foreach (var key in ToolFeatureKeys)
            {
                output.Add($"{key} = true");
            }
        }

        return string.Join(Environment.NewLine, TrimBlankLines(output)) + Environment.NewLine;
    }

    public static string EnsureDefaultPluginsEnabled(string existing)
    {
        var updated = existing;
        foreach (var section in DefaultPluginSections)
        {
            updated = EnsurePluginEnabled(updated, section);
        }

        return updated;
    }

    public static string NormalizeMcpServerNames(string existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var lines = existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            {
                continue;
            }

            var normalized = trimmed
                .Replace("mcp_servers.mcp__", "mcp_servers.", StringComparison.OrdinalIgnoreCase)
                .Replace("mcp_servers.\"mcp__", "mcp_servers.\"", StringComparison.OrdinalIgnoreCase)
                .Replace("mcp_servers.'mcp__", "mcp_servers.'", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(trimmed, normalized, StringComparison.Ordinal))
            {
                continue;
            }

            var leadingWhitespaceLength = lines[i].Length - lines[i].TrimStart().Length;
            lines[i] = lines[i][..leadingWhitespaceLength] + normalized;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RemoveNodeReplMcpSections(string existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return "";
        }

        var lines = existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();
        var skip = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var section = trimmed.Trim('[', ']').Trim();
                skip = IsNodeReplMcpSection(section);
            }

            if (!skip)
            {
                kept.Add(line);
            }
        }

        return string.Join(Environment.NewLine, TrimBlankLines(kept)) + Environment.NewLine;
    }

    private static bool IsNodeReplMcpSection(string section)
    {
        section = section.Replace("\"", "").Replace("'", "");
        return string.Equals(section, "mcp_servers.node_repl", StringComparison.OrdinalIgnoreCase)
            || section.StartsWith("mcp_servers.node_repl.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, "mcp_servers.mcp__node_repl", StringComparison.OrdinalIgnoreCase)
            || section.StartsWith("mcp_servers.mcp__node_repl.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSection(IEnumerable<string> lines, string sectionName)
    {
        return lines.Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith('[')
                && trimmed.EndsWith(']')
                && string.Equals(trimmed.Trim('[', ']').Trim(), sectionName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string EnsurePluginEnabled(string existing, string targetSection)
    {
        var sourceLines = string.IsNullOrWhiteSpace(existing)
            ? new List<string>()
            : existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        var output = new List<string>();
        var inTargetSection = false;
        var foundTargetSection = false;
        var enabledAddedInTargetSection = false;

        foreach (var line in sourceLines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inTargetSection && !enabledAddedInTargetSection)
                {
                    output.Add("enabled = true");
                }

                var section = trimmed.Trim('[', ']').Trim();
                inTargetSection = IsPluginSection(section, targetSection);
                if (inTargetSection)
                {
                    foundTargetSection = true;
                    enabledAddedInTargetSection = true;
                    output.Add(line);
                    output.Add("enabled = true");
                    continue;
                }

                enabledAddedInTargetSection = false;
            }

            if (inTargetSection && TryReadTomlKey(trimmed, out var key)
                && string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            output.Add(line);
        }

        if (inTargetSection && !enabledAddedInTargetSection)
        {
            output.Add("enabled = true");
        }

        output = TrimBlankLines(output);
        if (!foundTargetSection)
        {
            if (output.Count > 0)
            {
                output.Add("");
            }

            output.Add($"[{targetSection}]");
            output.Add("enabled = true");
        }

        return string.Join(Environment.NewLine, TrimBlankLines(output)) + Environment.NewLine;
    }

    private static bool IsPluginSection(string section, string targetSection)
    {
        static string Normalize(string value)
        {
            return value.Replace("'", "\"", StringComparison.Ordinal);
        }

        return string.Equals(Normalize(section), Normalize(targetSection), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadTomlKey(string trimmedLine, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
        {
            return false;
        }

        var index = trimmedLine.IndexOf('=');
        if (index <= 0)
        {
            return false;
        }

        key = trimmedLine[..index].Trim();
        return key.Length > 0;
    }

    private static List<string> GetBrowserClientHashes(string codexInstallDir)
    {
        var pluginsDir = Path.Combine(codexInstallDir, "resources", "plugins", "openai-bundled", "plugins");
        var candidates = new[]
        {
            Path.Combine(pluginsDir, "browser", "scripts", "browser-client.mjs"),
            Path.Combine(pluginsDir, "chrome", "scripts", "browser-client.mjs"),
            Path.Combine(
                codexInstallDir,
                "resources",
                "cua_node",
                "bin",
                "node_modules",
                "@oai",
                "cdp-browser-backend",
                "dist",
                "skill",
                "scripts",
                "browser-client.mjs")
        };

        return candidates
            .Where(File.Exists)
            .Select(HashFile)
            .Where(hash => hash.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildTrustedCodePaths(string codexInstallDir, string codexHome)
    {
        var candidates = new[]
        {
            codexHome,
            Path.Combine(codexInstallDir, "resources", "plugins"),
            Path.Combine(codexInstallDir, "resources", "cua_node")
        };

        return candidates
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string HashFile(string path)
    {
        try
        {
            var bytes = SHA256.HashData(File.ReadAllBytes(path));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static string TomlString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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
}
