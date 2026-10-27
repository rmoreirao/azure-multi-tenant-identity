namespace Kanban.Core;

public sealed partial class KanbanService
{
    private static void Status(string? status)
    {
        Rules.Require(status is null or "todo" or "inProgress" or "done",
            400, "INVALID_STATUS", "Use todo, inProgress, or done.");
    }

    private async Task<TaskView> TaskResult(IDataSession data, WorkItem task, CancellationToken ct)
    {
        Membership? assignee = null;
        var state = "unassigned";
        if (task.AssigneeMembershipId is Guid id)
        {
            assignee = await data.First(data.Query<Membership>().Where(m => m.Id == id), ct);
            state = assignee is not null && (await Access(data, assignee, task.TeamId, ct)).Role >= 2 ? "active" : "inactive";
        }
        return new(task.Id, task.CompanyId, task.TeamId, task.BoardId, task.Title, task.Description,
            task.Status, task.AssigneeMembershipId, assignee?.Name, state, task.CreatedAt, task.UpdatedAt, Rules.Version(task));
    }

    private static async Task ValidateAssignee(IDataSession data, Guid teamId, Guid? assignee, CancellationToken ct)
    {
        if (assignee is null) return;
        var target = await data.First(data.Query<Membership>().Where(m => m.Id == assignee && m.Active), ct);
        Rules.Require(target is not null && (await Access(data, target, teamId, ct)).Role >= 2,
            400, "INVALID_ASSIGNEE", "Select an active contributor in this team.");
    }

