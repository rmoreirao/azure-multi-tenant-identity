namespace Kanban.Core;

public abstract class CompanyRow
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public byte[] Version { get; set; } = [];
}

public sealed class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DirectoryId { get; set; }
    public Guid ObjectId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed class Company : CompanyRow
{
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class Membership : CompanyRow
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool Administrator { get; set; }
    public int Epoch { get; set; } = 1;
}

public sealed class Team : CompanyRow
{
    public string Name { get; set; } = "";
    public int Revision { get; set; }
}

public sealed class Board : CompanyRow
{
    public Guid TeamId { get; set; }
    public string Name { get; set; } = "Team board";
}

public sealed class WorkItem : CompanyRow
{
    public Guid TeamId { get; set; }
    public Guid BoardId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "todo";
    public Guid? AssigneeMembershipId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanyGroup : CompanyRow
{
    public string Name { get; set; } = "";
    public int Revision { get; set; }
}

public sealed class GroupMember : CompanyRow
{
    public Guid GroupId { get; set; }
    public Guid MembershipId { get; set; }
    public int MembershipEpoch { get; set; }
}

public sealed class MemberGrant : CompanyRow
{
    public Guid TeamId { get; set; }
    public Guid MembershipId { get; set; }
    public int MembershipEpoch { get; set; }
    public int Role { get; set; }
}

public sealed class GroupGrant : CompanyRow
{
    public Guid TeamId { get; set; }
    public Guid GroupId { get; set; }
    public int Role { get; set; }
}

public sealed class Invitation : CompanyRow
{
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public bool Consumed { get; set; }
    public Guid InvitedBy { get; set; }
}

public sealed class ClientRegistration
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ClientApproval : CompanyRow
{
    public Guid ClientId { get; set; }
    public bool Enabled { get; set; }
}

public sealed class ConnectionGrant : CompanyRow
{
    public int Revision { get; set; }
    public Guid MembershipId { get; set; }
    public int MembershipEpoch { get; set; }
    public Guid ClientId { get; set; }
    public bool Read { get; set; }
    public bool Write { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class ConnectionTeam : CompanyRow
{
    public Guid ConnectionId { get; set; }
    public Guid TeamId { get; set; }
}

public sealed class AuditEvent : CompanyRow
{
    public Guid? ActorId { get; set; }
    public Guid? OperatorObjectId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid ObjectId { get; set; }
    public string Action { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MutationReceipt : CompanyRow
{
    public Guid MembershipId { get; set; }
    public string Channel { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid ResultId { get; set; }
    public string ResultVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CompanyCreationReceipt
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid CompanyId { get; set; }
}
