using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Kanban.Core;

public sealed record Caller(Guid DirectoryId, Guid ObjectId, string Name, string? Email,
    Guid? ClientId, bool CanRead, bool CanWrite, string CorrelationId)
{
    public bool Browser => ClientId is null;
}

public sealed class DomainException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public static class Rules
{
    public static void Require([DoesNotReturnIf(false)] bool condition, int status, string code, string message)
    {
        if (!condition) throw new DomainException(status, code, message);
    }

    public static string Text(string? value, int minimum, int maximum)
    {
        var text = value?.Trim() ?? "";
        var length = text.EnumerateRunes().Count();
        Require(length >= minimum && length <= maximum, 400, "INVALID_TEXT",
            $"Text must contain between {minimum} and {maximum} Unicode characters.");
        return text;
    }

    public static string Address(string? value)
    {
        var text = Text(value, 3, 320);
        Require(text.Length <= 320 && System.Net.Mail.MailAddress.TryCreate(text, out var address) &&
            string.Equals(address.Address, text, StringComparison.OrdinalIgnoreCase),
            400, "ADDRESS_REQUIRED", "A usable provider-bound email address is required.");
        return text.ToUpperInvariant();
    }

    public static string Key(string value)
    {
        Require(value.Length is >= 16 and <= 128 && value.All(c => c is >= '!' and <= '~'),
            400, "INVALID_KEY", "A 16-128 character ASCII idempotency key is required.");
        return value;
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Hash<T>(T value) => Hash(JsonSerializer.Serialize(value));

    public static string Version(CompanyRow row) => Convert.ToBase64String(row.Version);

    public static void Match(CompanyRow row, string? expected)
    {
        Require(expected is not null, 428, "PRECONDITION_REQUIRED", "Supply the current version.");
        Require(expected == Version(row), 412, "VERSION_CONFLICT", "The record changed. Load its current version.");
    }

    public static void Browser(Caller caller) =>
        Require(caller.Browser, 403, "BROWSER_REQUIRED", "This operation requires a browser session.");
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record CompanyView(Guid Id, string Name, Guid MembershipId, bool Administrator, string Version);
public sealed record MemberView(Guid Id, string Name, string Email, bool Active, bool Administrator, string Version);
public sealed record TeamView(Guid Id, string Name, int Role, string Version);
public sealed record BoardView(Guid Id, Guid TeamId, string Name, string Version);
public sealed record GroupView(Guid Id, string Name, IReadOnlyList<Guid> MemberIds, string Version);
public sealed record GrantView(Guid Id, string PrincipalType, Guid PrincipalId, int Role, string Version);
public sealed record EffectiveAccess(int Role, IReadOnlyList<GrantView> Sources);
public sealed record TaskView(Guid Id, Guid CompanyId, Guid TeamId, Guid BoardId, string Title,
    string Description, string Status, Guid? AssigneeMembershipId, string? AssigneeName,
    string AssigneeState, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Version);
public sealed record CreateTaskResult(Guid TaskId, string Version, DateTimeOffset CreatedAt);
public sealed record InvitationView(Guid Id, string Email, DateTimeOffset ExpiresAt, bool Revoked,
    bool Consumed, string Version, string? Token = null);
public sealed record ApprovalView(Guid ClientId, string Name, bool Enabled, string? Version);
public sealed record ClientView(Guid Id, string Name);
public sealed record ConnectionView(Guid ClientId, bool Read, bool Write, bool Active,
    IReadOnlyList<Guid> TeamIds, string Version);
public sealed record AuditView(Guid Id, Guid? ActorId, Guid? ClientId, Guid? TeamId, Guid ObjectId,
    string Action, DateTimeOffset CreatedAt, string CorrelationId, Guid? OperatorObjectId);
public sealed record SetupView(CompanyView Company, bool HasTeams, bool HasInvitations, bool HasGroups);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCompanyRequest(string Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTeamRequest(string Name, Guid InitialManagerMembershipId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NameRequest(string Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MembershipRequest(bool Active);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationRequest(string Email);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptInvitationRequest(string Token);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupMembersRequest(Guid[] MembershipIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GrantRequest(int Role);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoveryRequest(Guid MembershipId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApprovalRequest(bool Enabled);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConnectionRequest(Guid[] TeamIds, bool Read, bool Write);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskRequest(string Title, string Description = "", Guid? AssigneeMembershipId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MoveTaskRequest(string TargetColumn);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateTaskRequest
{
    private string? title;
    private string? description;
    private Guid? assignee;
    public string? Title { get => title; set { title = value; TitleSpecified = true; } }
    public string? Description { get => description; set { description = value; DescriptionSpecified = true; } }
    public Guid? AssigneeMembershipId { get => assignee; set { assignee = value; AssigneeSpecified = true; } }
    [JsonIgnore] public bool TitleSpecified { get; private set; }
    [JsonIgnore] public bool DescriptionSpecified { get; private set; }
    [JsonIgnore] public bool AssigneeSpecified { get; private set; }
}

public sealed record TaskFilter(string? Status = null, Guid? Assignee = null, bool Unassigned = false);
public sealed record CursorState(string Scope, int Offset = 0, DateTimeOffset? CreatedAt = null, Guid? Id = null);

public interface ICursorCodec
{
    string Encode(CursorState cursor);
    CursorState Decode(string text);
}

public interface IDataSession
{
    IQueryable<T> Query<T>() where T : class;
    Task<List<T>> List<T>(IQueryable<T> query, CancellationToken cancellationToken);
    Task<T?> First<T>(IQueryable<T> query, CancellationToken cancellationToken);
    void Add<T>(T row) where T : class;
    void Remove<T>(T row) where T : class;
    Task Save(CancellationToken cancellationToken);
}

public interface ICompanyStore
{
    Task<T> InCompany<T>(Caller caller, Guid companyId, bool write,
        Func<IDataSession, Membership, Task<T>> action, CancellationToken cancellationToken);
    Task<Page<CompanyView>> Companies(Caller caller, int offset, int limit, CancellationToken cancellationToken);
    Task<Guid> CreateCompany(Caller caller, string name, string key, string hash, bool enabled, CancellationToken cancellationToken);
    Task<Guid> InvitationCompany(string tokenHash, CancellationToken cancellationToken);
    Task<T> AcceptInvitation<T>(Caller caller, Guid companyId,
        Func<IDataSession, Employee, Task<T>> action, CancellationToken cancellationToken);
}

public sealed record ProductOptions(bool SelfServiceEnabled, IReadOnlyList<ClientRegistration> Clients);
