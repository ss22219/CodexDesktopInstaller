using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexInstaller.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexLauncher.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const int ApiProxyPort = 17631;
    private const string FreeProxyBaseUrl = "http://127.0.0.1:17631/v1";
    private const string FreeUpstreamBaseUrl = "https://opencode.ai/zen/v1";

    private static readonly string[] OpenCodeFreeModels =
    [
        "deepseek-v4-flash-free",
        "north-mini-code-free",
        "mimo-v2.5-free",
        "nemotron-3-ultra-free"
    ];

    private readonly List<CodexProviderProfile> _profiles =
    [
        new(
            "opencode_free",
            "免费模型",
            "DeepSeek V4 免费",
            FreeUpstreamBaseUrl,
            "deepseek-v4-flash-free",
            "responses",
            OpenCodeFreeModels,
            true,
            OpenCodeFreeModels),
        new(
            "openapi_official",
            "OpenAI 官方",
            "OpenAI",
            "https://api.openai.com/v1",
            "gpt-5.5",
            "responses",
            ["gpt-5.5", "gpt-5", "gpt-4.1"],
            false,
            []),
        new(
            "custom",
            "自定义 API",
            "custom",
            "",
            "gpt-5.5",
            "responses",
            ["gpt-5.5", "gpt-5", "gpt-4.1", "glm-5.2", "deepseek-v4-flash"],
            false,
            [])
    ];

    private readonly Dictionary<string, string> _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private bool _loadingProfile;

    public MainWindowViewModel()
    {
        ProviderNames = new ObservableCollection<string>(_profiles.Select(profile => profile.Name));
        ModelOptions = new ObservableCollection<string>();
        _settings = LoadSettings();

        var selectedId = GetRawSetting("selected", "opencode_free");
        var selected = _profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? _profiles[0];

        _loadingProfile = true;
        SelectedProviderName = selected.Name;
        LoadProfile(selected);
        _loadingProfile = false;

        StatusText = "已准备好。默认使用免费模型。";
        _ = RefreshModelsForSelectedProfileAsync(showStatus: false);
    }

    public ObservableCollection<string> ProviderNames { get; }

    public ObservableCollection<string> ModelOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeProvider))]
    [NotifyPropertyChangedFor(nameof(IsOfficialProvider))]
    [NotifyPropertyChangedFor(nameof(IsCustomProvider))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionFields))]
    [NotifyPropertyChangedFor(nameof(ShowModelSelector))]
    private string selectedProviderName = "";

    [ObservableProperty]
    private string providerName = "";

    [ObservableProperty]
    private string baseUrl = "";

    [ObservableProperty]
    private string model = "";

    [ObservableProperty]
    private string wireApi = "responses";

    [ObservableProperty]
    private string apiKey = "";

    [ObservableProperty]
    private string statusText = "";

    public bool IsFreeProvider => string.Equals(GetSelectedProfile()?.Id, "opencode_free", StringComparison.Ordinal);

    public bool IsOfficialProvider => string.Equals(GetSelectedProfile()?.Id, "openapi_official", StringComparison.Ordinal);

    public bool IsCustomProvider => string.Equals(GetSelectedProfile()?.Id, "custom", StringComparison.Ordinal);

    public bool ShowConnectionFields => IsCustomProvider;

    public bool ShowModelSelector => !IsOfficialProvider;

    partial void OnSelectedProviderNameChanged(string value)
    {
        if (_loadingProfile)
        {
            return;
        }

        var profile = GetSelectedProfile();
        if (profile is not null)
        {
            LoadProfile(profile);
            StatusText = profile.Id switch
            {
                "openapi_official" => "将使用 OpenAI 官方模式。",
                "opencode_free" => "已切换到免费模型。",
                _ => "已切换到自定义 API。"
            };
            _ = RefreshModelsForSelectedProfileAsync(showStatus: false);
        }
    }

    [RelayCommand]
    private void SelectProvider(string providerId)
    {
        var profile = _profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        SelectedProviderName = profile.Name;
    }

    [RelayCommand]
    private async Task RefreshModels()
    {
        await RefreshModelsForSelectedProfileAsync(showStatus: true);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            SaveCurrentProfile();
            WriteCodexFiles();
            var restarted = !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RestartCodexIfRunning();
            StatusText = restarted ? "配置已保存，Codex 已重启。" : "配置已保存。";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveAndLaunch()
    {
        try
        {
            SaveCurrentProfile();
            EnsureBundledResourcesInstalled();
            WriteCodexFiles();
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                StopRunningCodex();
                StopStaleNodeReplProcesses();
            }
            StartApiProxyIfNeeded();
            LaunchCodex();
            StatusText = "配置已保存，正在启动 Codex。";
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var codexDir = GetCodexDir();
        Directory.CreateDirectory(codexDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = codexDir,
            UseShellExecute = true
        });
    }

    private CodexProviderProfile? GetSelectedProfile()
    {
        return _profiles.FirstOrDefault(profile => profile.Name == SelectedProviderName);
    }

    private void LoadProfile(CodexProviderProfile profile)
    {
        _loadingProfile = true;
        try
        {
            ProviderName = profile.Id == "opencode_free"
                ? profile.ProviderName
                : GetProfileSetting(profile.Id, "providerName", profile.ProviderName);
            BaseUrl = profile.Id == "opencode_free"
                ? FreeUpstreamBaseUrl
                : GetProfileSetting(profile.Id, "baseUrl", profile.BaseUrl);
            WireApi = profile.Id == "opencode_free"
                ? profile.WireApi
                : GetProfileSetting(profile.Id, "wireApi", profile.WireApi);
            ApiKey = profile.Id == "opencode_free"
                ? ""
                : GetProfileSetting(profile.Id, "apiKey", "");
            var savedModel = NormalizeSelectedModel(profile, GetProfileSetting(profile.Id, "model", profile.Model));
            SetModelOptions(profile, GetSavedModels(profile), savedModel);
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    private void SaveCurrentProfile()
    {
        var profile = GetSelectedProfile() ?? _profiles[0];

        _settings["selected"] = profile.Id;
        if (profile.Id == "custom")
        {
            SetProfileSetting(profile.Id, "providerName", ProviderName.Trim());
            SetProfileSetting(profile.Id, "baseUrl", BaseUrl.Trim());
            SetProfileSetting(profile.Id, "model", Model.Trim());
            SetProfileSetting(profile.Id, "wireApi", NormalizeWireApi(WireApi));
            SetProfileSetting(profile.Id, "apiKey", ApiKey);
            SetProfileSetting(profile.Id, "models", string.Join('\n', ModelOptions));
        }
        else if (profile.Id == "opencode_free")
        {
            SetProfileSetting(profile.Id, "model", Model.Trim());
            SetProfileSetting(profile.Id, "apiKey", "");
            SetProfileSetting(profile.Id, "models", string.Join('\n', ModelOptions));
        }

        Directory.CreateDirectory(GetLauncherSettingsDir());
        var lines = _settings.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}");
        AtomicWrite(GetLauncherSettingsPath(), string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private void WriteCodexFiles()
    {
        var codexDir = GetCodexDir();
        Directory.CreateDirectory(codexDir);

        var authPath = Path.Combine(codexDir, "auth.json");
        var configPath = Path.Combine(codexDir, "config.toml");

        var profile = GetSelectedProfile() ?? _profiles[0];
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : "";
        if (profile.Id == "openapi_official")
        {
            RemoveLauncherApiKeyAuth(authPath);
            AtomicWrite(
                configPath,
                CodexRuntimeConfigurator.EnsureDefaultPluginsEnabled(
                    CodexRuntimeConfigurator.EnsureToolFeatureFlags(
                        EnsureNodeReplMcpConfig(MergeOfficialCodexConfig(existing)))));
            RemoveLauncherModelCatalog(codexDir);
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            RemoveLauncherApiKeyAuth(authPath);
        }
        else
        {
            var authJson = "{\n  \"OPENAI_API_KEY\": " + JsonString(ApiKey) + "\n}\n";
            AtomicWrite(authPath, authJson);
        }

        WriteCodexModelCatalog(codexDir);

        var merged = CodexRuntimeConfigurator.EnsureToolFeatureFlags(
            EnsureNodeReplMcpConfig(MergeCodexConfig(existing, codexDir)));
        AtomicWrite(configPath, CodexRuntimeConfigurator.EnsureDefaultPluginsEnabled(merged));
    }

    private string MergeOfficialCodexConfig(string existing)
    {
        return RemoveLauncherCustomConfig(existing);
    }

    private string MergeCodexConfig(string existing, string codexDir)
    {
        var preserved = RemoveLauncherCustomConfig(existing);
        var topLevel = new List<string>();
        var sections = new List<string>();
        var currentSection = "";
        var skipCustomProvider = false;
        var inTopLevel = true;
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

        if (!string.IsNullOrWhiteSpace(preserved))
        {
            var lines = preserved.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var line in lines)
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

        output.AddRange(BuildCodexTopLevelLines(codexDir));

        var preservedSections = TrimBlankLines(sections);
        if (preservedSections.Count > 0)
        {
            output.Add("");
            output.AddRange(preservedSections);
        }

        output.Add("");
        output.AddRange(BuildCodexProviderSectionLines());

        return string.Join(Environment.NewLine, TrimBlankLines(output)) + Environment.NewLine;
    }

    private string RemoveLauncherCustomConfig(string existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return "";
        }

        existing = CodexRuntimeConfigurator.NormalizeMcpServerNames(existing);

        var lines = existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();
        var currentSection = "";
        var skipCustomProvider = false;
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

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed.Trim('[', ']').Trim();
                skipCustomProvider =
                    string.Equals(currentSection, "model_providers.custom", StringComparison.OrdinalIgnoreCase) ||
                    currentSection.StartsWith("model_providers.custom.", StringComparison.OrdinalIgnoreCase);
            }

            if (skipCustomProvider)
            {
                continue;
            }

            if (currentSection.Length == 0 && TryReadTomlKey(trimmed, out var key) && topLevelKeys.Contains(key))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join(Environment.NewLine, TrimBlankLines(kept)) + Environment.NewLine;
    }

    private string[] BuildCodexTopLevelLines(string codexDir)
    {
        var launcherModels = GetLauncherModelIds();
        return
        [
            "model_provider = \"custom\"",
            $"model = {TomlString(Model)}",
            $"default_model = {TomlString(Model)}",
            "model_reasoning_effort = \"none\"",
            $"available_models = {TomlStringArray(launcherModels)}",
            $"model_catalog_json = {TomlString(Path.Combine(codexDir, CodexModelCatalog.FileName))}",
            "use_hidden_models = true",
            "disable_response_storage = true",
            "web_search = \"disabled\""
        ];
    }

    private string[] BuildCodexProviderSectionLines()
    {
        var normalizedWireApi = NormalizeWireApi(WireApi);
        var isFreeProvider = string.Equals(GetSelectedProfile()?.Id, "opencode_free", StringComparison.Ordinal);
        var requiresAuth = !string.IsNullOrWhiteSpace(ApiKey) || !isFreeProvider;
        var providerBaseUrl = isFreeProvider ? FreeProxyBaseUrl : BaseUrl;
        return
        [
            "[model_providers.custom]",
            $"name = {TomlString(ProviderName)}",
            $"base_url = {TomlString(providerBaseUrl)}",
            $"wire_api = {TomlString(normalizedWireApi)}",
            $"requires_openai_auth = {requiresAuth.ToString().ToLowerInvariant()}"
        ];
    }

    private static string EnsureNodeReplMcpConfig(string existing)
    {
        var codexExe = FindCodexExe();
        if (codexExe is null)
        {
            return existing;
        }

        var codexInstallDir = Path.GetDirectoryName(codexExe);
        return string.IsNullOrWhiteSpace(codexInstallDir)
            ? existing
            : CodexRuntimeConfigurator.EnsureNodeReplMcpConfig(existing, codexInstallDir, GetCodexDir());
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

    private async Task RefreshModelsForSelectedProfileAsync(bool showStatus)
    {
        var profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        if (!profile.LoadModelsFromApi)
        {
            return;
        }

        if (IsFreeProfile(profile))
        {
            SetModelOptions(profile, profile.DefaultModels, Model);
            if (showStatus)
            {
                StatusText = "已使用内置免费模型列表。";
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            SetModelOptions(profile, profile.DefaultModels, Model);
            return;
        }

        try
        {
            if (showStatus)
            {
                StatusText = "正在获取可用模型...";
            }

            var models = await FetchModelIdsAsync(BaseUrl.Trim(), ApiKey);
            SetModelOptions(profile, models.Count > 0 ? models : profile.DefaultModels, Model);
            StatusText = models.Count > 0 && profile.AllowedModelIds.Length > 0
                ? $"已加载 {ModelOptions.Count} 个免费模型。"
                : models.Count > 0
                ? $"已加载 {ModelOptions.Count} 个模型。"
                : "暂时没有获取到模型，已使用内置列表。";
        }
        catch (Exception ex)
        {
            SetModelOptions(profile, profile.DefaultModels, Model);
            if (showStatus)
            {
                StatusText = $"模型列表获取失败，已使用内置列表：{ex.Message}";
            }
        }
    }

    private async Task<List<string>> FetchModelIdsAsync(string baseUrl, string apiKey)
    {
        var url = baseUrl.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idProperty))
            {
                continue;
            }

            var id = idProperty.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                models.Add(id);
            }
        }

        return models;
    }

    private IEnumerable<string> GetSavedModels(CodexProviderProfile profile)
    {
        var saved = GetProfileSetting(profile.Id, "models", "");
        var savedModels = saved
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId));

        return savedModels.Concat(profile.DefaultModels);
    }

    private void SetModelOptions(CodexProviderProfile profile, IEnumerable<string> models, string selectedModel)
    {
        var cleanModels = FilterAllowedModels(profile, models)
            .Select(modelId => modelId.Trim())
            .Where(modelId => modelId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleanModels.Count == 0)
        {
            cleanModels = FilterAllowedModels(profile, profile.DefaultModels)
                .Select(modelId => modelId.Trim())
                .Where(modelId => modelId.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        selectedModel = NormalizeSelectedModel(profile, selectedModel);
        if (!string.IsNullOrWhiteSpace(selectedModel) && cleanModels.All(modelId => !string.Equals(modelId, selectedModel, StringComparison.OrdinalIgnoreCase)))
        {
            cleanModels.Insert(0, selectedModel.Trim());
        }

        if (cleanModels.Count == 0)
        {
            cleanModels.Add("glm-5.2");
        }

        ModelOptions.Clear();
        foreach (var modelId in cleanModels)
        {
            ModelOptions.Add(modelId);
        }

        Model = string.IsNullOrWhiteSpace(selectedModel) ? cleanModels[0] : selectedModel.Trim();
    }

    private static IEnumerable<string> FilterAllowedModels(CodexProviderProfile profile, IEnumerable<string> models)
    {
        if (IsFreeProfile(profile))
        {
            return SortFreeModels(models.Where(IsFreeModel));
        }

        if (profile.AllowedModelIds.Length == 0)
        {
            return models;
        }

        var available = models.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profile.AllowedModelIds.Where(available.Contains);
    }

    private static string NormalizeSelectedModel(CodexProviderProfile profile, string selectedModel)
    {
        if (IsFreeProfile(profile))
        {
            return IsFreeModel(selectedModel)
                ? selectedModel
                : profile.Model;
        }

        if (profile.AllowedModelIds.Length == 0)
        {
            return selectedModel;
        }

        return profile.AllowedModelIds.Any(modelId => string.Equals(modelId, selectedModel, StringComparison.OrdinalIgnoreCase))
            ? selectedModel
            : profile.Model;
    }

    private static bool IsFreeProfile(CodexProviderProfile profile)
    {
        return string.Equals(profile.Id, "opencode_free", StringComparison.Ordinal);
    }

    private static bool IsFreeModel(string modelId)
    {
        return modelId.Trim().EndsWith("-free", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SortFreeModels(IEnumerable<string> models)
    {
        var priority = OpenCodeFreeModels
            .Select((modelId, index) => new { modelId, index })
            .ToDictionary(item => item.modelId, item => item.index, StringComparer.OrdinalIgnoreCase);

        return models
            .OrderBy(modelId => priority.TryGetValue(modelId, out var index) ? index : int.MaxValue)
            .ThenBy(modelId => modelId, StringComparer.OrdinalIgnoreCase);
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

    private void LaunchCodex()
    {
        var codexExe = FindCodexExe();
        if (codexExe is null)
        {
            throw new FileNotFoundException("未找到 Codex");
        }

        var codexArgs = new List<string>();
        if (IsFreeProvider)
        {
            AddOfflineSafeCodexArguments(codexArgs);
        }

        ProcessStartInfo startInfo;
        var codexApp = FindCodexApp();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && codexApp is not null)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add($"CODEX_HOME={GetCodexDir()}");
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add($"CODEX_FREE_HOME={GetMacAppSupportDir()}");
            startInfo.ArgumentList.Add(codexApp);
            startInfo.ArgumentList.Add("--args");
            startInfo.ArgumentList.Add($"--user-data-dir={Path.Combine(GetMacAppSupportDir(), "UserData")}");
            if (codexArgs.Count > 0)
            {
                foreach (var arg in codexArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = codexExe,
                WorkingDirectory = Path.GetDirectoryName(codexExe),
                UseShellExecute = false
            };
            startInfo.Environment["CODEX_HOME"] = GetCodexDir();
            foreach (var arg in codexArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
            PrependBundledNodeToPath(startInfo);
        }

        Process.Start(startInfo);
    }

    private static void AddOfflineSafeCodexArguments(ICollection<string> args)
    {
        args.Add("--no-first-run");
        args.Add("--no-default-browser-check");
        args.Add("--disable-background-networking");
        args.Add("--disable-component-update");
        args.Add("--disable-domain-reliability");
        args.Add("--disable-sync");
        args.Add("--disable-client-side-phishing-detection");
        args.Add("--disable-features=AutofillServerCommunication,CertificateTransparencyComponentUpdater,OptimizationGuideModelDownloading,OptimizationGuideOnDeviceModel,OptimizationHints,OptimizationHintsFetching,OptimizationTargetPrediction,SegmentationPlatform,MediaRouter");
        args.Add("--host-resolver-rules=MAP chat.openai.com 0.0.0.0,MAP chatgpt.com 0.0.0.0,MAP ab.chatgpt.com 0.0.0.0,MAP a.nel.cloudflare.com 0.0.0.0,MAP android.clients.google.com 0.0.0.0,MAP clients2.google.com 0.0.0.0,MAP dl.google.com 0.0.0.0,MAP optimizationguide-pa.googleapis.com 0.0.0.0,MAP redirector.gvt1.com 0.0.0.0,MAP mtalk.google.com 0.0.0.0,EXCLUDE localhost,EXCLUDE 127.0.0.1");
    }

    private bool RestartCodexIfRunning()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }

        if (!IsCodexRunning())
        {
            return false;
        }

        StopRunningCodex();
        StopStaleNodeReplProcesses();
        StartApiProxyIfNeeded();
        LaunchCodex();
        return true;
    }

    private static bool IsCodexRunning()
    {
        var codexExe = FindCodexExe();
        return codexExe is not null && FindCodexProcesses(codexExe).Any();
    }

    private static bool StopRunningCodex()
    {
        var codexExe = FindCodexExe();
        if (codexExe is null)
        {
            return false;
        }

        var processes = FindCodexProcesses(codexExe).ToList();
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    if (process.CloseMainWindow())
                    {
                        if (process.WaitForExit(3000))
                        {
                            continue;
                        }
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                }
            }
        }

        return processes.Count > 0;
    }

    private static void StopStaleNodeReplProcesses()
    {
        var codexExe = FindCodexExe();
        if (codexExe is null)
        {
            return;
        }

        var installDir = Path.GetDirectoryName(codexExe);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return;
        }

        var runtimeRoot = Path.GetFullPath(RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Path.Combine(installDir, "..", "Resources", "cua_node")
            : Path.Combine(installDir, "resources", "cua_node"));
        foreach (var process in Process.GetProcessesByName("node_repl"))
        {
            using (process)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath))
                    {
                        continue;
                    }

                    var fullProcessPath = Path.GetFullPath(processPath);
                    if (!fullProcessPath.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                }
            }
        }
    }

    private static IEnumerable<Process> FindCodexProcesses(string codexExe)
    {
        var processName = Path.GetFileNameWithoutExtension(codexExe);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            if (process.Id == Environment.ProcessId)
            {
                process.Dispose();
                continue;
            }

            if (IsSameProcessExecutable(process, codexExe))
            {
                yield return process;
            }
            else
            {
                process.Dispose();
            }
        }
    }

    private static bool IsSameProcessExecutable(Process process, string expectedExe)
    {
        try
        {
            var actual = process.MainModule?.FileName;
            return actual is null || string.Equals(
                Path.GetFullPath(actual),
                Path.GetFullPath(expectedExe),
                PathComparison);
        }
        catch
        {
            return false;
        }
    }

    private void StartApiProxyIfNeeded()
    {
        if (!IsFreeProvider)
        {
            StopRunningApiProxy();
            return;
        }

        var codexExe = FindCodexExe();
        if (codexExe is null)
        {
            throw new FileNotFoundException("未找到 Codex");
        }

        StopRunningApiProxy();

        if (IsProxyHealthy())
        {
            return;
        }

        var proxyExe = FindProxyExe();
        if (proxyExe is null)
        {
            throw new FileNotFoundException("未找到 API 转换器");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = proxyExe,
            WorkingDirectory = Path.GetDirectoryName(proxyExe),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(ApiProxyPort.ToString());
        startInfo.ArgumentList.Add("--upstream");
        startInfo.ArgumentList.Add(FreeUpstreamBaseUrl);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            startInfo.ArgumentList.Add("--codex-exe");
            startInfo.ArgumentList.Add(codexExe);
        }

        Process.Start(startInfo);

        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(150);
            if (IsProxyHealthy())
            {
                return;
            }
        }

        throw new InvalidOperationException("API 转换器启动失败");
    }

    private static void StopRunningApiProxy()
    {
        var proxyExe = FindProxyExe();
        if (proxyExe is null)
        {
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(proxyExe);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    if (!IsSameProcessExecutable(process, proxyExe))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                }
            }
        }
    }

    private bool IsProxyHealthy()
    {
        try
        {
            using var response = _httpClient.GetAsync($"http://127.0.0.1:{ApiProxyPort}/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindProxyExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "CodexApiProxy.exe"
            : "CodexApiProxy";
        var candidates = new[]
        {
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "..", fileName),
            Path.Combine(baseDir, "Proxy", fileName),
            Path.Combine(baseDir, "..", "Resources", fileName)
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string? FindCodexExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var macExecutableName = "Codex";
        var candidates = new[]
        {
            Path.Combine(baseDir, "Codex.exe"),
            Path.Combine(baseDir, "..", "Codex.exe"),
            Path.Combine("/Applications", "Codex 免费版.app", "Contents", "MacOS", macExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Codex 免费版.app", "Contents", "MacOS", macExecutableName),
            Path.Combine(baseDir, "Codex 免费版.app", "Contents", "MacOS", macExecutableName),
            Path.Combine(baseDir, "..", "Codex 免费版.app", "Contents", "MacOS", macExecutableName),
            Path.Combine(baseDir, "..", "Resources", "Codex 免费版.app", "Contents", "MacOS", macExecutableName)
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string? FindCodexApp()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine("/Applications", "Codex 免费版.app"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Codex 免费版.app"),
            Path.Combine(baseDir, "Codex 免费版.app"),
            Path.Combine(baseDir, "..", "Codex 免费版.app"),
            Path.Combine(baseDir, "..", "Resources", "Codex 免费版.app")
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    }

    private static void PrependBundledNodeToPath(ProcessStartInfo startInfo)
    {
        var nodeDir = FindBundledNodeDir();
        if (nodeDir is null)
        {
            return;
        }

        var existingPath = (startInfo.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH")) ?? "";
        var parts = existingPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !SameDirectory(part, nodeDir))
            .ToList();

        parts.Insert(0, nodeDir);
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, parts);
    }

    private static string? FindBundledNodeDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Tools", "Node"),
            Path.Combine(baseDir, "..", "Tools", "Node"),
            Path.Combine(baseDir, "..", "..", "Tools", "Node"),
            Path.Combine("/Applications", "Codex 免费版.app", "Contents", "Resources", "cua_node", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Codex 免费版.app", "Contents", "Resources", "cua_node", "bin"),
            Path.Combine(baseDir, "Codex 免费版.app", "Contents", "Resources", "cua_node", "bin"),
            Path.Combine(baseDir, "..", "Codex 免费版.app", "Contents", "Resources", "cua_node", "bin"),
            Path.Combine(baseDir, "..", "Resources", "Codex 免费版.app", "Contents", "Resources", "cua_node", "bin")
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node")));
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(Environment.ExpandEnvironmentVariables(right)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, PathComparison);
        }
        catch
        {
            return string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), PathComparison);
        }
    }

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void EnsureBundledResourcesInstalled()
    {
        var bundleDir = FindBundleDir();
        if (bundleDir is null)
        {
            return;
        }

        var codexDir = GetCodexDir();
        CopyDirectoryIfExists(Path.Combine(bundleDir, "Skills"), Path.Combine(codexDir, "skills"));
        CopyDirectoryIfExists(Path.Combine(bundleDir, "Plugins"), Path.Combine(codexDir, "plugins", "cache"));
    }

    private static string? FindBundleDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Bundle"),
            Path.Combine(baseDir, "..", "Bundle"),
            Path.Combine(baseDir, "..", "Resources", "Bundle")
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    }

    private static void CopyDirectoryIfExists(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

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

    private string GetProfileSetting(string id, string field, string fallback)
    {
        return DecodeSetting(GetRawSetting($"profile.{id}.{field}", ""), fallback);
    }

    private void SetProfileSetting(string id, string field, string value)
    {
        _settings[$"profile.{id}.{field}"] = EncodeSetting(value);
    }

    private string GetRawSetting(string key, string fallback)
    {
        return _settings.TryGetValue(key, out var value) ? value : fallback;
    }

    private static Dictionary<string, string> LoadSettings()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = GetLauncherSettingsPath();
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[line[..separator]] = line[(separator + 1)..];
        }

        return result;
    }

    private static string EncodeSetting(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string DecodeSetting(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return fallback;
        }
    }

    private static string NormalizeWireApi(string value)
    {
        return "responses";
    }

    private static string GetCodexDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Data", ".codex"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(GetMacAppSupportDir(), ".codex");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static string GetMacAppSupportDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "CodexFreeLauncher");
    }

    private static string GetLauncherSettingsDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "CodexLauncher");
    }

    private static string GetLauncherSettingsPath()
    {
        return Path.Combine(GetLauncherSettingsDir(), "profiles.ini");
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static void RemoveLauncherApiKeyAuth(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return;
        }

        var text = File.ReadAllText(authPath, Encoding.UTF8);
        if (text.Contains("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("access_token", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("refresh_token", StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(authPath);
        }
    }

    private void WriteCodexModelCatalog(string codexDir)
    {
        CodexModelCatalog.Write(codexDir, GetLauncherModelIds());
    }

    private List<string> GetLauncherModelIds()
    {
        var models = ModelOptions.Count > 0
            ? ModelOptions.ToList()
            : [Model];

        if (!string.IsNullOrWhiteSpace(Model) && models.All(modelId => !string.Equals(modelId, Model, StringComparison.OrdinalIgnoreCase)))
        {
            models.Insert(0, Model);
        }

        return models
            .Select(modelId => modelId.Trim())
            .Where(modelId => modelId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AppendCatalogEntry(StringBuilder builder, string modelId, int priority)
    {
        var displayName = BuildDisplayName(modelId);
        builder.AppendLine("    {");
        builder.AppendLine($"      \"slug\": {JsonString(modelId)},");
        builder.AppendLine($"      \"display_name\": {JsonString(displayName)},");
        builder.AppendLine($"      \"description\": {JsonString(displayName)},");
        builder.AppendLine("      \"base_instructions\": \"You are Codex, a coding agent. You and the user share the same workspace and collaborate to achieve the user's goals.\",");
        builder.AppendLine("      \"default_reasoning_level\": \"high\",");
        builder.AppendLine("      \"supported_reasoning_levels\": [");
        builder.AppendLine("        { \"effort\": \"none\", \"description\": \"Disable Thinking\" },");
        builder.AppendLine("        { \"effort\": \"high\", \"description\": \"Enabled Thinking\" }");
        builder.AppendLine("      ],");
        builder.AppendLine("      \"shell_type\": \"shell_command\",");
        builder.AppendLine("      \"visibility\": \"list\",");
        builder.AppendLine("      \"supported_in_api\": true,");
        builder.AppendLine($"      \"priority\": {priority},");
        builder.AppendLine("      \"supports_reasoning_summaries\": true,");
        builder.AppendLine("      \"default_reasoning_summary\": \"none\",");
        builder.AppendLine("      \"support_verbosity\": false,");
        builder.AppendLine("      \"truncation_policy\": { \"mode\": \"bytes\", \"limit\": 10000 },");
        builder.AppendLine("      \"supports_parallel_tool_calls\": false,");
        builder.AppendLine("      \"supports_image_detail_original\": false,");
        builder.AppendLine("      \"context_window\": 262144,");
        builder.AppendLine("      \"max_context_window\": 262144,");
        builder.AppendLine("      \"effective_context_window_percent\": 95,");
        builder.AppendLine("      \"experimental_supported_tools\": [],");
        builder.AppendLine("      \"input_modalities\": [\"text\"],");
        builder.AppendLine("      \"supports_search_tool\": false,");
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

    private static void RemoveLauncherModelCatalog(string codexDir)
    {
        var path = Path.Combine(codexDir, CodexModelCatalog.FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string TomlString(string value)
    {
        return JsonString(value);
    }

    private static string TomlStringArray(IEnumerable<string> values)
    {
        return "[" + string.Join(", ", values.Select(TomlString)) + "]";
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
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch
            });
        }
        builder.Append('"');
        return builder.ToString();
    }

    private sealed record CodexProviderProfile(
        string Id,
        string Name,
        string ProviderName,
        string BaseUrl,
        string Model,
        string WireApi,
        string[] DefaultModels,
        bool LoadModelsFromApi,
        string[] AllowedModelIds);
}
