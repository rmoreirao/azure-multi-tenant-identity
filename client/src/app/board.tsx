import { Button } from "@fluentui/react-components";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { type DragEvent, useState } from "react";
import { Link, useParams } from "react-router";
import { ApiError, api, type Model } from "./api";
import {
  Action,
  columns,
  Empty,
  Feedback,
  field,
  MutationForm,
  PageState,
  roles,
  usePages,
} from "./shared";

export function Teams({ company }: { company: Model<"CompanyView"> }) {
  const teams = usePages(["teams"], (c) => api.teams(company.id, c));
  return (
    <section>
      <div className="page-heading">
        <div>
          <h1>Team boards</h1>
          <p>Only teams with a current direct or group grant are listed.</p>
        </div>
        {company.administrator && (
          <Link className="button-link" to={`/companies/${company.id}/setup`}>
            Create a team
          </Link>
        )}
      </div>
      <ul className="resource-list">
        {teams.items.map((t) => (
          <li key={t.id}>
            <div>
              <Link to={`/companies/${company.id}/teams/${t.id}`}>{t.name}</Link>
              <p className="muted">{roles[Number(t.role)]}</p>
            </div>
          </li>
        ))}
      </ul>
      {!teams.isPending && !teams.error && !teams.items.length && (
        <Empty>
          No team access yet.{" "}
          {company.administrator
            ? "Create a team with an explicit initial manager, or ask a manager for a grant. Being a company administrator does not grant board access."
            : "Ask a team manager to grant you access directly or through a group."}
        </Empty>
      )}
      <PageState query={teams} />
    </section>
  );
}
function TaskCreation({
  company,
  teamId,
  assignees,
}: {
  company: Model<"CompanyView">;
  teamId: string;
  assignees: Model<"MemberView">[];
}) {
  const storage = `kanban.task-attempt.${company.id}.${company.membershipId}.${teamId}`;
  const [formRevision, setFormRevision] = useState(0);
  const [attempt, setAttempt] = useState<{
    key: string;
    body: Model<"CreateTaskRequest">;
    startedAt: number | null;
  } | null>(() => {
    const saved = sessionStorage.getItem(storage);
    if (!saved) return null;
    const value: unknown = JSON.parse(saved);
    if (
      !value ||
      typeof value !== "object" ||
      !("key" in value) ||
      typeof value.key !== "string" ||
      !("body" in value) ||
      !value.body ||
      typeof value.body !== "object" ||
      !("title" in value.body) ||
      typeof value.body.title !== "string" ||
      !("description" in value.body) ||
      typeof value.body.description !== "string" ||
      !("assigneeMembershipId" in value.body) ||
      (value.body.assigneeMembershipId !== null &&
        typeof value.body.assigneeMembershipId !== "string")
    )
      throw new Error("The saved task attempt is invalid. Do not retry with a new key.");
    return {
      key: value.key,
      startedAt:
        "startedAt" in value &&
        typeof value.startedAt === "number" &&
        Number.isFinite(value.startedAt)
          ? value.startedAt
          : null,
      body: {
        title: value.body.title,
        description: value.body.description,
        assigneeMembershipId: value.body.assigneeMembershipId,
      },
    };
  });
  const expired =
    attempt !== null &&
    (attempt.startedAt === null ||
      Date.now() < attempt.startedAt ||
      Date.now() - attempt.startedAt >= 24 * 60 * 60 * 1000);
  return (
    <details className="task-creation" open={attempt !== null}>
      <summary>Create a task</summary>
      <MutationForm
        key={formRevision}
        label={attempt ? "Retry this creation" : "Create task"}
        disabled={expired}
        submit={async (data) => {
          const current = attempt ?? {
            key: crypto.randomUUID(),
            startedAt: Date.now(),
            body: {
              title: field(data, "title"),
              description: field(data, "description"),
              assigneeMembershipId: field(data, "assignee") || null,
            },
          };
          if (
            current.startedAt === null ||
            Date.now() < current.startedAt ||
            Date.now() - current.startedAt >= 24 * 60 * 60 * 1000
          )
            throw new Error(
              "The 24-hour retry window has expired. Check the task list before resolving this attempt.",
            );
          sessionStorage.setItem(storage, JSON.stringify(current));
          setAttempt(current);
          try {
            await api.createTask(company.id, teamId, current.body, current.key);
            sessionStorage.removeItem(storage);
            setAttempt(null);
            setFormRevision((value) => value + 1);
          } catch (error) {
            if (error instanceof Error && "status" in error && error.status === 400) {
              sessionStorage.removeItem(storage);
              setAttempt(null);
            }
            throw error;
          }
        }}
      >
        <label>
          Title
          <input
            name="title"
            required
            maxLength={400}
            readOnly={!!attempt}
            defaultValue={attempt?.body.title}
          />
        </label>
        <label>
          Description
          <textarea
            name="description"
            rows={3}
            maxLength={20000}
            readOnly={!!attempt}
            defaultValue={attempt?.body.description}
          />
        </label>
        <label>
          Assignee
          <select
            name="assignee"
            disabled={!!attempt}
            defaultValue={attempt?.body.assigneeMembershipId ?? ""}
          >
            <option value="">Unassigned</option>
            {assignees.map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
              </option>
            ))}
          </select>
        </label>
        {attempt && (
          <p className="muted">
            An uncertain result is retried with exactly the same content and key. Do not start a new
            attempt to resolve it.
          </p>
        )}
      </MutationForm>
      {attempt && (
        <div className="form-actions">
          {expired && (
            <p role="status">
              This attempt has no current 24-hour retry window. Retrying is blocked to prevent a
              duplicate. Check the task list before clearing it.
            </p>
          )}
          <Button
            onClick={() => {
              if (
                !window.confirm(
                  "Have you checked the task list and resolved this uncertain result? Clearing it discards this saved attempt and allows a new task.",
                )
              )
                return;
              sessionStorage.removeItem(storage);
              setAttempt(null);
              setFormRevision((value) => value + 1);
            }}
          >
            Mark resolved and clear attempt
          </Button>
        </div>
      )}
    </details>
  );
}
export function Board({ company }: { company: Model<"CompanyView"> }) {
  const { teamId = "" } = useParams();
  const queryClient = useQueryClient();
  const teamQuery = useQuery({
    queryKey: ["team", teamId],
    queryFn: () => api.team(company.id, teamId),
    retry: false,
  });
  const team = teamQuery.data;
  const board = useQuery({
    queryKey: ["board", teamId],
    queryFn: () => api.board(company.id, teamId),
    retry: false,
  });
  const [status, setStatus] = useState("");
  const [assignee, setAssignee] = useState("");
  const [cursors, setCursors] = useState<string[]>([]);
  const cursor = cursors.at(-1);
  const tasks = useQuery({
    queryKey: ["tasks", teamId, status, assignee, cursor],
    queryFn: () =>
      api.tasks(company.id, teamId, status || undefined, assignee || undefined, cursor),
    retry: false,
  });
  const assignees = usePages(["assignees", teamId], (c) => api.assignees(company.id, teamId, c));
  const canWrite = Number(team?.role ?? 0) >= 2;
  const move = useMutation({
    mutationFn: ({ row, target }: { row: Model<"TaskView">; target: string }) =>
      api.move(row, target),
    onSuccess: () => queryClient.invalidateQueries(),
  });
  function drop(event: DragEvent, target: string) {
    event.preventDefault();
    if (!canWrite || move.isPending) return;
    const row = tasks.data?.items.find((t) => t.id === event.dataTransfer.getData("text/plain"));
    if (row && row.status !== target) move.mutate({ row, target });
  }
  const denied = [teamQuery.error, board.error, tasks.error].find(
    (error) => error instanceof ApiError && [401, 403, 404].includes(error.status),
  );
  if (denied)
    return (
      <section>
        <Link to={`/companies/${company.id}`}>Team boards</Link>
        <Feedback error={denied} />
      </section>
    );
  return (
    <section>
      <div className="page-heading">
        <div>
          <Link to={`/companies/${company.id}`}>Team boards</Link>
          <h1>{team?.name ?? "Team board"}</h1>
          <p>
            {board.data?.name}{" "}
            {team && <span className="role-label">{roles[Number(team.role)]}</span>}
          </p>
        </div>
        <div className="actions">
          <Button onClick={() => void queryClient.invalidateQueries()}>Refresh</Button>
          {Number(team?.role) === 3 && (
            <Link to={`/companies/${company.id}/teams/${teamId}/access`}>Manage access</Link>
          )}
        </div>
      </div>
      <PageState query={board} />
      <PageState query={teamQuery} />
      {canWrite && <TaskCreation company={company} teamId={teamId} assignees={assignees.items} />}
      <div className="board-filters">
        <label>
          Status
          <select
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setCursors([]);
            }}
          >
            <option value="">All statuses</option>
            {columns.map(([id, name]) => (
              <option key={id} value={id}>
                {name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Assignee
          <select
            value={assignee}
            onChange={(e) => {
              setAssignee(e.target.value);
              setCursors([]);
            }}
          >
            <option value="">Anyone</option>
            <option value="unassigned">Unassigned</option>
            {assignees.items.map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
              </option>
            ))}
          </select>
        </label>
        <span className="muted">{tasks.data?.items.length ?? 0} tasks on this page</span>
      </div>
      <Feedback error={move.error} />
      <PageState query={tasks} />
      <div className="board-columns">
        {columns.map(([id, name]) => (
          <section
            key={id}
            className="board-column"
            aria-label={name}
            onDragOver={(event) => {
              if (canWrite) event.preventDefault();
            }}
            onDrop={(event) => drop(event, id)}
          >
            <h2>
              {name} <span>{tasks.data?.items.filter((t) => t.status === id).length ?? 0}</span>
            </h2>
            {tasks.data?.items
              .filter((t) => t.status === id)
              .map((row) => (
                <article
                  key={row.id}
                  className="task-card"
                  draggable={canWrite && !move.isPending}
                  onDragStart={(event) => event.dataTransfer.setData("text/plain", row.id)}
                >
                  <Link
                    className="task-title"
                    to={`/companies/${company.id}/teams/${teamId}/tasks/${row.id}`}
                  >
                    {row.title}
                  </Link>
                  <p className={row.assigneeState === "inactive" ? "inactive-assignee" : "muted"}>
                    {row.assigneeName ?? "Unassigned"}
                    {row.assigneeState === "inactive" ? " (inactive)" : ""}
                  </p>
                  {canWrite && (
                    <label className="move-label">
                      Move to
                      <select
                        aria-label={`Move ${row.title} to`}
                        value={row.status}
                        disabled={move.isPending}
                        onChange={(e) => move.mutate({ row, target: e.target.value })}
                      >
                        {columns.map(([target, text]) => (
                          <option key={target} value={target}>
                            {text}
                          </option>
                        ))}
                      </select>
                    </label>
                  )}
                </article>
              ))}
            {!tasks.isPending &&
              !tasks.error &&
              !tasks.data?.items.some((t) => t.status === id) && (
                <Empty>No tasks on this page.</Empty>
              )}
          </section>
        ))}
      </div>
      <div className="pagination">
        <Button
          disabled={!cursors.length || tasks.isFetching}
          onClick={() => setCursors((c) => c.slice(0, -1))}
        >
          Previous page
        </Button>
        <Button
          disabled={!tasks.data?.nextCursor || tasks.isFetching}
          onClick={() => {
            const next = tasks.data?.nextCursor;
            if (next) setCursors((c) => [...c, next]);
          }}
        >
          Next page
        </Button>
      </div>
      <PageState query={assignees} />
      {Number(team?.role) === 3 && team && board.data && (
        <details className="settings">
          <summary>Team & board settings</summary>
          <MutationForm
            label="Rename team"
            version={team.version}
            submit={(data) => api.renameTeam(company.id, team, field(data, "name"))}
          >
            <label>
              Team name
              <input name="name" required defaultValue={team.name} maxLength={200} />
            </label>
          </MutationForm>
          <MutationForm
            label="Rename board"
            version={board.data.version}
            submit={(data) => {
              const current = board.data;
              if (!current) throw new Error("Load the current board before renaming it.");
              return api.renameBoard(company.id, teamId, current, field(data, "name"));
            }}
          >
            <label>
              Board name
              <input name="name" required defaultValue={board.data.name} maxLength={200} />
            </label>
          </MutationForm>
        </details>
      )}
    </section>
  );
}
export function TaskDetail({ company }: { company: Model<"CompanyView"> }) {
  const { teamId = "", taskId = "" } = useParams();
  const task = useQuery({
    queryKey: ["task", teamId, taskId],
    queryFn: () => api.task(company.id, teamId, taskId),
    retry: false,
  });
  const team = useQuery({
    queryKey: ["team", teamId],
    queryFn: () => api.team(company.id, teamId),
    retry: false,
  });
  const assignees = usePages(["assignees", teamId], (c) => api.assignees(company.id, teamId, c));
  const canWrite = Number(team.data?.role ?? 0) >= 2;
  const denied = [task.error, team.error].find(
    (error) => error instanceof ApiError && [401, 403, 404].includes(error.status),
  );
  if (denied)
    return (
      <section>
        <Link to={`/companies/${company.id}/teams/${teamId}`}>Back to board</Link>
        <Feedback error={denied} />
      </section>
    );
  return (
    <section>
      <Link to={`/companies/${company.id}/teams/${teamId}`}>Back to board</Link>
      <h1>Task details</h1>
      <PageState query={task} />
      {task.data && (
        <TaskEditor key={taskId} row={task.data} assignees={assignees.items} canWrite={canWrite} />
      )}
      <PageState query={assignees} />
      <PageState query={team} />
    </section>
  );
}
function TaskEditor({
  row,
  assignees,
  canWrite,
}: {
  row: Model<"TaskView">;
  assignees: Model<"MemberView">[];
  canWrite: boolean;
}) {
  const [snapshot, setSnapshot] = useState(row);
  if (!canWrite)
    return (
      <article className="task-read">
        <h2>{row.title}</h2>
        <p className="plain-text">{row.description || "No description."}</p>
        <p>
          {row.assigneeName ?? "Unassigned"}
          {row.assigneeState === "inactive" ? " (inactive)" : ""}
        </p>
        <p>Status: {columns.find(([id]) => id === row.status)?.[1]}</p>
      </article>
    );
  return (
    <div key={snapshot.version}>
      <MutationForm
        label="Save task"
        reset={false}
        submit={async (data) => {
          const assigned = field(data, "assignee") || null;
          const updated = await api.editTask(snapshot, {
            title: field(data, "title"),
            description: field(data, "description"),
            ...(assigned !== snapshot.assigneeMembershipId
              ? { assigneeMembershipId: assigned }
              : {}),
          });
          setSnapshot(updated);
        }}
      >
        <label>
          Title
          <input name="title" defaultValue={snapshot.title} required maxLength={400} />
        </label>
        <label>
          Description
          <textarea
            name="description"
            defaultValue={snapshot.description}
            rows={8}
            maxLength={20000}
          />
        </label>
        <label>
          Assignee
          <select name="assignee" defaultValue={snapshot.assigneeMembershipId ?? ""}>
            <option value="">Unassigned</option>
            {snapshot.assigneeMembershipId &&
              !assignees.some((a) => a.id === snapshot.assigneeMembershipId) && (
                <option value={snapshot.assigneeMembershipId}>
                  {snapshot.assigneeName}{" "}
                  {snapshot.assigneeState === "inactive" ? "(inactive - retained)" : "(current)"}
                </option>
              )}
            {assignees.map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
              </option>
            ))}
          </select>
        </label>
        {snapshot.assigneeState === "inactive" && (
          <p className="inactive-assignee">
            This assignment is inactive. You can keep it while editing, clear it, or select a
            current contributor.
          </p>
        )}
      </MutationForm>
      <div className="actions">
        <Action
          confirm="Discard your draft and load the current task?"
          run={async () => setSnapshot(await api.task(row.companyId, row.teamId, row.id))}
        >
          Load current task (discard draft)
        </Action>
      </div>
      <p className="muted">
        Task content is plain text. Changes require the version you loaded; competing changes are
        never silently overwritten.
      </p>
    </div>
  );
}
