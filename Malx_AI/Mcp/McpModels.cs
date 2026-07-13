using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Malx_AI.Mcp
{
    public enum McpConnectorKind
    {
        Gmail,
        GoogleDrive,
        GitHub,
        Todoist
    }

    public sealed class McpConnectorInfo
    {
        public required string Id { get; init; }
        /// <summary>@mention handle without '@' (e.g. Gmail, GoogleDrive).</summary>
        public required string Handle { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public required McpConnectorKind Kind { get; init; }
        public required string LogoGlyph { get; init; }
        public bool IsConnected { get; set; }
        public string? AccountLabel { get; set; }
        public DateTime? ConnectedAtUtc { get; set; }
    }

    public sealed class McpToolDefinition
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string ConnectorId { get; init; }
        public required JsonObject ParametersSchema { get; init; }
    }

    public sealed class McpTokenBundle
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
        public string AccountEmail { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
    }

    public sealed class McpConnectorStateFile
    {
        public string GoogleOAuthClientId { get; set; } = string.Empty;
        public Dictionary<string, McpTokenBundle> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class McpToolExecutionResult
    {
        public string Name { get; init; } = string.Empty;
        public string Result { get; init; } = string.Empty;
        public bool Success { get; init; }
    }

    public readonly record struct McpMentionSpan(int Start, int Length, string Handle, bool IsComplete);
}