    public Task<Page<TaskView>> Tasks(Caller caller, Guid companyId, Guid teamId, TaskFilter filter,
        string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 1, ct);
            Status(filter.Status);
            Rules.Require(!filter.Unassigned || filter.Assignee is null, 400, "INVALID_FILTER", "Select an assignee or unassigned, not both.");
            var scope = $"tasks:{companyId}:{teamId}:{member.Id}:{caller.ClientId}:{Rules.Hash(filter)}";
            var query = data.Query<WorkItem>().Where(t => t.TeamId == teamId);
            if (filter.Status is not null) query = query.Where(t => t.Status == filter.Status);
            if (filter.Assignee is not null) query = query.Where(t => t.AssigneeMembershipId == filter.Assignee);
            if (filter.Unassigned) query = query.Where(t => t.AssigneeMembershipId == null);
            if (cursor is not null)
            {
                var state = cursors.Decode(cursor);
                Rules.Require(state.Scope == scope && state.CreatedAt is not null && state.Id is not null,
                    400, "INVALID_CURSOR", "The cursor does not match this query.");
                var time = state.CreatedAt!.Value;
                var id = state.Id!.Value;
                query = query.Where(t => t.CreatedAt < time || t.CreatedAt == time && t.Id.CompareTo(id) < 0);
            }
            var rows = await data.List(query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                .Take(Limit(limit) + 1), ct);
            var result = new List<TaskView>();
            foreach (var task in rows.Take(limit)) result.Add(await TaskResult(data, task, ct));
            var last = rows.Take(limit).LastOrDefault();
            return new Page<TaskView>(result, rows.Count > limit && last is not null
                ? cursors.Encode(new(scope, CreatedAt: last.CreatedAt, Id: last.Id)) : null);
        }, ct);

    public Task<TaskView> GetTask(Caller caller, Guid companyId, Guid teamId, Guid taskId, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 1, ct);
            var row = await data.First(data.Query<WorkItem>().Where(t => t.TeamId == teamId && t.Id == taskId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The task is not available.");
            return await TaskResult(data, row, ct);
        }, ct);

    public Task<CreateTaskResult> CreateTask(Caller caller, Guid companyId, Guid teamId, CreateTaskRequest request,
        string key, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 2, ct);
            Rules.Key(key);
            Rules.Require(request.Description is not null, 400, "INVALID_TEXT", "Description must not be null.");
            var title = Rules.Text(request.Title, 1, 200);
            var description = Rules.Text(request.Description, 0, 10000);
            var hash = Rules.Hash(new CreateTaskRequest(title, description, request.AssigneeMembershipId));
            var channel = caller.ClientId?.ToString() ?? "browser";
            var operation = $"task.create:{teamId}";
            var receipt = await data.First(data.Query<MutationReceipt>().Where(r => r.MembershipId == member.Id &&
                r.Channel == channel && r.Operation == operation && r.Key == key), ct);
            if (receipt is not null && receipt.CreatedAt > Now.AddHours(-24))
            {
                Rules.Require(receipt.RequestHash == hash, 409, "IDEMPOTENCY_CONFLICT", "The key was already used for different content.");
                return new CreateTaskResult(receipt.ResultId, receipt.ResultVersion, receipt.CreatedAt);
            }
            if (receipt is not null) data.Remove(receipt);
            await ValidateAssignee(data, teamId, request.AssigneeMembershipId, ct);
            var board = await data.First(data.Query<Board>().Where(b => b.TeamId == teamId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The board is not available.");
            var row = new WorkItem
            {
                CompanyId = companyId, TeamId = teamId, BoardId = board.Id, Title = title, Description = description,
                AssigneeMembershipId = request.AssigneeMembershipId, CreatedBy = member.Id, CreatedAt = Now, UpdatedAt = Now
            };
            data.Add(row);
            Audit(data, caller, member, "task.created", row.Id, teamId);
            await data.Save(ct);
            data.Add(new MutationReceipt
            {
                CompanyId = companyId, MembershipId = member.Id, Channel = channel, Operation = operation,
                Key = key, RequestHash = hash, ResultId = row.Id, ResultVersion = Rules.Version(row), CreatedAt = row.CreatedAt
            });
            await data.Save(ct);
            return new CreateTaskResult(row.Id, Rules.Version(row), row.CreatedAt);
        }, ct);

    public Task<TaskView> UpdateTask(Caller caller, Guid companyId, Guid teamId, Guid taskId,
        UpdateTaskRequest request, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 2, ct);
            var row = await data.First(data.Query<WorkItem>().Where(t => t.TeamId == teamId && t.Id == taskId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The task is not available.");
            Rules.Match(row, version);
            Rules.Require(request.TitleSpecified || request.DescriptionSpecified || request.AssigneeSpecified,
                400, "EMPTY_UPDATE", "Supply at least one editable field.");
            if (request.TitleSpecified)
            {
                Rules.Require(request.Title is not null, 400, "INVALID_TEXT", "Title must not be null.");
                row.Title = Rules.Text(request.Title, 1, 200);
            }
            if (request.DescriptionSpecified)
            {
                Rules.Require(request.Description is not null, 400, "INVALID_TEXT", "Description must not be null.");
                row.Description = Rules.Text(request.Description, 0, 10000);
            }
            if (request.AssigneeSpecified && row.AssigneeMembershipId != request.AssigneeMembershipId)
            {
                await ValidateAssignee(data, teamId, request.AssigneeMembershipId, ct);
                row.AssigneeMembershipId = request.AssigneeMembershipId;
            }
            row.UpdatedAt = Now;
            Audit(data, caller, member, "task.updated", taskId, teamId);
            await data.Save(ct);
            return await TaskResult(data, row, ct);
        }, ct);

    public Task<TaskView> MoveTask(Caller caller, Guid companyId, Guid teamId, Guid taskId,
        string target, string? version, CancellationToken ct) =>
        store.InCompany(caller, companyId, true, async (data, member) =>
        {
            await TeamAccess(data, caller, member, teamId, 2, ct);
            Rules.Require(target is not null, 400, "INVALID_STATUS", "Select a target column.");
            Status(target);
            var row = await data.First(data.Query<WorkItem>().Where(t => t.TeamId == teamId && t.Id == taskId), ct)
                ?? throw new DomainException(404, "NOT_FOUND", "The task is not available.");
            Rules.Match(row, version);
            row.Status = target;
            row.UpdatedAt = Now;
            Audit(data, caller, member, "task.moved", taskId, teamId);
            await data.Save(ct);
            return await TaskResult(data, row, ct);
        }, ct);

    public Task<Page<AuditView>> AuditEvents(Caller caller, Guid companyId, string? cursor, int limit, CancellationToken ct) =>
        store.InCompany(caller, companyId, false, (data, member) =>
        {
            Rules.Browser(caller);
            var managed = Direct(data, member).Where(g => g.Role == 3).Select(g => g.TeamId)
                .Union(GroupGrants(data, member).Where(g => g.Role == 3).Select(g => g.TeamId));
            var query = data.Query<AuditEvent>().Where(a => a.TeamId == null && member.Administrator ||
                a.TeamId != null && managed.Contains(a.TeamId.Value));
            return PageRows(data, query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id),
                $"audit:{companyId}:{member.Id}", cursor, limit,
                a => new AuditView(a.Id, a.ActorId, a.ClientId, a.TeamId, a.ObjectId, a.Action, a.CreatedAt, a.CorrelationId, a.OperatorObjectId), ct);
        }, ct);
}
