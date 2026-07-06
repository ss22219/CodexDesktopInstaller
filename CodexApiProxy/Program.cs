using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
{
    Environment.Exit(CodexApiProxy.RunSelfTest());
    return;
}

var options = ProxyOptions.Parse(args);
using var singleInstance = SingleInstanceMutex.TryAcquire(options.Port);
if (singleInstance is null)
{
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var codexMonitor = CodexProcessMonitor.RunAsync(options, shutdown);
await new CodexApiProxy(options).RunAsync(shutdown.Token);
shutdown.Cancel();
await codexMonitor;

internal sealed record ProxyOptions(int Port, string Upstream, string ApiKey, int? CodexProcessId, string? CodexExe)
{
    public static ProxyOptions Parse(string[] args)
    {
        var port = 17631;
        var upstream = "https://opencode.ai/zen/v1";
        var apiKey = "";
        int? codexProcessId = null;
        string? codexExe = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var parsedPort))
            {
                port = parsedPort;
            }
            else if (arg == "--upstream" && i + 1 < args.Length)
            {
                upstream = args[++i].Trim();
            }
            else if (arg == "--api-key" && i + 1 < args.Length)
            {
                apiKey = args[++i];
            }
            else if ((arg == "--codex-pid" || arg == "--parent-pid")
                     && i + 1 < args.Length
                     && int.TryParse(args[++i], out var parsedProcessId)
                     && parsedProcessId > 0)
            {
                codexProcessId = parsedProcessId;
            }
            else if (arg == "--codex-exe" && i + 1 < args.Length)
            {
                codexExe = args[++i].Trim();
            }
        }

        return new ProxyOptions(port, upstream.TrimEnd('/'), apiKey, codexProcessId, codexExe);
    }
}

