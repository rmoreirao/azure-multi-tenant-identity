namespace Kanban.SafetyTests;

public sealed class RoleSafetyTests
{
    [SqlFact]
    public async Task MaximumDirectGroupRoleDoesNotGiveCompanyAdministratorsImplicitContentAccess()
    {
        await using var f = await SqlScenario.Create();
        var hidden = await f.Service.CreateTeam(f.Owner, f.Company.Id, new("Hidden", f.Member.Id), default);
        await SqlScenario.Denied(404, () => f.Service.Board(f.Owner, f.Company.Id, hidden.Id, default));
        var task = await f.Task();
        await SqlScenario.Denied(403, () => f.Service.MoveTask(f.Viewer, f.Company.Id, f.Team.Id, task.TaskId, "done", task.Version, default));
        var group = await f.Service.CreateGroup(f.Owner, f.Company.Id, "Contributors", default);
        await f.Service.ChangeGroupMembers(f.Owner, f.Company.Id, group.Id, [f.Reader.Id], group.Version, default);
        var grant = await f.Service.ChangeGrant(f.Owner, f.Company.Id, f.Team.Id, "group", group.Id, 2, "*", default);
        var access = await f.Service.Effective(f.Owner, f.Company.Id, f.Team.Id, f.Reader.Id, default);
        Assert.Equal(2, access.Role);
        Assert.Equal(2, access.Sources.Count);
        await f.Service.ChangeGrant(f.Owner, f.Company.Id, f.Team.Id, "group", group.Id, null, grant.Version, default);
        Assert.Equal(1, (await f.Service.Effective(f.Owner, f.Company.Id, f.Team.Id, f.Reader.Id, default)).Role);
        var audit = await f.Service.AuditEvents(f.Owner, f.Company.Id, null, 100, default);
        Assert.DoesNotContain(audit.Items, a => a.TeamId == hidden.Id);
    }

    [SqlFact]
    public async Task AccessLossRetainsInactiveAssignmentsAndAllowsNarrowManagerRecovery()
    {
        await using var f = await SqlScenario.Create();
        var task = await f.Task(f.Member.Id);
        var grants = await f.Service.Grants(f.Owner, f.Company.Id, f.Team.Id, null, 100, default);
        var grant = grants.Items.Single(g => g.PrincipalId == f.Member.Id);
        await f.Service.ChangeGrant(f.Owner, f.Company.Id, f.Team.Id, "member", f.Member.Id, null, grant.Version, default);
        var read = await f.Service.GetTask(f.Owner, f.Company.Id, f.Team.Id, task.TaskId, default);
        Assert.Equal("inactive", read.AssigneeState);
        Assert.Equal(f.Member.Id, read.AssigneeMembershipId);
        var edited = await f.Service.UpdateTask(f.Owner, f.Company.Id, f.Team.Id, task.TaskId, new() { Title = "Retained" }, read.Version, default);
        var moved = await f.Service.MoveTask(f.Owner, f.Company.Id, f.Team.Id, task.TaskId, "done", edited.Version, default);
        Assert.Equal("inactive", moved.AssigneeState);
        var hidden = await f.Service.CreateTeam(f.Owner, f.Company.Id, new("Recoverable", f.Reader.Id), default);
        await f.Service.ChangeMembership(f.Owner, f.Company.Id, f.Reader.Id, false, f.Reader.Version, default);
        var recovered = await f.Service.RecoverManager(f.Owner, f.Company.Id, hidden.Id, f.Member.Id, hidden.Version, default);
        Assert.Equal(0, recovered.Role);
        await SqlScenario.Denied(404, () => f.Service.Board(f.Owner, f.Company.Id, hidden.Id, default));
        Assert.NotNull(await f.Service.Board(f.Contributor, f.Company.Id, hidden.Id, default));
    }
}
