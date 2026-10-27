namespace Kanban.Core;

public sealed partial class KanbanService(
    ICompanyStore store, ICursorCodec cursors, ProductOptions options, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    private static void Admin(Caller caller, Membership member)
    {
        Rules.Browser(caller);
        Rules.Require(member.Administrator, 403, "ADMIN_REQUIRED", "Company administrator access is required.");
    }

    private static IQueryable<MemberGrant> Direct(IDataSession data, Membership member) =>
        data.Query<MemberGrant>().Where(g => g.MembershipId == member.Id && g.MembershipEpoch == member.Epoch);

    private static IQueryable<GroupGrant> GroupGrants(IDataSession data, Membership member) =>
        from grant in data.Query<GroupGrant>()
        join link in data.Query<GroupMember>() on grant.GroupId equals link.GroupId
        where link.MembershipId == member.Id && link.MembershipEpoch == member.Epoch
        select grant;

    private static async Task<EffectiveAccess> Access(IDataSession data, Membership member, Guid team,
        CancellationToken ct)
    {
        if (!member.Active) return new(0, []);
        var direct = await data.List(Direct(data, member).Where(g => g.TeamId == team)
            .Select(g => new GrantView(g.Id, "member", member.Id, g.Role, Convert.ToBase64String(g.Version))), ct);
        var groups = await data.List(GroupGrants(data, member).Where(g => g.TeamId == team)
            .Select(g => new GrantView(g.Id, "group", g.GroupId, g.Role, Convert.ToBase64String(g.Version))), ct);
        var sources = direct.Concat(groups).ToArray();
        return new(sources.Select(g => g.Role).DefaultIfEmpty(0).Max(), sources);
    }

    private static async Task<ConnectionGrant?> Connection(IDataSession data, Caller caller,
        Membership member, bool write, CancellationToken ct)
    {
        if (caller.Browser) return null;
        Rules.Require(caller.CanRead && (!write || caller.CanWrite), 403, "CAPABILITY_REQUIRED",
            "The delegated capability does not permit this operation.");
        var approved = await data.First(data.Query<ClientApproval>()
            .Where(a => a.ClientId == caller.ClientId && a.Enabled), ct);
        var grant = await data.First(data.Query<ConnectionGrant>().Where(g =>
            g.ClientId == caller.ClientId && g.MembershipId == member.Id && g.MembershipEpoch == member.Epoch &&
            g.Active && g.Read && (!write || g.Write)), ct);
        Rules.Require(approved is not null && grant is not null, 403, "CONNECTION_REQUIRED",
            "An active company-approved connection is required.");
        return grant;
    }

    private static async Task<int> TeamAccess(IDataSession data, Caller caller, Membership member,
        Guid teamId, int minimum, CancellationToken ct)
    {
        var team = await data.First(data.Query<Team>().Where(t => t.Id == teamId), ct);
        var access = await Access(data, member, teamId, ct);
        Rules.Require(team is not null && access.Role > 0, 404, "NOT_FOUND", "The team is not available.");
        Rules.Require(access.Role >= minimum, 403, "ROLE_REQUIRED", "Your team role does not permit this operation.");
        var connection = await Connection(data, caller, member, minimum >= 2, ct);
        if (connection is not null)
        {
            var selected = await data.First(data.Query<ConnectionTeam>()
                .Where(t => t.ConnectionId == connection.Id && t.TeamId == teamId), ct);
            Rules.Require(selected is not null, 403, "TEAM_OUT_OF_SCOPE", "This team is outside the connection.");
        }
        return access.Role;
    }

    private void Audit(IDataSession data, Caller caller, Membership member, string action, Guid objectId, Guid? team = null) =>
        data.Add(new AuditEvent
        {
            CompanyId = member.CompanyId, ActorId = member.Id, ClientId = caller.ClientId,
            TeamId = team, ObjectId = objectId, Action = action, CreatedAt = Now,
            CorrelationId = caller.CorrelationId
        });

    private int Offset(string scope, string? cursor)
    {
        if (cursor is null) return 0;
        var state = cursors.Decode(cursor);
        Rules.Require(state.Scope == scope && state.Offset >= 0, 400, "INVALID_CURSOR", "The cursor does not match this query.");
        return state.Offset;
    }

    private static int Limit(int limit)
    {
        Rules.Require(limit is >= 1 and <= 100, 400, "INVALID_LIMIT", "Page size must be between 1 and 100.");
        return limit;
    }

    private async Task<Page<TResult>> PageRows<T, TResult>(IDataSession data, IOrderedQueryable<T> query,
        string scope, string? cursor, int limit, Func<T, TResult> map, CancellationToken ct)
    {
        var offset = Offset(scope, cursor);
        var rows = await data.List(query.Skip(offset).Take(Limit(limit) + 1), ct);
        return new(rows.Take(limit).Select(map).ToArray(), rows.Count > limit
            ? cursors.Encode(new(scope, checked(offset + limit))) : null);
    }

    private static CompanyView CompanyResult(Company row, Membership member) =>
        new(row.Id, row.Name, member.Id, member.Administrator, Rules.Version(row));
    private static MemberView MemberResult(Membership row) =>
        new(row.Id, row.Name, row.Email, row.Active, row.Administrator, Rules.Version(row));

    public async Task<Page<CompanyView>> Companies(Caller caller, string? cursor, int limit, CancellationToken ct)
    {
        Rules.Browser(caller);
        var scope = $"companies:{caller.DirectoryId}:{caller.ObjectId}";
        var offset = Offset(scope, cursor);
        var page = await store.Companies(caller, offset, Limit(limit), ct);
        return page with { NextCursor = page.NextCursor is not null ? cursors.Encode(new(scope, offset + limit)) : null };
    }

    public async Task<CompanyView> CreateCompany(Caller caller, CreateCompanyRequest request, string key, CancellationToken ct)
    {
        Rules.Browser(caller);
        Rules.Address(caller.Email);
        var name = Rules.Text(request.Name, 1, 100);
        var id = await store.CreateCompany(caller, name, Rules.Key(key), Rules.Hash(name), options.SelfServiceEnabled, ct);
        return await Company(caller, id, ct);
    }

    public Task<CompanyView> Company(Caller caller, Guid companyId, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Rules.Browser(caller);
            var row = await data.First(data.Query<Company>().Where(c => c.Id == companyId && c.Active), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The company is not available.");
            return CompanyResult(row, member);
        }, ct);

    public Task<SetupView> Setup(Caller caller, Guid companyId, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            Admin(caller, member);
            var company = await data.First(data.Query<Company>().Where(c => c.Id == companyId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The company is not available.");
            return new SetupView(CompanyResult(company, member),
                await data.First(data.Query<Team>(), ct) is not null,
                await data.First(data.Query<Invitation>(), ct) is not null,
                await data.First(data.Query<CompanyGroup>(), ct) is not null);
        }, ct);

    public Task<Page<MemberView>> Members(Caller caller, Guid companyId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, (data, member) =>
        {
            Admin(caller, member);
            return PageRows(data, data.Query<Membership>().OrderBy(m => m.Id), $"members:{companyId}",
                cursor, limit, MemberResult, ct);
        }, ct);

    public Task<MemberView> ChangeMembership(Caller caller, Guid companyId, Guid membershipId, bool active,
        string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var target = await data.First(data.Query<Membership>().Where(m => m.Id == membershipId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The membership is not available.");
            Rules.Match(target, version);
            if (!active && target.Administrator && target.Active)
            {
                var other = await data.First(data.Query<Membership>()
                    .Where(m => m.Active && m.Administrator && m.Id != target.Id), ct);
                Rules.Require(other is not null, 409, "LAST_ADMINISTRATOR",
                    "Appoint another administrator through the controlled operator path first.");
            }
            if (target.Active != active)
            {
                target.Active = active;
                target.Administrator = false;
                target.Epoch = checked(target.Epoch + 1);
                Audit(data, caller, member, active ? "membership.reactivated" : "membership.deactivated", target.Id);
            }
            await data.Save(ct);
            return MemberResult(target);
        }, ct);

    public Task<Page<InvitationView>> Invitations(Caller caller, Guid companyId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, (data, member) =>
        {
            Admin(caller, member);
            return PageRows(data, data.Query<Invitation>().OrderBy(i => i.Id), $"invitations:{companyId}",
                cursor, limit, i => InvitationResult(i), ct);
        }, ct);

    private static InvitationView InvitationResult(Invitation row, string? token = null) =>
        new(row.Id, row.Email, row.ExpiresAt, row.Revoked, row.Consumed, Rules.Version(row), token);

    public Task<InvitationView> Invite(Caller caller, Guid companyId, string address, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var row = new Invitation
            {
                CompanyId = companyId, Email = Rules.Address(address), TokenHash = Rules.Hash(token),
                ExpiresAt = Now.AddDays(7), InvitedBy = member.Id
            };
            data.Add(row);
            Audit(data, caller, member, "invitation.created", row.Id);
            await data.Save(ct);
            return InvitationResult(row, token);
        }, ct);

    public Task<InvitationView> RevokeInvitation(Caller caller, Guid companyId, Guid id, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var row = await data.First(data.Query<Invitation>().Where(i => i.Id == id), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The invitation is not available.");
            Rules.Match(row, version);
            Rules.Require(!row.Consumed, 409, "INVITATION_USED", "The invitation has already been accepted.");
            row.Revoked = true;
            Audit(data, caller, member, "invitation.revoked", id);
            await data.Save(ct);
            return InvitationResult(row);
        }, ct);

    public async Task<CompanyView> AcceptInvitation(Caller caller, string token, CancellationToken ct)
    {
        Rules.Browser(caller);
        Rules.Require(token is not null && token.Length == 64 && token.All(Uri.IsHexDigit), 400, "INVALID_INVITATION", "The invitation is invalid.");
        var email = Rules.Address(caller.Email);
        var hash = Rules.Hash(token);
        var companyId = await store.InvitationCompany(hash, ct);
        return await store.AcceptInvitation(caller, companyId, async (data, employee) =>
        {
            var row = await data.First(data.Query<Invitation>().Where(i => i.TokenHash == hash), ct);
            Rules.Require(row is not null && !row.Revoked && !row.Consumed && row.ExpiresAt > Now &&
                row.Email == email, 400, "INVALID_INVITATION", "The invitation is expired, unavailable, or belongs to another address.");
            var invitation = row!;
            var member = await data.First(data.Query<Membership>().Where(m => m.UserId == employee.Id), ct);
            if (member is null)
            {
                member = new Membership
                {
                    CompanyId = companyId, UserId = employee.Id, Name = employee.Name, Email = employee.Email
                };
                data.Add(member);
            }
            else if (!member.Active)
            {
                member.Active = true;
                member.Administrator = false;
                member.Epoch = checked(member.Epoch + 1);
            }
            invitation.Consumed = true;
            Audit(data, caller, member, "invitation.accepted", invitation.Id);
            await data.Save(ct);
            var company = await data.First(data.Query<Company>().Where(c => c.Id == companyId && c.Active), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The company is not available.");
            return CompanyResult(company, member);
        }, ct);
    }
}
