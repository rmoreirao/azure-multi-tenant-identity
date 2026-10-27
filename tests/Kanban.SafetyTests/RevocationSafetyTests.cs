using Kanban.Core;

namespace Kanban.SafetyTests;

public sealed class RevocationSafetyTests
{
    [SqlFact]
    public async Task UninitializedDelegatedProfileHasAnExplicitError()
    {
        await using var f = await SqlScenario.Create();
        var caller = f.Delegated(f.Contributor) with { ObjectId = Guid.NewGuid() };
        var error = await SqlScenario.Denied(403, () => f.Store.Companies(caller, 0, 50, default));
        Assert.Equal("PROFILE_REQUIRED", error.Code);
        Assert.NotEmpty((await f.Store.Companies(f.Delegated(f.Contributor), 0, 50, default)).Items);
    }

    [SqlFact]
    public async Task ConnectionsAndMembershipEpochsReauthorizeEveryOperation()
    {
        await using var f = await SqlScenario.Create();
        var client = f.Delegated(f.Contributor);
        await SqlScenario.Denied(403, () => f.Service.Teams(client, f.Company.Id, null, 100, default));
        var approval = await f.Service.ChangeApproval(f.Owner, f.Company.Id, f.ClientId, true, "*", default);
        var connection = await f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId, new([f.Team.Id], true, true), "*", default);
        Assert.Single((await f.Service.Teams(client, f.Company.Id, null, 100, default)).Items);
        await f.Service.ChangeApproval(f.Owner, f.Company.Id, f.ClientId, false, approval.Version, default);
        await SqlScenario.Denied(403, () => f.Service.Board(client, f.Company.Id, f.Team.Id, default));
        approval = (await f.Service.Approvals(f.Owner, f.Company.Id, default)).Single();
        await f.Service.ChangeApproval(f.Owner, f.Company.Id, f.ClientId, true, approval.Version, default);
        await f.Service.RevokeConnection(f.Contributor, f.Company.Id, f.ClientId, connection.Version, default);
        await SqlScenario.Denied(403, () => f.Service.Board(client, f.Company.Id, f.Team.Id, default));
        connection = (await f.Service.Connections(f.Contributor, f.Company.Id, default)).Single();
        await f.Service.ChangeConnection(f.Contributor, f.Company.Id, f.ClientId, new([f.Team.Id], true, true), connection.Version, default);
        await f.Service.ChangeMembership(f.Owner, f.Company.Id, f.Member.Id, false, f.Member.Version, default);
        await SqlScenario.Denied(404, () => f.Service.Board(client, f.Company.Id, f.Team.Id, default));
        var membership = (await f.Service.Members(f.Owner, f.Company.Id, null, 100, default)).Items.Single(m => m.Id == f.Member.Id);
        await f.Service.ChangeMembership(f.Owner, f.Company.Id, f.Member.Id, true, membership.Version, default);
        Assert.Empty((await f.Service.Teams(f.Contributor, f.Company.Id, null, 100, default)).Items);
        Assert.False((await f.Service.Connections(f.Contributor, f.Company.Id, default)).Single().Active);
        await f.Service.ChangeGrant(f.Owner, f.Company.Id, f.Team.Id, "member", f.Member.Id, 2, "*", default);
        await SqlScenario.Denied(403, () => f.Service.Board(client, f.Company.Id, f.Team.Id, default));
    }
}