internal sealed class SingleInstanceMutex : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceMutex(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceMutex? TryAcquire(int port)
    {
        var mutexName = $@"Local\CodexApiProxy_{port}";
        var mutex = new Mutex(false, mutexName);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstanceMutex(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceMutex(mutex);
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}

internal static class CodexProcessMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupWaitTimeout = TimeSpan.FromSeconds(60);

    public static async Task RunAsync(ProxyOptions options, CancellationTokenSource shutdown)
    {
        if (options.CodexProcessId is null && string.IsNullOrWhiteSpace(options.CodexExe))
        {
            return;
        }

        try
        {
            var seenCodex = false;
            var deadline = DateTimeOffset.UtcNow + StartupWaitTimeout;
            while (!shutdown.IsCancellationRequested)
            {
                var running = options.CodexProcessId is { } processId
                    ? IsProcessRunning(processId)
                    : IsCodexExecutableRunning(options.CodexExe!);

                if (running)
                {
                    seenCodex = true;
                }
                else if (seenCodex || DateTimeOffset.UtcNow >= deadline)
                {
                    shutdown.Cancel();
                    return;
                }

                await Task.Delay(PollInterval, shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCodexExecutableRunning(string codexExe)
    {
        var expectedPath = Path.GetFullPath(codexExe);
        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var actualPath = process.MainModule?.FileName;
                    if (actualPath is null
                        || string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal sealed class CodexApiProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly string[] FreeModels =
    [
        "deepseek-v4-flash-free",
        "north-mini-code-free",
        "mimo-v2.5-free",
        "nemotron-3-ultra-free"
    ];

    private readonly ProxyOptions _options;
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    private static readonly object DebugLogLock = new();

    public CodexApiProxy(ProxyOptions options)
    {
        _options = options;
    }

    public static int RunSelfTest()
    {
        try
        {
            var sample = JsonNode.Parse("""
            {
              "model": "deepseek-v4-flash-free",
              "input": [
                {
                  "type": "function_call",
                  "call_id": "call_123",
                  "name": "list_mcp_resources",
                  "arguments": "{}"
                },
                {
                  "type": "function_call_output",
                  "call_id": "call_123",
                  "output": { "resources": [] }
                }
              ],
              "tools": []
            }
            """)!.AsObject();
            var chat = BuildChatRequest(sample);
            var messages = chat["messages"] as JsonArray
                ?? throw new InvalidOperationException("missing messages");
            if (messages.Count != 2)
            {
                throw new InvalidOperationException($"unexpected message count: {messages.Count}");
            }

            var assistant = messages[0] as JsonObject
                ?? throw new InvalidOperationException("missing assistant message");
            var tool = messages[1] as JsonObject
                ?? throw new InvalidOperationException("missing tool message");

            if (NodeText(assistant["role"]) != "assistant"
                || assistant["tool_calls"] is not JsonArray toolCalls
                || toolCalls.Count != 1)
            {
                throw new InvalidOperationException("assistant tool_calls were not preserved");
            }

            if (NodeText(assistant["reasoning_content"]) != "tool call")
            {
                throw new InvalidOperationException("missing DeepSeek tool-call reasoning placeholder");
            }

            if (NodeText(tool["role"]) != "tool"
                || NodeText(tool["tool_call_id"]) != "call_123"
                || !NodeText(tool["content"]).Contains("\"resources\":[]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("tool result was not converted to role=tool");
            }

            var reasoningSample = JsonNode.Parse("""
            {
              "model": "deepseek-v4-flash-free",
              "input": [
                {
                  "type": "reasoning",
                  "summary": [
                    { "type": "summary_text", "text": "Need to call the tool." }
                  ]
                },
                {
                  "type": "function_call",
                  "call_id": "call_456",
                  "name": "list_mcp_resources",
                  "arguments": "{}"
                }
              ],
              "tools": []
            }
            """)!.AsObject();
            var reasoningChat = BuildChatRequest(reasoningSample);
            var reasoningMessages = reasoningChat["messages"] as JsonArray
                ?? throw new InvalidOperationException("missing reasoning messages");
            var reasoningAssistant = reasoningMessages[0] as JsonObject
                ?? throw new InvalidOperationException("missing reasoning assistant message");
            if (NodeText(reasoningAssistant["reasoning_content"]) != "Need to call the tool.")
            {
                throw new InvalidOperationException("reasoning_content was not restored to assistant tool call");
            }

            var chatResponse = JsonNode.Parse("""
            {
              "id": "chatcmpl_selftest",
              "model": "deepseek-v4-flash-free",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "reasoning_content": "Need to call the tool.",
                    "content": "",
                    "tool_calls": [
                      {
                        "id": "call_789",
                        "type": "function",
                        "function": {
                          "name": "list_mcp_resources",
                          "arguments": "{}"
                        }
                      }
                    ]
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 1,
                "completion_tokens": 1,
                "total_tokens": 2
              }
            }
            """)!.AsObject();
            var responses = ChatToResponses(chatResponse, sample);
            var output = responses["output"] as JsonArray
                ?? throw new InvalidOperationException("missing response output");
            if (output.Count < 2
                || NodeText(output[0]?["type"]) != "reasoning"
                || NodeText(output[1]?["reasoning_content"]) != "Need to call the tool.")
            {
                throw new InvalidOperationException("chat reasoning_content was not preserved in Responses output");
            }

            Console.WriteLine("CodexApiProxy self-test passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("CodexApiProxy self-test failed: " + ex.Message);
            return 1;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, _options.Port);
        listener.Start();
        using var registration = cancellationToken.Register(listener.Stop);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = await HttpRequest.ReadAsync(stream);
            if (request is null)
            {
                return;
            }

            if (request.Method == "GET" && request.Path is "/health" or "/v1/health")
            {
                await WriteJsonAsync(stream, 200, new JsonObject { ["ok"] = true });
                return;
            }

            if (request.Method == "GET" && IsModelsPath(request.Path))
            {
                await ForwardModelsAsync(stream, request);
                return;
            }

            if (request.Method != "POST" || !IsResponsesPath(request.Path))
            {
                await WriteJsonAsync(stream, 404, ErrorJson("Not found"));
                return;
            }

            await HandleResponsesAsync(stream, request);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(client.GetStream(), 500, ErrorJson(ex.Message));
            }
            catch
            {
            }
        }
    }

    private static bool IsResponsesPath(string path)
    {
        var clean = path.Split('?', 2)[0];
        return clean is "/responses" or "/v1/responses" or "/responses/compact" or "/v1/responses/compact";
    }

    private static bool IsModelsPath(string path)
    {
        var clean = path.Split('?', 2)[0];
        return clean is "/models" or "/v1/models";
    }

    private async Task ForwardModelsAsync(NetworkStream stream, HttpRequest incoming)
    {
        if (UsesBuiltInFreeModels())
        {
            await WriteJsonAsync(stream, 200, BuildLocalModelsResponse());
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.Upstream}/models");
        ApplyAuth(request, incoming);
        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await WriteRawAsync(stream, (int)response.StatusCode, contentType, body);
    }

    private bool UsesBuiltInFreeModels()
    {
        return string.IsNullOrWhiteSpace(_options.ApiKey)
            && _options.Upstream.Contains("opencode.ai/zen", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject BuildLocalModelsResponse()
    {
        var data = new JsonArray();
        foreach (var model in FreeModels)
        {
            JsonNode modelNode = new JsonObject
            {
                ["id"] = model,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "opencode-free"
            };
            data.Add(modelNode);
        }

        return new JsonObject
        {
            ["object"] = "list",
            ["data"] = data
        };
    }

    private async Task HandleResponsesAsync(NetworkStream stream, HttpRequest incoming)
    {
        var bodyText = Encoding.UTF8.GetString(incoming.Body);
        var responseRequest = JsonNode.Parse(bodyText)?.AsObject()
            ?? throw new InvalidOperationException("Invalid JSON body");
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var wantsStream = responseRequest["stream"]?.GetValue<bool>() == true;
        WriteDebugLog($"request {requestId} model={NodeText(responseRequest["model"])} stream={wantsStream} incoming_tools={ToolSummary(responseRequest["tools"])}");
        WriteDebugLog($"request {requestId} special_tools={SpecialToolSummary(responseRequest["tools"])}");
        var chatRequest = BuildChatRequest(responseRequest);
        WriteDebugLog($"request {requestId} chat_tools={ToolSummary(chatRequest["tools"])}");

        var chatResponse = await PostChatAsync(chatRequest, incoming);

        using (chatResponse)
        {
            var responseBytes = await chatResponse.Content.ReadAsByteArrayAsync();
            WriteDebugLog($"request {requestId} upstream_status={(int)chatResponse.StatusCode}");
            if (!chatResponse.IsSuccessStatusCode)
            {
                await WriteJsonAsync(stream, (int)chatResponse.StatusCode, NormalizeError(responseBytes, (int)chatResponse.StatusCode));
                return;
            }

            var chatJson = JsonNode.Parse(responseBytes)?.AsObject()
                ?? throw new InvalidOperationException("Invalid upstream JSON body");
            var responsesJson = ChatToResponses(chatJson, responseRequest);
            if (wantsStream)
            {
                await WriteSseAsync(stream, responsesJson);
            }
            else
            {
                await WriteJsonAsync(stream, 200, responsesJson);
            }
        }
    }

    private async Task<HttpResponseMessage> PostChatAsync(JsonObject chatRequest, HttpRequest incoming)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.Upstream}/chat/completions");
        request.Content = new StringContent(chatRequest.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        ApplyAuth(request, incoming);
        return await _httpClient.SendAsync(request);
    }

    private void ApplyAuth(HttpRequestMessage request, HttpRequest incoming)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
            return;
        }

        // Free opencode requests must not inherit the user's OpenAI/Codex auth header.
    }

    private static JsonObject BuildChatRequest(JsonObject responseRequest)
    {
        var messages = new JsonArray();
        var model = NodeText(responseRequest["model"], "north-mini-code-free");
        var requiresReasoningContent = RequiresReasoningContent(model);
        var instructions = NodeText(responseRequest["instructions"]);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            AddNode(messages, Message("system", instructions));
        }

        if (responseRequest["messages"] is JsonArray rawMessages)
        {
            AddMessageNodes(messages, rawMessages, requiresReasoningContent);
        }
        else
        {
            AddInputMessages(messages, responseRequest["input"], requiresReasoningContent);
        }

        if (messages.Count == 0)
        {
            AddNode(messages, Message("user", ""));
        }

        var chat = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false
        };

        var temperature = responseRequest["temperature"]?.DeepClone();
        if (temperature is not null)
        {
            chat["temperature"] = temperature;
        }

        var tools = NormalizeTools(responseRequest["tools"]);
        if (tools.Count > 0)
        {
            chat["tools"] = tools;
            chat["tool_choice"] = "auto";
        }

        return chat;
    }

    private static bool RequiresReasoningContent(string model)
    {
        return model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Contains("kimi", StringComparison.OrdinalIgnoreCase)
            || model.Contains("moonshot", StringComparison.OrdinalIgnoreCase)
            || model.Contains("mimo", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddInputMessages(JsonArray messages, JsonNode? input, bool requiresReasoningContent)
    {
        if (input is null)
        {
            return;
        }

        if (input is JsonValue)
        {
            var text = NodeText(input);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AddNode(messages, Message("user", text));
            }

            return;
        }

        if (input is not JsonArray array)
        {
            return;
        }

        AddMessageNodes(messages, array, requiresReasoningContent);
    }

    private static void AddMessageNodes(JsonArray messages, JsonArray array, bool requiresReasoningContent)
    {
        string? pendingReasoning = null;
        foreach (var item in array)
        {
            AddMessageNode(messages, item, requiresReasoningContent, ref pendingReasoning);
        }
    }

    private static void AddMessageNode(
        JsonArray messages,
        JsonNode? node,
        bool requiresReasoningContent,
        ref string? pendingReasoning)
    {
        if (node is not JsonObject obj)
        {
            var text = NodeText(node);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AddNode(messages, Message("user", text));
            }

            return;
        }

        var type = NodeText(obj["type"]);
        if (type == "reasoning")
        {
            AppendReasoning(ref pendingReasoning, ReasoningText(obj));
            return;
        }

        if (type == "function_call")
        {
            var reasoning = TakeReasoning(obj, ref pendingReasoning);
            AddNode(messages, AssistantToolCallMessage(obj, reasoning, requiresReasoningContent));
            return;
        }

        if (type == "function_call_output")
        {
            var callId = NodeText(obj["call_id"]);
            var output = ToolResultText(obj["output"]);
            AddNode(messages, ToolMessage(callId, output));
            return;
        }

        if (type == "tool_search_call")
        {
            var reasoning = TakeReasoning(obj, ref pendingReasoning);
            AddNode(messages, AssistantToolCallMessage(new JsonObject
            {
                ["call_id"] = NodeText(obj["call_id"], NodeText(obj["id"], "call_" + Guid.NewGuid().ToString("N"))),
                ["name"] = "tool_search",
                ["arguments"] = JsonArgumentText(obj["arguments"])
            }, reasoning, requiresReasoningContent));
            return;
        }

        if (type == "tool_search_output")
        {
            var callId = NodeText(obj["call_id"]);
            var tools = obj["tools"]?.DeepClone() ?? obj["output"]?.DeepClone() ?? obj.DeepClone();
            AddNode(messages, ToolMessage(callId, ToolResultText(tools)));
            return;
        }

        var role = NormalizeRole(NodeText(obj["role"], "user"));
        var content = NodeText(obj["content"]);
        if (string.IsNullOrWhiteSpace(content) && type == "message")
        {
            content = NodeText(obj);
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            var message = Message(role, content);
            if (role == "assistant")
            {
                var reasoning = TakeReasoning(obj, ref pendingReasoning);
                if (!string.IsNullOrWhiteSpace(reasoning))
                {
                    message["reasoning_content"] = reasoning;
                }
            }

            AddNode(messages, message);
        }
    }

    private static void AppendReasoning(ref string? pendingReasoning, string reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return;
        }

        pendingReasoning = string.IsNullOrWhiteSpace(pendingReasoning)
            ? reasoning
            : pendingReasoning + "\n\n" + reasoning;
    }

    private static string TakeReasoning(JsonObject obj, ref string? pendingReasoning)
    {
        var reasoning = ReasoningText(obj);
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            reasoning = pendingReasoning ?? "";
        }

        pendingReasoning = null;
        return reasoning;
    }

    private static JsonObject Message(string role, string content)
    {
        return new JsonObject
        {
            ["role"] = role,
            ["content"] = content
        };
    }

    private static JsonObject AssistantToolCallMessage(
        JsonObject functionCall,
        string? reasoningContent = null,
        bool requiresReasoningContent = false)
    {
        var callId = NodeText(functionCall["call_id"], NodeText(functionCall["id"], "call_" + Guid.NewGuid().ToString("N")));
        var name = NodeText(functionCall["name"]);
        var arguments = NodeText(functionCall["arguments"], "{}");
        var toolCalls = new JsonArray();
        AddNode(toolCalls, new JsonObject
        {
            ["id"] = callId,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments
            }
        });

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["tool_calls"] = toolCalls
        };

        if (string.IsNullOrWhiteSpace(reasoningContent) && requiresReasoningContent)
        {
            reasoningContent = "tool call";
        }

        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            message["reasoning_content"] = reasoningContent;
        }

        return message;
    }

    private static JsonObject ToolMessage(string callId, string content)
    {
        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = string.IsNullOrWhiteSpace(callId) ? "call_" + Guid.NewGuid().ToString("N") : callId,
            ["content"] = content
        };
    }

    private static void AddNode(JsonArray array, JsonNode? node)
    {
        ((IList<JsonNode?>)array).Add(node);
    }

    private static string NormalizeRole(string role)
    {
        return role switch
        {
            "assistant" => "assistant",
            "system" => "system",
            "developer" => "system",
            "tool" => "user",
            _ => "user"
        };
    }

    private static JsonArray NormalizeTools(JsonNode? rawTools)
    {
        var result = new JsonArray();
        if (rawTools is not JsonArray tools)
        {
            return result;
        }

        foreach (var rawTool in tools)
        {
            if (rawTool is not JsonObject tool)
            {
                continue;
            }

            if (NodeText(tool["type"]) != "function")
            {
                if (TryAddSpecialTool(result, tool))
                {
                    continue;
                }

                continue;
            }

            var function = tool["function"] as JsonObject;
            var name = NodeText(function?["name"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = NodeText(tool["name"]);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalizedFunction = new JsonObject
            {
                ["name"] = name,
                ["description"] = NodeText(function?["description"], NodeText(tool["description"]))
            };

            var parameters = function?["parameters"]?.DeepClone()
                ?? tool["parameters"]?.DeepClone()
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            normalizedFunction["parameters"] = parameters;

            AddNode(result, new JsonObject
            {
                ["type"] = "function",
                ["function"] = normalizedFunction
            });
        }

        return result;
    }

    private static bool TryAddSpecialTool(JsonArray result, JsonObject tool)
    {
        var type = NodeText(tool["type"]);
        if (type == "tool_search")
        {
            AddFunctionTool(
                result,
                "tool_search",
                NodeText(tool["description"], "Search deferred tool metadata and expose matching tools for the next model call."),
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Search query for the tool to expose, for example node_repl js."
                        },
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1,
                            ["description"] = "Optional maximum number of matching tools to return."
                        }
                    },
                    ["required"] = new JsonArray("query")
                });
            return true;
        }

        return false;
    }

    private static void AddFunctionTool(JsonArray tools, string name, string description, JsonObject parameters)
    {
        if (HasTool(tools, name))
        {
            return;
        }

        AddNode(tools, new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters
            }
        });
    }

    private static bool HasTool(JsonArray tools, string name)
    {
        foreach (var rawTool in tools)
        {
            if (rawTool is not JsonObject tool)
            {
                continue;
            }

            var function = tool["function"] as JsonObject;
            if (string.Equals(NodeText(function?["name"]), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject ChatToResponses(JsonObject chat, JsonObject originalRequest)
    {
        var responseId = NodeText(chat["id"], "resp_" + Guid.NewGuid().ToString("N"));
        var model = NodeText(chat["model"], NodeText(originalRequest["model"], "unknown"));
        var output = new JsonArray();
        var dynamicTools = DynamicToolNamespaces(originalRequest);
        var choice = chat["choices"] is JsonArray choices && choices.Count > 0
            ? choices[0] as JsonObject
            : null;
        var message = choice?["message"] as JsonObject;
        var toolCalls = message?["tool_calls"] as JsonArray;
        var reasoningContent = NodeText(message?["reasoning_content"]);

        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            AddNode(output, ReasoningOutput(reasoningContent));
        }

        if (toolCalls is not null && toolCalls.Count > 0)
        {
            foreach (var toolCall in toolCalls)
            {
                if (toolCall is not JsonObject call)
                {
                    continue;
                }

                var function = call["function"] as JsonObject;
                var functionName = NodeText(function?["name"]);
                var callId = NodeText(call["id"], "call_" + Guid.NewGuid().ToString("N"));
                var arguments = NodeText(function?["arguments"], "{}");
                if (functionName == "tool_search")
                {
                    var toolSearch = new JsonObject
                    {
                        ["id"] = "ts_" + Guid.NewGuid().ToString("N"),
                        ["type"] = "tool_search_call",
                        ["status"] = "completed",
                        ["call_id"] = callId,
                        ["execution"] = "client",
                        ["arguments"] = ParseJsonNode(arguments)
                    };
                    AttachReasoning(toolSearch, reasoningContent);
                    AddNode(output, toolSearch);
                    continue;
                }

                if (TryResolveDynamicTool(dynamicTools, functionName, out var dynamicNamespace, out var dynamicName))
                {
                    var dynamicCall = new JsonObject
                    {
                        ["id"] = "fc_" + Guid.NewGuid().ToString("N"),
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = callId,
                        ["namespace"] = dynamicNamespace,
                        ["name"] = dynamicName,
                        ["arguments"] = arguments
                    };
                    AttachReasoning(dynamicCall, reasoningContent);
                    AddNode(output, dynamicCall);
                    continue;
                }

                var functionCall = new JsonObject
                {
                    ["id"] = "fc_" + Guid.NewGuid().ToString("N"),
                    ["type"] = "function_call",
                    ["status"] = "completed",
                    ["call_id"] = callId,
                    ["name"] = functionName,
                    ["arguments"] = arguments
                };
                AttachReasoning(functionCall, reasoningContent);
                AddNode(output, functionCall);
            }
        }

        if (output.Count == 0)
        {
            var text = NodeText(message?["content"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = reasoningContent;
            }

            AddNode(output, MessageOutput(text));
        }

        return new JsonObject
        {
            ["id"] = responseId,
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["status"] = "completed",
            ["output"] = output,
            ["usage"] = UsageToResponses(chat["usage"])
        };
    }

    private static void AttachReasoning(JsonObject item, string reasoningContent)
    {
        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            item["reasoning_content"] = reasoningContent;
        }
    }

    private static JsonObject ReasoningOutput(string text)
    {
        var summary = new JsonArray();
        AddNode(summary, new JsonObject
        {
            ["type"] = "summary_text",
            ["text"] = text
        });

        return new JsonObject
        {
            ["id"] = "rs_" + Guid.NewGuid().ToString("N"),
            ["type"] = "reasoning",
            ["summary"] = summary
        };
    }

    private static List<(string Namespace, HashSet<string> Tools)> DynamicToolNamespaces(JsonNode? node)
    {
        var namespaces = new List<(string Namespace, HashSet<string> Tools)>();
        CollectDynamicToolNamespaces(node, namespaces);
        return namespaces;
    }

    private static void CollectDynamicToolNamespaces(JsonNode? node, List<(string Namespace, HashSet<string> Tools)> namespaces)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectDynamicToolNamespaces(item, namespaces);
            }

            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        if (NodeText(obj["type"]) == "namespace")
        {
            var namespaceName = NodeText(obj["name"]);
            if (!string.IsNullOrWhiteSpace(namespaceName) && obj["tools"] is JsonArray tools)
            {
                var toolNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rawTool in tools)
                {
                    if (rawTool is JsonObject tool)
                    {
                        var toolName = NodeText(tool["name"]);
                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            toolNames.Add(toolName);
                        }
                    }
                }

                if (toolNames.Count > 0)
                {
                    namespaces.Add((namespaceName, toolNames));
                }
            }
        }

        foreach (var pair in obj)
        {
            CollectDynamicToolNamespaces(pair.Value, namespaces);
        }
    }

    private static bool TryResolveDynamicTool(
        List<(string Namespace, HashSet<string> Tools)> dynamicTools,
        string functionName,
        out string dynamicNamespace,
        out string dynamicName)
    {
        foreach (var toolNamespace in dynamicTools)
        {
            if (toolNamespace.Tools.Contains(functionName))
            {
                dynamicNamespace = toolNamespace.Namespace;
                dynamicName = functionName;
                return true;
            }

            var prefix = toolNamespace.Namespace + "__";
            if (functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                var unqualifiedName = functionName[prefix.Length..];
                if (toolNamespace.Tools.Contains(unqualifiedName))
                {
                    dynamicNamespace = toolNamespace.Namespace;
                    dynamicName = unqualifiedName;
                    return true;
                }
            }
        }

        dynamicNamespace = "";
        dynamicName = functionName;
        return false;
    }

    private static JsonObject MessageOutput(string text)
    {
        var content = new JsonArray();
        AddNode(content, new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray()
        });

        return new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = content
        };
    }

    private static JsonObject UsageToResponses(JsonNode? usage)
    {
        if (usage is not JsonObject obj)
        {
            return new JsonObject
            {
                ["input_tokens"] = 0,
                ["output_tokens"] = 0,
                ["total_tokens"] = 0
            };
        }

        var input = NodeInt(obj["prompt_tokens"]);
        var output = NodeInt(obj["completion_tokens"]);
        var total = NodeInt(obj["total_tokens"], input + output);
        return new JsonObject
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["total_tokens"] = total
        };
    }

    private static JsonObject NormalizeError(byte[] body, int status)
    {
        var text = Encoding.UTF8.GetString(body);
        var logPath = WriteErrorLog(status, text);
        var prefix = $"上游返回错误 HTTP {status}";
        try
        {
            var parsed = JsonNode.Parse(text);
            var message = NodeText(parsed?["error"]?["message"], NodeText(parsed?["message"], text));
            return ErrorJson($"{prefix}: {message}\n\n原始错误已保存: {logPath}\n\n{text}");
        }
        catch
        {
            return ErrorJson($"{prefix}\n\n原始错误已保存: {logPath}\n\n{text}");
        }
    }

    private static string WriteErrorLog(int status, string text)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexLauncher",
            "proxy-errors");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"proxy-error-{DateTime.Now:yyyyMMdd-HHmmss}-{status}.txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static JsonObject ErrorJson(string message)
    {
        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "invalid_request_error",
                ["code"] = "invalid_request_error",
                ["param"] = null
            }
        };
    }

    private static string ToolSummary(JsonNode? rawTools)
    {
        if (rawTools is not JsonArray tools)
        {
            return "0 []";
        }

        var names = new List<string>();
        foreach (var rawTool in tools)
        {
            if (rawTool is not JsonObject tool)
            {
                names.Add("<non-object>");
                continue;
            }

            var type = NodeText(tool["type"], "<missing-type>");
            var function = tool["function"] as JsonObject;
            var name = NodeText(function?["name"], NodeText(tool["name"], "<missing-name>"));
            names.Add($"{type}:{name}");
        }

        return $"{tools.Count} [{string.Join(", ", names.Take(20))}{(names.Count > 20 ? ", ..." : "")}]";
    }

    private static string SpecialToolSummary(JsonNode? rawTools)
    {
        if (rawTools is not JsonArray tools)
        {
            return "[]";
        }

        var snippets = new List<string>();
        foreach (var rawTool in tools)
        {
            if (rawTool is not JsonObject tool)
            {
                continue;
            }

            var type = NodeText(tool["type"]);
            if (type == "function")
            {
                continue;
            }

            var json = tool.ToJsonString(JsonOptions);
            snippets.Add(json.Length > 1200 ? json[..1200] + "..." : json);
        }

        return $"[{string.Join(" | ", snippets)}]";
    }

    private static void WriteDebugLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CodexLauncher");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "proxy-debug.log");
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            lock (DebugLogLock)
            {
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }

    private static string NodeText(JsonNode? node, string fallback = "")
    {
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text ?? fallback;
            }

            return node.ToJsonString(JsonOptions);
        }

        if (node is JsonArray array)
        {
            var parts = new List<string>();
            foreach (var item in array)
            {
                var text = TextFromContentPart(item);
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            return parts.Count == 0 ? fallback : string.Join(Environment.NewLine, parts);
        }

        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("text", out var textNode))
            {
                return NodeText(textNode, fallback);
            }

            if (obj.TryGetPropertyValue("content", out var contentNode))
            {
                return NodeText(contentNode, fallback);
            }
        }

        return fallback;
    }

    private static string ToolResultText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text ?? "";
        }

        return node.ToJsonString(JsonOptions);
    }

    private static string ReasoningText(JsonObject obj)
    {
        var direct = NodeText(obj["reasoning_content"]);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var summary = ReasoningSummaryText(obj["summary"]);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        return ReasoningSummaryText(obj["content"]);
    }

    private static string ReasoningSummaryText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue)
        {
            return NodeText(node);
        }

        if (node is JsonArray array)
        {
            var parts = new List<string>();
            foreach (var item in array)
            {
                var text = item is JsonObject obj
                    ? NodeText(obj["text"], NodeText(obj["summary_text"], NodeText(obj["content"])))
                    : NodeText(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }

            return string.Join("\n\n", parts);
        }

        return NodeText(node);
    }

    private static string JsonArgumentText(JsonNode? node)
    {
        if (node is null)
        {
            return "{}";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? "{}" : text;
        }

        return node.ToJsonString(JsonOptions);
    }

    private static JsonNode ParseJsonNode(string text)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) ?? new JsonObject();
        }
        catch
        {
            return new JsonObject { ["query"] = text };
        }
    }

    private static string TextFromContentPart(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue)
        {
            return NodeText(node);
        }

        if (node is not JsonObject obj)
        {
            return "";
        }

        var type = NodeText(obj["type"]);
        if (type is "input_text" or "output_text" or "text")
        {
            return NodeText(obj["text"]);
        }

        if (type == "function_call_output")
        {
            return NodeText(obj["output"]);
        }

        return NodeText(obj["text"]);
    }

    private static int NodeInt(JsonNode? node, int fallback = 0)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return fallback;
        }
    }

    private static async Task WriteSseAsync(NetworkStream stream, JsonObject response)
    {
        var message = FirstMessage(response);
        var text = message is null ? "" : NodeText(message["content"]);
        var head = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);

        await WriteEventAsync(stream, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = new JsonObject
            {
                ["id"] = NodeText(response["id"]),
                ["object"] = "response",
                ["status"] = "in_progress",
                ["model"] = NodeText(response["model"])
            }
        });

        if (message is not null)
        {
            await WriteEventAsync(stream, "response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = 0,
                ["item"] = message.DeepClone()
            });
            await WriteEventAsync(stream, "response.content_part.added", new JsonObject
            {
                ["type"] = "response.content_part.added",
                ["output_index"] = 0,
                ["content_index"] = 0,
                ["part"] = new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = "",
                    ["annotations"] = new JsonArray()
                }
            });
            if (!string.IsNullOrEmpty(text))
            {
                await WriteEventAsync(stream, "response.output_text.delta", new JsonObject
                {
                    ["type"] = "response.output_text.delta",
                    ["output_index"] = 0,
                    ["content_index"] = 0,
                    ["delta"] = text
                });
            }
            await WriteEventAsync(stream, "response.output_text.done", new JsonObject
            {
                ["type"] = "response.output_text.done",
                ["output_index"] = 0,
                ["content_index"] = 0,
                ["text"] = text
            });
            await WriteEventAsync(stream, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 0,
                ["item"] = message.DeepClone()
            });
        }
        else if (response["output"] is JsonArray output)
        {
            for (var i = 0; i < output.Count; i++)
            {
                if (output[i] is not JsonObject item)
                {
                    continue;
                }

                await WriteEventAsync(stream, "response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added",
                    ["output_index"] = i,
                    ["item"] = item.DeepClone()
                });
                await WriteEventAsync(stream, "response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done",
                    ["output_index"] = i,
                    ["item"] = item.DeepClone()
                });
            }
        }

        await WriteEventAsync(stream, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = response.DeepClone()
        });

        var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await stream.WriteAsync(done);
    }

    private static JsonObject? FirstMessage(JsonObject response)
    {
        if (response["output"] is not JsonArray output)
        {
            return null;
        }

        foreach (var item in output)
        {
            if (item is JsonObject obj && NodeText(obj["type"]) == "message")
            {
                return obj;
            }
        }

        return null;
    }

    private static async Task WriteEventAsync(NetworkStream stream, string eventName, JsonObject data)
    {
        var text = $"event: {eventName}\n" +
                   $"data: {data.ToJsonString(JsonOptions)}\n\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, JsonNode body)
    {
        await WriteRawAsync(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body.ToJsonString(JsonOptions)));
    }

    private static async Task WriteRawAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }
}

