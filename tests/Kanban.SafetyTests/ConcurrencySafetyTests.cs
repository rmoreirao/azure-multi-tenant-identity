using Kanban.Core;

namespace Kanban.SafetyTests;

public sealed class ConcurrencySafetyTests
{
    [SqlFact]
    public async Task CompetingEditsRequireTheExactTaskVersion()
    {
        await using var f = await SqlScenario.Create();
        var task = await f.Task();
        await SqlScenario.Denied(428, () => f.Service.MoveTask(f.Contributor, f.Company.Id, f.Team.Id, task.TaskId, "done", null, default));
        var writes = await Task.WhenAll(new[] { "done", "inProgress" }.Select(async target =>
        {
            try { await f.Service.MoveTask(f.Contributor, f.Company.Id, f.Team.Id, task.TaskId, target, task.Version, default); return 200; }
            catch (DomainException ex) { return ex.Status; }
        }));
        Assert.Equal(new[] { 200, 412 }, writes.Order().ToArray());
        await SqlScenario.Denied(400, () => f.Service.MoveTask(f.Contributor, f.Company.Id, f.Team.Id, task.TaskId, null!, task.Version, default));
    }

    [SqlFact]
    public async Task GroupAndConnectionSetChangesAdvanceTheirVersions()
    {
        await using var f = await SqlScenario.Create();
        var group = await f.Service.CreateGroup(f.Owner, f.Company.Id, "Conditional", default);
        var changed = await f.Service.ChangeGroupMembers(f.Owner, f.Company.Id, group.Id, [f.Member.Id], group.Version, default);
        Assert.NotEqual(group.Version, changed.Version);
        await SqlScenario.Denied(412, () => f.Service.ChangeGroupMembers(f.Owner, f.Company.Id, group.Id, [], group.Version, default));
        await f.Service.ChangeApproval(f.Owner, f.Company.Id, f.ClientId, true, "*", default);
        var other = await f.Service.CreateTeam(f.Owner, f.Company.Id, new("Other", f.Member.Id), default);
        var connection = await f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId, new([f.Team.Id], true, true), "*", default);
        var updated = await f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId, new([other.Id], true, true), connection.Version, default);
        Assert.NotEqual(connection.Version, updated.Version);
        await SqlScenario.Denied(412, () => f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId, new([f.Team.Id], true, true), connection.Version, default));
        await SqlScenario.Denied(403, () => f.Service.Board(f.Delegated(f.Contributor), f.Company.Id, f.Team.Id, default));
    }
}
