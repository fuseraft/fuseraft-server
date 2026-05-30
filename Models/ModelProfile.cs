using System.Text.Json.Serialization;

namespace fuseraft.Server.Models;

public sealed record ModelProfile
{
    public string Id       { get; init; } = string.Empty;
    public string Name     { get; init; } = string.Empty;
    public string ModelId  { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;

    public string? ReasoningEffort { get; init; }

    [JsonIgnore]
    public string ApiKeyEnvVar => $"FUSERAFT_PROFILE_{Id}_KEY";
}
