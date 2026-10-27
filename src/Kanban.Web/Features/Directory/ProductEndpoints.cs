using System.Security.Claims;
using System.Text.Json;
using Kanban.Core;
using Kanban.Web.Features.Session;
using Microsoft.AspNetCore.RateLimiting;

namespace Kanban.Web.Features.Directory;

public sealed record BrowserOperation;

public static class ProductEndpoints
{
    public static Caller Caller(HttpContext context)
    {
        var user = context.User;
        if (!Guid.TryParse(user.FindFirstValue("tid"), out var tenant) ||
            !Guid.TryParse(user.FindFirstValue("oid"), out var employee) || employee == Guid.Empty)
            throw new DomainException(401, "AUTHENTICATION_REQUIRED", "Sign in to continue.");
        Guid? client = null;
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            if (!Guid.TryParse(user.FindFirstValue("azp"), out var clientId))
                throw new DomainException(401, "INVALID_CLIENT", "The client identity is missing.");
            client = clientId;
        }
        var email = user.FindFirstValue("email");
        if (email is null)
        {
            var emails = user.FindFirstValue("emails");
            if (emails is not null && emails.StartsWith('['))
            {
                try
                {
                    using var values = JsonDocument.Parse(emails);
                    email = values.RootElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString()).FirstOrDefault();
                }
                catch (JsonException)
                {
                    throw new DomainException(400, "ADDRESS_REQUIRED", "The provider profile has no usable email address.");
                }
            }
            else email = emails;
        }
        email ??= user.FindFirstValue("preferred_username");
        var name = SessionSummary.FromPrincipal(user, "product").DisplayName;
        var scopes = user.FindFirstValue("scp")?.Split(' ') ?? [];
        return new(tenant, employee, name.Length <= 512 ? name : name[..512], email, client,
            client is null || scopes.Contains("tasks.read") || scopes.Contains("tasks.write"),
            client is null || scopes.Contains("tasks.write"),
            context.TraceIdentifier);
    }

    private static string? Expected(HttpContext context)
    {
        var match = context.Request.Headers.IfMatch.ToString();
        var create = context.Request.Headers.IfNoneMatch.ToString();
        if (create.Length > 0)
        {
            Rules.Require(match.Length == 0 && create == "*", 400, "INVALID_PRECONDITION", "Use a single conditional-write header.");
            return "*";
        }
        if (match.Length == 0) return null;
        Rules.Require(match.Length >= 3 && match[0] == '"' && match[^1] == '"' &&
            !match.StartsWith("W/", StringComparison.Ordinal) && !match.Contains(','),
            400, "INVALID_PRECONDITION", "Supply a single strong ETag.");
        return match[1..^1];
    }

    private static RouteHandlerBuilder Browser(this RouteHandlerBuilder route) =>
        route.RequireAuthorization("browser-operation").WithMetadata(new BrowserOperation());

    public static void MapProduct(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        foreach (var status in new[] { 400, 401, 403, 404, 409, 412, 428, 429, 503 })
            api.ProducesProblem(status);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var result = await next(context);
            var value = result is IValueHttpResult http ? http.Value : result;
            var version = value switch
            {
                TaskView row => row.Version, BoardView row => row.Version, TeamView row => row.Version,
                CompanyView row => row.Version, MemberView row => row.Version, GroupView row => row.Version,
                ConnectionView row => row.Version, ApprovalView row => row.Version,
                InvitationView row => row.Version, GrantView row => row.Version,
                CreateTaskResult row => row.Version, _ => null
            };
            if (version is not null) context.HttpContext.Response.Headers.ETag = $"\"{version}\"";
            return result;
        });
        api.MapGet("/clients", (KanbanService service) => service.Clients).Browser();
        api.MapGet("/companies", (HttpContext h, KanbanService s, string? cursor, int? limit, CancellationToken ct) =>
            s.Companies(Caller(h), cursor, limit ?? 50, ct)).Browser();
        api.MapPost("/companies", async (HttpContext h, KanbanService s, CreateCompanyRequest request, CancellationToken ct) =>
        {
            var row = await s.CreateCompany(Caller(h), request, h.Request.Headers["Idempotency-Key"].ToString(), ct);
            return TypedResults.Created($"/api/v1/companies/{row.Id}", row);
        }).Browser().RequireRateLimiting("company-creation");
        api.MapPost("/invitations/accept", (HttpContext h, KanbanService s, AcceptInvitationRequest request, CancellationToken ct) =>
            s.AcceptInvitation(Caller(h), request.Token, ct)).Browser();
        var company = api.MapGroup("/companies/{companyId:guid}");
        company.MapGet("/", (HttpContext h, KanbanService s, Guid companyId, CancellationToken ct) =>
            s.Company(Caller(h), companyId, ct)).Browser();
        company.MapGet("/setup", (HttpContext h, KanbanService s, Guid companyId, CancellationToken ct) =>
            s.Setup(Caller(h), companyId, ct)).Browser();
        company.MapGet("/memberships", (HttpContext h, KanbanService s, Guid companyId, string? cursor, int? limit, CancellationToken ct) =>
            s.Members(Caller(h), companyId, cursor, limit ?? 50, ct)).Browser();
        company.MapPatch("/memberships/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid id, MembershipRequest request, CancellationToken ct) =>
            s.ChangeMembership(Caller(h), companyId, id, request.Active, Expected(h), ct)).Browser();
        company.MapGet("/invitations", (HttpContext h, KanbanService s, Guid companyId, string? cursor, int? limit, CancellationToken ct) =>
            s.Invitations(Caller(h), companyId, cursor, limit ?? 50, ct)).Browser();
        company.MapPost("/invitations", (HttpContext h, KanbanService s, Guid companyId, InvitationRequest request, CancellationToken ct) =>
            s.Invite(Caller(h), companyId, request.Email, ct)).Browser();
        company.MapPost("/invitations/{id:guid}/revocations", (HttpContext h, KanbanService s, Guid companyId, Guid id, CancellationToken ct) =>
            s.RevokeInvitation(Caller(h), companyId, id, Expected(h), ct)).Browser();
        company.MapGet("/groups", (HttpContext h, KanbanService s, Guid companyId, string? cursor, int? limit, CancellationToken ct) =>
            s.Groups(Caller(h), companyId, cursor, limit ?? 50, ct)).Browser();
        company.MapPost("/groups", (HttpContext h, KanbanService s, Guid companyId, NameRequest request, CancellationToken ct) =>
            s.CreateGroup(Caller(h), companyId, request.Name, ct)).Browser();
        company.MapPut("/groups/{id:guid}/members", (HttpContext h, KanbanService s, Guid companyId, Guid id, GroupMembersRequest request, CancellationToken ct) =>
            s.ChangeGroupMembers(Caller(h), companyId, id, request.MembershipIds, Expected(h), ct)).Browser();
        company.MapGet("/team-administration", (HttpContext h, KanbanService s, Guid companyId, string? cursor, int? limit, CancellationToken ct) =>
            s.AdminTeams(Caller(h), companyId, cursor, limit ?? 50, ct)).Browser();
        company.MapGet("/teams", (HttpContext h, KanbanService s, Guid companyId, string? cursor, int? limit, CancellationToken ct) =>
            s.Teams(Caller(h), companyId, cursor, limit ?? 50, ct));
        company.MapPost("/teams", (HttpContext h, KanbanService s, Guid companyId, CreateTeamRequest request, CancellationToken ct) =>
            s.CreateTeam(Caller(h), companyId, request, ct)).Browser();
        company.MapGet("/audit-events", (HttpContext h, KanbanService s, Guid companyId, string? cursor, int? limit, CancellationToken ct) =>
            s.AuditEvents(Caller(h), companyId, cursor, limit ?? 50, ct)).Browser();
        company.MapGet("/client-approvals", (HttpContext h, KanbanService s, Guid companyId, CancellationToken ct) =>
            s.Approvals(Caller(h), companyId, ct)).Browser();
        company.MapPut("/client-approvals/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid id, ApprovalRequest request, CancellationToken ct) =>
            s.ChangeApproval(Caller(h), companyId, id, request.Enabled, Expected(h), ct)).Browser();
        company.MapGet("/connections", (HttpContext h, KanbanService s, Guid companyId, CancellationToken ct) =>
            s.Connections(Caller(h), companyId, ct)).Browser();
        company.MapPut("/connections/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid id, ConnectionRequest request, CancellationToken ct) =>
            s.ChangeConnection(Caller(h), companyId, id, request, Expected(h), ct)).Browser();
        company.MapDelete("/connections/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid id, CancellationToken ct) =>
            s.RevokeConnection(Caller(h), companyId, id, Expected(h), ct)).Browser();
        var team = company.MapGroup("/teams/{teamId:guid}");
        team.MapGet("/", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, CancellationToken ct) =>
            s.TeamDetails(Caller(h), companyId, teamId, ct));
        team.MapPatch("/", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, NameRequest request, CancellationToken ct) =>
            s.RenameTeam(Caller(h), companyId, teamId, request.Name, Expected(h), ct)).Browser();
        team.MapGet("/board", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, CancellationToken ct) =>
            s.Board(Caller(h), companyId, teamId, ct));
        team.MapPatch("/board", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, NameRequest request, CancellationToken ct) =>
            s.RenameBoard(Caller(h), companyId, teamId, request.Name, Expected(h), ct)).Browser();
        team.MapGet("/grant-candidates", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string? cursor, int? limit, CancellationToken ct) =>
            s.Candidates(Caller(h), companyId, teamId, cursor, limit ?? 50, ct)).Browser();
        team.MapGet("/assignees", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string? cursor, int? limit, CancellationToken ct) =>
            s.Assignees(Caller(h), companyId, teamId, cursor, limit ?? 50, ct));
        team.MapGet("/grant-groups", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string? cursor, int? limit, CancellationToken ct) =>
            s.GrantGroups(Caller(h), companyId, teamId, cursor, limit ?? 50, ct)).Browser();
        team.MapGet("/grants", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string? cursor, int? limit, CancellationToken ct) =>
            s.Grants(Caller(h), companyId, teamId, cursor, limit ?? 50, ct)).Browser();
        team.MapPut("/grants/{type}/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string type, Guid id, GrantRequest request, CancellationToken ct) =>
            s.ChangeGrant(Caller(h), companyId, teamId, type, id, request.Role, Expected(h), ct)).Browser();
        team.MapDelete("/grants/{type}/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string type, Guid id, CancellationToken ct) =>
            s.ChangeGrant(Caller(h), companyId, teamId, type, id, null, Expected(h), ct)).Browser();
        team.MapGet("/effective-access/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, Guid id, CancellationToken ct) =>
            s.Effective(Caller(h), companyId, teamId, id, ct)).Browser();
        team.MapPost("/manager-recovery", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, RecoveryRequest request, CancellationToken ct) =>
            s.RecoverManager(Caller(h), companyId, teamId, request.MembershipId, Expected(h), ct)).Browser();
        team.MapGet("/tasks", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, string? status,
            Guid? assignee, bool? unassigned, string? cursor, int? limit, CancellationToken ct) =>
            s.Tasks(Caller(h), companyId, teamId, new(status, assignee, unassigned ?? false), cursor, limit ?? 50, ct));
        team.MapGet("/tasks/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, Guid id, CancellationToken ct) =>
            s.GetTask(Caller(h), companyId, teamId, id, ct));
        team.MapPost("/tasks", async (HttpContext h, KanbanService s, Guid companyId, Guid teamId, CreateTaskRequest request, CancellationToken ct) =>
        {
            var result = await s.CreateTask(Caller(h), companyId, teamId, request, h.Request.Headers["Idempotency-Key"].ToString(), ct);
            return TypedResults.Created($"/api/v1/companies/{companyId}/teams/{teamId}/tasks/{result.TaskId}", result);
        });
        team.MapPatch("/tasks/{id:guid}", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, Guid id, UpdateTaskRequest request, CancellationToken ct) =>
            s.UpdateTask(Caller(h), companyId, teamId, id, request, Expected(h), ct));
        team.MapPost("/tasks/{id:guid}/moves", (HttpContext h, KanbanService s, Guid companyId, Guid teamId, Guid id, MoveTaskRequest request, CancellationToken ct) =>
            s.MoveTask(Caller(h), companyId, teamId, id, request.TargetColumn, Expected(h), ct));
    }
}
