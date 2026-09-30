using System.Text.Json.Serialization;

namespace AgentHub.Core.McpCore;

// 列表合同保留已有 JSON 字段；错误只标示读取状态，不包含配置原文。
public sealed record McpListResponse(
    [property: JsonPropertyName("motherPath")] string MotherPath,
    [property: JsonPropertyName("motherEmpty")] bool MotherEmpty,
    [property: JsonPropertyName("targets")] IReadOnlyDictionary<string, bool> Targets,
    [property: JsonPropertyName("excludeNames")] IReadOnlyList<string> ExcludeNames,
    [property: JsonPropertyName("adapters")] IReadOnlyList<McpAdapterInfo> Adapters,
    [property: JsonPropertyName("items")] IReadOnlyList<McpListItem> Items);

public sealed record McpAdapterInfo(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("configPath")] string ConfigPath,
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("error")] string? Error);

public sealed record McpAgentInfo(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("presence")] string Presence,
    [property: JsonPropertyName("enabledOnAgent")] bool? EnabledOnAgent,
    [property: JsonPropertyName("drift")] bool Drift,
    [property: JsonPropertyName("configPath")] string ConfigPath,
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record McpListItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("args")] IReadOnlyList<string> Args,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string> Env,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("startupTimeoutSec")] int? StartupTimeoutSec,
    [property: JsonPropertyName("timeoutMs")] int? TimeoutMs,
    [property: JsonPropertyName("explicitType")] string? ExplicitType,
    [property: JsonPropertyName("inMother")] bool InMother,
    [property: JsonPropertyName("hasSecretRisk")] bool HasSecretRisk,
    [property: JsonPropertyName("agents")] IReadOnlyList<McpAgentInfo> Agents);

public sealed record McpApiError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("ok")] bool Ok = false);

public sealed record McpRawResponse(
    string Id, string Transport, string? Command, IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env, string? Url, IReadOnlyDictionary<string, string> Headers,
    bool Enabled, int? StartupTimeoutSec, int? TimeoutMs, string? Alias, string? Note,
    string? ExplicitType, bool HasSecretRisk);

public sealed record McpUpsertAgentsResponse(
    bool Ok, bool HasSecretRisk, IReadOnlyList<McpSyncItemResult> Items, string? Error);
