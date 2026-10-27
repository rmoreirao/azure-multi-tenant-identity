import { useQuery } from "@tanstack/react-query";
import { api, type Model } from "./api";
import { Action, Empty, field, MutationForm, PageState, usePages } from "./shared";

export function Approvals({ companyId }: { companyId: string }) {
  const approvals = useQuery({
    queryKey: ["approvals"],
    queryFn: () => api.approvals(companyId),
    retry: false,
  });
  return (
    <section>
      <h1>Company client approvals</h1>
      <p>
        Approval is off by default. Employees must separately authorize their own team and
        read/write subsets. Disabling approval blocks the next relevant REST or MCP operation.
      </p>
      <ul className="resource-list">
        {approvals.data?.map((row) => (
          <li key={row.clientId}>
            <div>
              <strong>{row.name}</strong>
              <p className="muted">{row.enabled ? "Approved" : "Not approved"}</p>
            </div>
            <Action run={() => api.approval(companyId, row)}>
              {row.enabled ? "Disable client" : "Approve client"}
            </Action>
          </li>
        ))}
      </ul>
      <PageState query={approvals} />
    </section>
  );
}
export function Connections({ companyId }: { companyId: string }) {
  const clients = useQuery({ queryKey: ["clients"], queryFn: api.clients, retry: false });
  const connections = useQuery({
    queryKey: ["connections"],
    queryFn: () => api.connections(companyId),
    retry: false,
  });
  const teams = usePages(["teams"], (c) => api.teams(companyId, c));
  return (
    <section>
      <h1>Your connections</h1>
      <p>
        Authorize only the teams and capabilities you want each client to use. Company approval and
        your current team role are checked on every operation.
      </p>
      {connections.data &&
        clients.data?.map((client) => {
          const connection = connections.data?.find((row) => row.clientId === client.id);
          const missingTeams = connection?.teamIds.some(
            (id) => !teams.items.some((team) => team.id === id),
          );
          return (
            <details className="detail-row" key={client.id}>
              <summary>
                {client.name}{" "}
                <span className="muted">
                  {connection?.active ? "Authorized" : "Not authorized"}
                </span>
              </summary>
              <MutationForm
                label="Authorize connection"
                reset={false}
                version={connection?.version ?? "new"}
                disabled={Boolean(
                  (missingTeams && teams.hasNextPage) ||
                    teams.isPending ||
                    teams.error ||
                    connections.error,
                )}
                submit={(data) =>
                  api.connect(
                    companyId,
                    client.id,
                    data.getAll("team").map(String),
                    field(data, "capability") === "write",
                    connection?.version,
                  )
                }
              >
                <label>
                  Capability
                  <select name="capability" defaultValue={connection?.write ? "write" : "read"}>
                    <option value="read">Read tasks</option>
                    <option value="write">Read and write tasks</option>
                  </select>
                </label>
                <div className="check-list">
                  {teams.items.map((team) => (
                    <label key={team.id}>
                      <input
                        type="checkbox"
                        name="team"
                        value={team.id}
                        defaultChecked={connection?.teamIds.includes(team.id)}
                      />
                      {team.name}
                    </label>
                  ))}
                </div>
                {missingTeams && (
                  <p className="muted">
                    {teams.hasNextPage
                      ? "Load the remaining team pages before saving. Existing selections must be visible before replacement is enabled."
                      : "Some previously selected teams are no longer available. Saving removes them; only the checked teams are authorized."}
                  </p>
                )}
              </MutationForm>
              {connection?.active && (
                <Action run={() => api.disconnect(companyId, connection)}>Revoke connection</Action>
              )}
            </details>
          );
        })}
      {!teams.isPending && !teams.items.length && (
        <Empty>
          No permitted teams. Ask a team manager for access before authorizing a connection.
        </Empty>
      )}
      <PageState query={clients} />
      <PageState query={connections} />
      <PageState query={teams} />
    </section>
  );
}
export function Audit({ company }: { company: Model<"CompanyView"> }) {
  const audit = usePages(["audit"], (c) => api.audit(company.id, c));
  return (
    <section>
      <h1>Audit events</h1>
      <p>
        Company administrators see directory and approval events. Team managers see events for their
        managed teams. Hidden team activity is not included.
      </p>
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Action</th>
              <th>Object</th>
              <th>Correlation</th>
            </tr>
          </thead>
          <tbody>
            {audit.items.map((row) => (
              <tr key={row.id}>
                <td>{new Date(row.createdAt).toLocaleString()}</td>
                <td>{row.action}</td>
                <td className="identifier">{row.objectId}</td>
                <td className="identifier">{row.correlationId}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {!audit.isPending && !audit.items.length && (
        <Empty>No events are visible with your current permissions.</Empty>
      )}
      <PageState query={audit} />
    </section>
  );
}
