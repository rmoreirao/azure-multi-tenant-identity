import createClient from "openapi-fetch";
import type { components, paths } from "../generated/api";

export type Model<K extends keyof components["schemas"]> = components["schemas"][K];
export type Page<T> = { items: T[]; nextCursor: string | null };
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly retryAfter: string | null = null,
  ) {
    super(message);
  }
}

const client = createClient<paths>({ credentials: "same-origin" });
async function csrf(): Promise<string> {
  const response = await fetch("/api/antiforgery", {
    credentials: "same-origin",
    cache: "no-store",
  });
  if (!response.ok)
    throw new ApiError(response.status, "Your browser session changed. Sign in again.");
  const value: unknown = await response.json();
  if (!value || typeof value !== "object" || !("token" in value) || typeof value.token !== "string")
    throw new Error("The browser verification response is invalid. Refresh and retry.");
  return value.token;
}
client.use({
  async onRequest({ request }) {
    if (!["GET", "HEAD", "OPTIONS"].includes(request.method))
      request.headers.set("X-CSRF-TOKEN", await csrf());
    return request;
  },
});
async function result<T>(
  operation: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  const { data, error, response } = await operation;
  if (!response.ok) {
    const title =
      error && typeof error === "object" && "title" in error && typeof error.title === "string"
        ? error.title
        : response.status === 401
          ? "Your session ended. Sign in again."
          : response.status === 404
            ? "This resource is unavailable or you no longer have access."
            : "The request could not be completed. Your draft has been preserved.";
    throw new ApiError(response.status, title, response.headers.get("Retry-After"));
  }
  if (data === undefined) throw new Error("The server returned no result.");
  return data;
}
const condition = (version: string | null | undefined) =>
  version ? { "If-Match": `"${version}"` } : { "If-None-Match": "*" };
const paging = (cursor?: string) => ({ limit: 50, cursor });

