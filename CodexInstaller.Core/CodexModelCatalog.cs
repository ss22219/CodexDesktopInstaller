using System.Text;

namespace CodexInstaller.Core;

public static class CodexModelCatalog
{
    public const string FileName = "codex-launcher-model-catalog.json";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string codexDir, IEnumerable<string> modelIds)
    {
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, FileName), Build(modelIds), Utf8NoBom);
    }

    public static string Build(IEnumerable<string> modelIds)
    {
        var cleanModels = modelIds
            .Select(modelId => modelId.Trim())
            .Where(modelId => modelId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"models\": [");
        for (var i = 0; i < cleanModels.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine(",");
            }

            AppendCatalogEntry(builder, cleanModels[i], 1000 + i);
        }

        builder.AppendLine();
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendCatalogEntry(StringBuilder builder, string modelId, int priority)
    {
        var displayName = BuildDisplayName(modelId);
        builder.AppendLine("    {");
        builder.AppendLine($"      \"slug\": {JsonString(modelId)},");
        builder.AppendLine($"      \"display_name\": {JsonString(displayName)},");
        builder.AppendLine($"      \"description\": {JsonString(displayName)},");
        builder.AppendLine("      \"base_instructions\": \"You are Codex, a coding agent. You and the user share the same workspace and collaborate to achieve the user's goals.\",");
        builder.AppendLine("      \"default_reasoning_level\": \"none\",");
        builder.AppendLine("      \"supported_reasoning_levels\": [");
        builder.AppendLine("        { \"effort\": \"none\", \"description\": \"Disable Thinking\" }");
        builder.AppendLine("      ],");
        builder.AppendLine("      \"shell_type\": \"shell_command\",");
        builder.AppendLine("      \"visibility\": \"list\",");
        builder.AppendLine("      \"supported_in_api\": true,");
        builder.AppendLine($"      \"priority\": {priority},");
        builder.AppendLine("      \"supports_reasoning_summaries\": false,");
        builder.AppendLine("      \"default_reasoning_summary\": \"none\",");
        builder.AppendLine("      \"support_verbosity\": true,");
        builder.AppendLine("      \"truncation_policy\": { \"mode\": \"tokens\", \"limit\": 10000 },");
        builder.AppendLine("      \"supports_parallel_tool_calls\": true,");
        builder.AppendLine("      \"supports_image_detail_original\": true,");
        builder.AppendLine("      \"context_window\": 262144,");
        builder.AppendLine("      \"max_context_window\": 262144,");
        builder.AppendLine("      \"effective_context_window_percent\": 95,");
        builder.AppendLine("      \"experimental_supported_tools\": [],");
        builder.AppendLine("      \"input_modalities\": [\"text\", \"image\"],");
        builder.AppendLine("      \"supports_search_tool\": true,");
        builder.AppendLine("      \"additional_speed_tiers\": [\"fast\"],");
        builder.AppendLine("      \"service_tiers\": [");
        builder.AppendLine("        {");
        builder.AppendLine("          \"id\": \"priority\",");
        builder.AppendLine("          \"name\": \"Fast\",");
        builder.AppendLine("          \"description\": \"1.5x speed\"");
        builder.AppendLine("        }");
        builder.AppendLine("      ],");
        builder.AppendLine("      \"availability_nux\": null,");
        builder.AppendLine("      \"upgrade\": null");
        builder.Append("    }");
    }

    private static string BuildDisplayName(string modelId)
    {
        return string.Join(' ', modelId
            .Replace("_", "-", StringComparison.Ordinal)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Length <= 3 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string JsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString()
            });
        }

        builder.Append('"');
        return builder.ToString();
    }
}
