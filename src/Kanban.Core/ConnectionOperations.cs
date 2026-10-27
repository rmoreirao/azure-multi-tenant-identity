namespace Kanban.Core;

public sealed partial class KanbanService
{
    public Task<IReadOnlyList<ApprovalView>> Approvals(Caller caller, Guid companyId, CancellationToken ct) =>
        store.InCompany<IReadOnlyList<ApprovalView>>(caller, companyId, false, async (data, member) =>
        {
            Admin(caller, member);
            var rows = await data.List(data.Query<ClientApproval>(), ct);
            return options.Clients.Select(client =>
            {
                var row = rows.SingleOrDefault(a => a.ClientId == client.Id);
                return new ApprovalView(client.Id, client.Name, row?.Enabled ?? false, row is null ? null : Rules.Version(row));
            }).ToArray();
        }, ct);

    public Task<ApprovalView> ChangeApproval(Caller caller, Guid companyId, Guid clientId, bool enabled,
        string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Admin(caller, member);
            var client = options.Clients.SingleOrDefault(c => c.Id == clientId)
                ?? throw new DomainException(400, "INVALID_CLIENT", "Select a configured client registration.");
            var row = await data.First(data.Query<ClientApproval>().Where(a => a.ClientId == clientId), ct);
            Conditional(row, version);
            if (row is null)
            {
                row = new ClientApproval { CompanyId = companyId, ClientId = clientId };
                data.Add(row);
            }
            row.Enabled = enabled;
            Audit(data, caller, member, enabled ? "client.approved" : "client.disabled", clientId);
            await data.Save(ct);
            return new ApprovalView(clientId, client.Name, enabled, Rules.Version(row));
        }, ct);

    public Task<IReadOnlyList<ConnectionView>> Connections(Caller caller, Guid companyId, CancellationToken ct) =>
        store.InCompany<IReadOnlyList<ConnectionView>>(caller, companyId, false, async (data, member) =>
        {
            Rules.Browser(caller);
            var rows = await data.List(data.Query<ConnectionGrant>().Where(g => g.MembershipId == member.Id), ct);
            var result = new List<ConnectionView>();
            foreach (var row in rows)
            {
                var teams = await data.List(data.Query<ConnectionTeam>().Where(t => t.ConnectionId == row.Id).Select(t => t.TeamId), ct);
                result.Add(new(row.ClientId, row.Read, row.Write, row.Active && row.MembershipEpoch == member.Epoch,
                    teams, Rules.Version(row)));
            }
            return result;
        }, ct);

    public IReadOnlyList<ClientView> Clients => options.Clients.Select(c => new ClientView(c.Id, c.Name)).ToArray();

    public Task<ConnectionView> ChangeConnection(Caller caller, Guid companyId, Guid clientId,
        ConnectionRequest request, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Rules.Browser(caller);
            Rules.Require(options.Clients.Any(c => c.Id == clientId), 400, "INVALID_CLIENT", "Select a configured client.");
            var approval = await data.First(data.Query<ClientApproval>().Where(a => a.ClientId == clientId && a.Enabled), ct);
            Rules.Require(approval is not null, 403, "CLIENT_NOT_APPROVED", "The company has not approved this client.");
            Rules.Require(request.Read && request.TeamIds is not null && request.TeamIds.Length is >= 1 and <= 100 &&
                request.TeamIds.Distinct().Count() == request.TeamIds.Length, 400, "INVALID_CONNECTION",
                "Select read access and between 1 and 100 distinct permitted teams.");
            foreach (var team in request.TeamIds)
                await TeamAccess(data, caller, member, team, request.Write ? 2 : 1, ct);
            var row = await data.First(data.Query<ConnectionGrant>().Where(g => g.ClientId == clientId && g.MembershipId == member.Id), ct);
            Conditional(row, version);
            if (row is null)
            {
                row = new ConnectionGrant { CompanyId = companyId, MembershipId = member.Id, ClientId = clientId };
                data.Add(row);
            }
            row.MembershipEpoch = member.Epoch;
            row.Active = true;
            row.Read = request.Read;
            row.Write = request.Write;
            row.Revision++;
            var old = await data.List(data.Query<ConnectionTeam>().Where(t => t.ConnectionId == row.Id), ct);
            foreach (var item in old) data.Remove(item);
            await data.Save(ct);
            foreach (var id in request.TeamIds)
                data.Add(new ConnectionTeam { CompanyId = companyId, ConnectionId = row.Id, TeamId = id });
            Audit(data, caller, member, "connection.authorized", row.Id);
            await data.Save(ct);
            return new ConnectionView(clientId, row.Read, row.Write, true, request.TeamIds, Rules.Version(row));
        }, ct);


    public Task<ConnectionView> RevokeConnection(Caller caller, Guid companyId, Guid clientId, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            Rules.Browser(caller);
            var row = await data.First(data.Query<ConnectionGrant>().Where(g => g.MembershipId == member.Id && g.ClientId == clientId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The connection is not available.");
            Rules.Match(row, version);
            row.Active = false;
            Audit(data, caller, member, "connection.revoked", row.Id);
            await data.Save(ct);
            return new ConnectionView(clientId, row.Read, row.Write, false, [], Rules.Version(row));
        }, ct);
}