internal sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }

    public static async Task<HttpRequest?> ReadAsync(NetworkStream stream)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var buffer = new MemoryStream();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length));
                if (read <= 0)
                {
                    return null;
                }

                buffer.Write(rented, 0, read);
                headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
                if (buffer.Length > 1024 * 1024)
                {
                    throw new InvalidOperationException("Request header too large");
                }
            }

            var all = buffer.ToArray();
            var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var index = line.IndexOf(':');
                if (index > 0)
                {
                    headers[line[..index].Trim().ToLowerInvariant()] = line[(index + 1)..].Trim();
                }
            }

            var contentLength = headers.TryGetValue("content-length", out var value) && int.TryParse(value, out var parsed)
                ? parsed
                : 0;
            var bodyStart = headerEnd + 4;
            using var body = new MemoryStream();
            if (all.Length > bodyStart)
            {
                body.Write(all, bodyStart, all.Length - bodyStart);
            }

            while (body.Length < contentLength)
            {
                var need = Math.Min(rented.Length, contentLength - (int)body.Length);
                var read = await stream.ReadAsync(rented.AsMemory(0, need));
                if (read <= 0)
                {
                    break;
                }

                body.Write(rented, 0, read);
            }

            return new HttpRequest
            {
                Method = requestLine[0].ToUpperInvariant(),
                Path = requestLine[1],
                Headers = headers,
                Body = body.ToArray()
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }
}
