using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Kanban.Web.Features.Mcp;

[McpServerToolType]
public sealed class IdentityProbeTools
{
    [McpServerTool(Name = "identity_probe", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Checks authenticated MCP connectivity. Returns no company data and makes no task or permission changes.")]
    public static IdentityProbeResult Probe() => new(true, "kanban-product", true);
}

public sealed record IdentityProbeResult(bool Authenticated, string Stage, bool TaskAuthorizationImplemented);
