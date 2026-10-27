namespace Kanban.Core;

public sealed partial class KanbanService
{
    public Task<TeamView> TeamDetails(Caller caller, Guid companyId, Guid teamId, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            var role = await TeamAccess(data, caller, member, teamId, 1, ct);
            var team = (await data.First(data.Query<Team>().Where(t => t.Id == teamId), ct))!;
            return new TeamView(team.Id, team.Name, role, Rules.Version(team));
        }, ct);

    public Task<Page<TeamView>> Teams(Caller caller, Guid companyId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            var ids = Direct(data, member).Select(g => g.TeamId).Union(GroupGrants(data, member).Select(g => g.TeamId));
            var query = data.Query<Team>().Where(t => ids.Contains(t.Id));
            var connection = await Connection(data, caller, member, false, ct);
            if (connection is not null)
            {
                var selected = data.Query<ConnectionTeam>().Where(t => t.ConnectionId == connection.Id).Select(t => t.TeamId);
                query = query.Where(t => selected.Contains(t.Id));
            }
            var scope = $"teams:{companyId}:{member.Id}:{caller.ClientId}";
            var offset = Offset(scope, cursor);
            var rows = await data.List(query.OrderBy(t => t.Id).Skip(offset).Take(Limit(limit) + 1), ct);
            var result = new List<TeamView>();
            foreach (var team in rows.Take(limit))
                result.Add(new(team.Id, team.Name, (await Access(data, member, team.Id, ct)).Role, Rules.Version(team)));
            return new Page<TeamView>(result, rows.Count > limit ? cursors.Encode(new(scope, offset + limit)) : null);
        }, ct);

    public Task<Page<TeamView>> AdminTeams(Caller caller, Guid companyId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, (data, member) =>
        {
            Admin(caller, member);
            return PageRows(data, data.Query<Team>().OrderBy(t => t.Id), $"admin-teams:{companyId}",
                cursor, limit, t => new TeamView(t.Id, t.Name, 0, Rules.Version(t)), ct);
        }, ct);

    public Task<TeamView> CreateTeam(Caller caller, Guid companyId, CreateTeamRequest request, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var manager = await data.First(data.Query<Membership>()
                .Where(m => m.Id == request.InitialManagerMembershipId && m.Active), ct)
                ?? throw new DomainException(400, "INVALID_MANAGER", "Select an active company member as initial manager.");
            var team = new Team { CompanyId = companyId, Name = Rules.Text(request.Name, 1, 100) };
            data.Add(team);
            data.Add(new Board { CompanyId = companyId, TeamId = team.Id });
            data.Add(new MemberGrant
            {
                CompanyId = companyId, TeamId = team.Id, MembershipId = manager.Id,
                MembershipEpoch = manager.Epoch, Role = 3
            });
            Audit(data, caller, member, "team.created", team.Id);
            Audit(data, caller, member, "team.initial-manager", manager.Id, team.Id);
            await data.Save(ct);
            return new TeamView(team.Id, team.Name, manager.Id == member.Id ? 3 : 0, Rules.Version(team));
        }, ct);

    public Task<TeamView> RenameTeam(Caller caller, Guid companyId, Guid teamId, string name, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Rules.Browser(caller);
            var role = await TeamAccess(data, caller, member, teamId, 3, ct);
            var team = (await data.First(data.Query<Team>().Where(t => t.Id == teamId), ct))!;
            Rules.Match(team, version);
            team.Name = Rules.Text(name, 1, 100);
            Audit(data, caller, member, "team.renamed", teamId, teamId);
            await data.Save(ct);
            return new TeamView(team.Id, team.Name, role, Rules.Version(team));
        }, ct);

    public Task<BoardView> Board(Caller caller, Guid companyId, Guid teamId, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 1, ct);
            var board = await data.First(data.Query<Board>().Where(b => b.TeamId == teamId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The board is not available.");
            return new BoardView(board.Id, teamId, board.Name, Rules.Version(board));
        }, ct);

    public Task<BoardView> RenameBoard(Caller caller, Guid companyId, Guid teamId, string name, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Rules.Browser(caller);
            await TeamAccess(data, caller, member, teamId, 3, ct);
            var board = await data.First(data.Query<Board>().Where(b => b.TeamId == teamId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The board is not available.");
            Rules.Match(board, version);
            board.Name = Rules.Text(name, 1, 100);
            Audit(data, caller, member, "board.renamed", board.Id, teamId);
            await data.Save(ct);
            return new BoardView(board.Id, teamId, board.Name, Rules.Version(board));
        }, ct);

    public Task<Page<MemberView>> Candidates(Caller caller, Guid companyId, Guid teamId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Rules.Browser(caller);
            await TeamAccess(data, caller, member, teamId, 3, ct);
            return await PageRows(data, data.Query<Membership>().Where(m => m.Active).OrderBy(m => m.Id),
                $"candidates:{companyId}:{teamId}", cursor, limit,
                m => new MemberView(m.Id, m.Name, "", true, false, Rules.Version(m)), ct);
        }, ct);

    public Task<Page<GroupView>> Groups(Caller caller, Guid companyId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Admin(caller, member);
            var page = await PageRows(data, data.Query<CompanyGroup>().OrderBy(g => g.Id),
                $"groups:{companyId}", cursor, limit, g => g, ct);
            var result = new List<GroupView>();
            foreach (var companyGroup in page.Items)
            {
                var ids = await data.List((from link in data.Query<GroupMember>()
                    join m in data.Query<Membership>() on link.MembershipId equals m.Id
                    where link.GroupId == companyGroup.Id && m.Active && link.MembershipEpoch == m.Epoch
                    select m.Id).Take(100), ct);
                result.Add(new(companyGroup.Id, companyGroup.Name, ids, Rules.Version(companyGroup)));
            }
            return new Page<GroupView>(result, page.NextCursor);
        }, ct);

    public Task<Page<GroupView>> GrantGroups(Caller caller, Guid companyId, Guid teamId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Rules.Browser(caller);
            await TeamAccess(data, caller, member, teamId, 3, ct);
            return await PageRows(data, data.Query<CompanyGroup>().OrderBy(g => g.Id),
                $"grant-groups:{companyId}:{teamId}", cursor, limit,
                g => new GroupView(g.Id, g.Name, [], Rules.Version(g)), ct);
        }, ct);

    public Task<Page<MemberView>> Assignees(Caller caller, Guid companyId, Guid teamId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 1, ct);
            var direct = from g in data.Query<MemberGrant>()
                join m in data.Query<Membership>() on g.MembershipId equals m.Id
                where g.TeamId == teamId && g.Role >= 2 && m.Active && m.Epoch == g.MembershipEpoch
                select m.Id;
            var groups = from g in data.Query<GroupGrant>()
                join link in data.Query<GroupMember>() on g.GroupId equals link.GroupId
                join m in data.Query<Membership>() on link.MembershipId equals m.Id
                where g.TeamId == teamId && g.Role >= 2 && m.Active && m.Epoch == link.MembershipEpoch
                select m.Id;
            var ids = direct.Union(groups);
            return await PageRows(data, data.Query<Membership>().Where(m => ids.Contains(m.Id)).OrderBy(m => m.Id),
                $"assignees:{companyId}:{teamId}", cursor, limit,
                m => new MemberView(m.Id, m.Name, "", true, false, Rules.Version(m)), ct);
        }, ct);

    public Task<GroupView> CreateGroup(Caller caller, Guid companyId, string name, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var row = new CompanyGroup { CompanyId = companyId, Name = Rules.Text(name, 1, 100) };
            data.Add(row);
            Audit(data, caller, member, "group.created", row.Id);
            await data.Save(ct);
            return new GroupView(row.Id, row.Name, [], Rules.Version(row));
        }, ct);

    public Task<GroupView> ChangeGroupMembers(Caller caller, Guid companyId, Guid groupId, Guid[] memberIds,
        string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            Rules.Require(memberIds is not null && memberIds.Length <= 100 && memberIds.Distinct().Count() == memberIds.Length,
                400, "INVALID_MEMBERS", "Supply up to 100 distinct active memberships.");
            var group = await data.First(data.Query<CompanyGroup>().Where(g => g.Id == groupId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The group is not available.");
            Rules.Match(group, version);
            var selected = await data.List(data.Query<Membership>().Where(m => memberIds.Contains(m.Id) && m.Active), ct);
            Rules.Require(selected.Count == memberIds.Length, 400, "INVALID_MEMBERS", "All members must be active in this company.");
            var old = await data.List(data.Query<GroupMember>().Where(g => g.GroupId == groupId), ct);
            foreach (var link in old) data.Remove(link);
            await data.Save(ct);
            foreach (var target in selected)
                data.Add(new GroupMember
                {
                    CompanyId = companyId, GroupId = groupId, MembershipId = target.Id, MembershipEpoch = target.Epoch
                });
            group.Revision++;
            Audit(data, caller, member, "group.members-replaced", groupId);
            await data.Save(ct);
            return new GroupView(group.Id, group.Name, memberIds, Rules.Version(group));
        }, ct);

    public Task<Page<GrantView>> Grants(Caller caller, Guid companyId, Guid teamId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Rules.Browser(caller);
            await TeamAccess(data, caller, member, teamId, 3, ct);
            var direct = from grant in data.Query<MemberGrant>()
                join m in data.Query<Membership>() on grant.MembershipId equals m.Id
                where grant.TeamId == teamId && m.Active && m.Epoch == grant.MembershipEpoch
                select new { grant.Id, PrincipalType = "member", PrincipalId = grant.MembershipId, grant.Role, grant.Version };
            var groups = data.Query<GroupGrant>().Where(g => g.TeamId == teamId)
                .Select(g => new { g.Id, PrincipalType = "group", PrincipalId = g.GroupId, g.Role, g.Version });
            return await PageRows(data, direct.Concat(groups).OrderBy(g => g.Id),
                $"grants:{companyId}:{teamId}", cursor, limit,
                g => new GrantView(g.Id, g.PrincipalType, g.PrincipalId, g.Role, Convert.ToBase64String(g.Version)), ct);
        }, ct);

    public Task<EffectiveAccess> Effective(Caller caller, Guid companyId, Guid teamId, Guid membershipId, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Rules.Browser(caller);
            await TeamAccess(data, caller, member, teamId, 3, ct);
            var target = await data.First(data.Query<Membership>().Where(m => m.Id == membershipId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The membership is not available.");
            return await Access(data, target, teamId, ct);
        }, ct);

    public Task<GrantView> ChangeGrant(Caller caller, Guid companyId, Guid teamId, string type, Guid principalId,
        int? role, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Rules.Browser(caller);
            await TeamAccess(data, caller, member, teamId, 3, ct);
            Rules.Require(type is "member" or "group" && (role is null or >= 1 and <= 3),
                400, "INVALID_GRANT", "Select a member/group and a role from 1 to 3.");
            CompanyRow? existing;
            Membership? target = null;
            if (type == "member")
            {
                target = await data.First(data.Query<Membership>().Where(m => m.Id == principalId && m.Active), ct);
                Rules.Require(target is not null, 400, "INVALID_PRINCIPAL", "Select an active same-company member.");
                existing = await data.First(data.Query<MemberGrant>().Where(g => g.TeamId == teamId && g.MembershipId == principalId), ct);
            }
            else
            {
                Rules.Require(await data.First(data.Query<CompanyGroup>().Where(g => g.Id == principalId), ct) is not null,
                    400, "INVALID_PRINCIPAL", "Select a same-company group.");
                existing = await data.First(data.Query<GroupGrant>().Where(g => g.TeamId == teamId && g.GroupId == principalId), ct);
            }
            var historical = existing is MemberGrant old && target is not null && old.MembershipEpoch != target.Epoch;
            Rules.Require(!historical || role is not null, 404, "NOT_FOUND", "The active grant is not available.");
            Conditional(historical ? null : existing, version);
            if (role is null)
            {
                Rules.Require(existing is not null, 404, "NOT_FOUND", "The grant is not available.");
                data.Remove(existing!);
            }
            else if (type == "member")
            {
                var grant = existing as MemberGrant ?? new MemberGrant
                {
                    CompanyId = companyId, TeamId = teamId, MembershipId = principalId
                };
                grant.Role = role.Value;
                grant.MembershipEpoch = target!.Epoch;
                if (existing is null) data.Add(grant);
                existing = grant;
            }
            else
            {
                var grant = existing as GroupGrant ?? new GroupGrant
                {
                    CompanyId = companyId, TeamId = teamId, GroupId = principalId
                };
                grant.Role = role.Value;
                if (existing is null) data.Add(grant);
                existing = grant;
            }
            var team = (await data.First(data.Query<Team>().Where(t => t.Id == teamId), ct))!;
            team.Revision++;
            Audit(data, caller, member, role is null ? "grant.removed" : "grant.changed", principalId, teamId);
            await data.Save(ct);
            return new GrantView(existing!.Id, type, principalId, role ?? 0, Rules.Version(existing));
        }, ct);

    private static void Conditional(CompanyRow? row, string? version)
    {
        if (row is null)
            Rules.Require(version == "*", 428, "PRECONDITION_REQUIRED", "Use If-None-Match: * to create a grant.");
        else
        {
            Rules.Require(version != "*", 412, "VERSION_CONFLICT", "The record already exists.");
            Rules.Match(row, version);
        }
    }

    public Task<TeamView> RecoverManager(Caller caller, Guid companyId, Guid teamId, Guid membershipId,
        string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var team = await data.First(data.Query<Team>().Where(t => t.Id == teamId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The team is not available.");
            Rules.Match(team, version);
            var directManagers = from grant in data.Query<MemberGrant>()
                join m in data.Query<Membership>() on grant.MembershipId equals m.Id
                where grant.TeamId == teamId && grant.Role == 3 && m.Active && m.Epoch == grant.MembershipEpoch
                select m.Id;
            var groupManagers = from grant in data.Query<GroupGrant>()
                join link in data.Query<GroupMember>() on grant.GroupId equals link.GroupId
                join m in data.Query<Membership>() on link.MembershipId equals m.Id
                where grant.TeamId == teamId && grant.Role == 3 && m.Active && m.Epoch == link.MembershipEpoch
                select m.Id;
            Rules.Require(await data.First(directManagers.Union(groupManagers), ct) == Guid.Empty,
                409, "MANAGER_EXISTS", "Use the existing team manager to arrange grants.");
            var target = await data.First(data.Query<Membership>().Where(m => m.Id == membershipId && m.Active), ct)
                ?? throw new DomainException(400, "INVALID_MANAGER", "Select an active company member.");
            var row = await data.First(data.Query<MemberGrant>().Where(g => g.TeamId == teamId && g.MembershipId == membershipId), ct);
            if (row is null)
            {
                row = new MemberGrant { CompanyId = companyId, TeamId = teamId, MembershipId = membershipId };
                data.Add(row);
            }
            row.Role = 3;
            row.MembershipEpoch = target.Epoch;
            team.Revision++;
            Audit(data, caller, member, "team.manager-recovered", membershipId, teamId);
            await data.Save(ct);
            return new TeamView(team.Id, team.Name, target.Id == member.Id ? 3 : 0, Rules.Version(team));
        }, ct);
}