export const api = {
  async session() {
    const response = await client.GET("/api/session");
    if (response.response.status === 401) return null;
    return result(Promise.resolve(response));
  },
  companies: (cursor?: string) =>
    result(client.GET("/api/v1/companies", { params: { query: paging(cursor) } })),
  createCompany: (name: string, key: string) =>
    result(
      client.POST("/api/v1/companies", { body: { name }, headers: { "Idempotency-Key": key } }),
    ),
  company: (companyId: string) =>
    result(client.GET("/api/v1/companies/{companyId}", { params: { path: { companyId } } })),
  setup: (companyId: string) =>
    result(client.GET("/api/v1/companies/{companyId}/setup", { params: { path: { companyId } } })),
  accept: (token: string) => result(client.POST("/api/v1/invitations/accept", { body: { token } })),
  members: (companyId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/memberships", {
        params: { path: { companyId }, query: paging(cursor) },
      }),
    ),
  membership: (companyId: string, member: Model<"MemberView">) =>
    result(
      client.PATCH("/api/v1/companies/{companyId}/memberships/{id}", {
        params: { path: { companyId, id: member.id } },
        headers: condition(member.version),
        body: { active: !member.active },
      }),
    ),
  invitations: (companyId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/invitations", {
        params: { path: { companyId }, query: paging(cursor) },
      }),
    ),
  invite: (companyId: string, email: string) =>
    result(
      client.POST("/api/v1/companies/{companyId}/invitations", {
        params: { path: { companyId } },
        body: { email },
      }),
    ),
  revokeInvitation: (companyId: string, row: Model<"InvitationView">) =>
    result(
      client.POST("/api/v1/companies/{companyId}/invitations/{id}/revocations", {
        params: { path: { companyId, id: row.id } },
        headers: condition(row.version),
      }),
    ),
  teams: (companyId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams", {
        params: { path: { companyId }, query: paging(cursor) },
      }),
    ),
  team: (companyId: string, teamId: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}", {
        params: { path: { companyId, teamId } },
      }),
    ),
  adminTeams: (companyId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/team-administration", {
        params: { path: { companyId }, query: paging(cursor) },
      }),
    ),
  createTeam: (companyId: string, name: string, initialManagerMembershipId: string) =>
    result(
      client.POST("/api/v1/companies/{companyId}/teams", {
        params: { path: { companyId } },
        body: { name, initialManagerMembershipId },
      }),
    ),
  recover: (companyId: string, team: Model<"TeamView">, membershipId: string) =>
    result(
      client.POST("/api/v1/companies/{companyId}/teams/{teamId}/manager-recovery", {
        params: { path: { companyId, teamId: team.id } },
        headers: condition(team.version),
        body: { membershipId },
      }),
    ),
  renameTeam: (companyId: string, team: Model<"TeamView">, name: string) =>
    result(
      client.PATCH("/api/v1/companies/{companyId}/teams/{teamId}", {
        params: { path: { companyId, teamId: team.id } },
        headers: condition(team.version),
        body: { name },
      }),
    ),
  board: (companyId: string, teamId: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/board", {
        params: { path: { companyId, teamId } },
      }),
    ),
  renameBoard: (companyId: string, teamId: string, board: Model<"BoardView">, name: string) =>
    result(
      client.PATCH("/api/v1/companies/{companyId}/teams/{teamId}/board", {
        params: { path: { companyId, teamId } },
        headers: condition(board.version),
        body: { name },
      }),
    ),
  tasks: (companyId: string, teamId: string, status?: string, assignee?: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/tasks", {
        params: {
          path: { companyId, teamId },
          query: {
            ...paging(cursor),
            status,
            assignee: assignee === "unassigned" ? undefined : assignee,
            unassigned: assignee === "unassigned",
          },
        },
      }),
    ),
  task: (companyId: string, teamId: string, id: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/tasks/{id}", {
        params: { path: { companyId, teamId, id } },
      }),
    ),
  createTask: (companyId: string, teamId: string, body: Model<"CreateTaskRequest">, key: string) =>
    result(
      client.POST("/api/v1/companies/{companyId}/teams/{teamId}/tasks", {
        params: { path: { companyId, teamId } },
        body,
        headers: { "Idempotency-Key": key },
      }),
    ),
  editTask: (row: Model<"TaskView">, body: Model<"UpdateTaskRequest">) =>
    result(
      client.PATCH("/api/v1/companies/{companyId}/teams/{teamId}/tasks/{id}", {
        params: { path: { companyId: row.companyId, teamId: row.teamId, id: row.id } },
        body,
        headers: condition(row.version),
      }),
    ),
  move: (row: Model<"TaskView">, targetColumn: string) =>
    result(
      client.POST("/api/v1/companies/{companyId}/teams/{teamId}/tasks/{id}/moves", {
        params: { path: { companyId: row.companyId, teamId: row.teamId, id: row.id } },
        body: { targetColumn },
        headers: condition(row.version),
      }),
    ),
  assignees: (companyId: string, teamId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/assignees", {
        params: { path: { companyId, teamId }, query: paging(cursor) },
      }),
    ),
  candidates: (companyId: string, teamId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/grant-candidates", {
        params: { path: { companyId, teamId }, query: paging(cursor) },
      }),
    ),
  grantGroups: (companyId: string, teamId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/grant-groups", {
        params: { path: { companyId, teamId }, query: paging(cursor) },
      }),
    ),
  grants: (companyId: string, teamId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/grants", {
        params: { path: { companyId, teamId }, query: paging(cursor) },
      }),
    ),
  grant: (
    companyId: string,
    teamId: string,
    type: string,
    id: string,
    role: number,
    version?: string,
  ) =>
    result(
      client.PUT("/api/v1/companies/{companyId}/teams/{teamId}/grants/{type}/{id}", {
        params: { path: { companyId, teamId, type, id } },
        body: { role },
        headers: condition(version),
      }),
    ),
  removeGrant: (companyId: string, teamId: string, row: Model<"GrantView">) =>
    result(
      client.DELETE("/api/v1/companies/{companyId}/teams/{teamId}/grants/{type}/{id}", {
        params: { path: { companyId, teamId, type: row.principalType, id: row.principalId } },
        headers: condition(row.version),
      }),
    ),
  effective: (companyId: string, teamId: string, id: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/teams/{teamId}/effective-access/{id}", {
        params: { path: { companyId, teamId, id } },
      }),
    ),
  groups: (companyId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/groups", {
        params: { path: { companyId }, query: paging(cursor) },
      }),
    ),
  createGroup: (companyId: string, name: string) =>
    result(
      client.POST("/api/v1/companies/{companyId}/groups", {
        params: { path: { companyId } },
        body: { name },
      }),
    ),
  groupMembers: (companyId: string, row: Model<"GroupView">, membershipIds: string[]) =>
    result(
      client.PUT("/api/v1/companies/{companyId}/groups/{id}/members", {
        params: { path: { companyId, id: row.id } },
        body: { membershipIds },
        headers: condition(row.version),
      }),
    ),
  clients: () => result(client.GET("/api/v1/clients")),
  approvals: (companyId: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/client-approvals", {
        params: { path: { companyId } },
      }),
    ),
  approval: (companyId: string, row: Model<"ApprovalView">) =>
    result(
      client.PUT("/api/v1/companies/{companyId}/client-approvals/{id}", {
        params: { path: { companyId, id: row.clientId } },
        body: { enabled: !row.enabled },
        headers: condition(row.version),
      }),
    ),
  connections: (companyId: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/connections", { params: { path: { companyId } } }),
    ),
  connect: (
    companyId: string,
    clientId: string,
    teamIds: string[],
    write: boolean,
    version?: string,
  ) =>
    result(
      client.PUT("/api/v1/companies/{companyId}/connections/{id}", {
        params: { path: { companyId, id: clientId } },
        body: { teamIds, read: true, write },
        headers: condition(version),
      }),
    ),
  disconnect: (companyId: string, row: Model<"ConnectionView">) =>
    result(
      client.DELETE("/api/v1/companies/{companyId}/connections/{id}", {
        params: { path: { companyId, id: row.clientId } },
        headers: condition(row.version),
      }),
    ),
  audit: (companyId: string, cursor?: string) =>
    result(
      client.GET("/api/v1/companies/{companyId}/audit-events", {
        params: { path: { companyId }, query: paging(cursor) },
      }),
    ),
  async signOut() {
    const token = await csrf();
    const form = document.createElement("form");
    form.method = "post";
    form.action = "/auth/logout";
    const input = document.createElement("input");
    input.type = "hidden";
    input.name = "__RequestVerificationToken";
    input.value = token;
    form.append(input);
    document.body.append(form);
    form.submit();
  },
};
