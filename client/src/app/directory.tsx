import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { api, type Model } from "./api";
import { Action, Empty, field, MutationForm, PageState, roles, usePages } from "./shared";

export function Directory({ company }: { company: Model<"CompanyView"> }) {
  const members = usePages(["members"], (c) => api.members(company.id, c));
  const invitations = usePages(["invitations"], (c) => api.invitations(company.id, c));
  const [link, setLink] = useState("");
  return (
    <section>
      <h1>Directory & invitations</h1>
      <p>
        Membership belongs to this company only. Reactivating an employee does not restore old team
        roles, groups, administrator status or connections.
      </p>
      <h2>Invite an employee</h2>
      <MutationForm
        label="Create invitation"
        submit={async (data) => {
          const row = await api.invite(company.id, field(data, "email"));
          setLink(`${location.origin}/invitations#token=${row.token}`);
        }}
      >
        <label>
          Email address
          <input name="email" type="email" required maxLength={320} autoComplete="email" />
        </label>
        <p className="muted">
          Valid for seven days, single-use, and bound to this address. No email is sent
          automatically.
        </p>
      </MutationForm>
      {link && (
        <div className="invitation-result" role="status">
          <label>
            Copy this link and share it using your approved channel
            <input readOnly value={link} onFocus={(e) => e.currentTarget.select()} />
          </label>
          <p>This is the only time the full link is shown.</p>
        </div>
      )}
      <h2>Employees</h2>
      <ul className="resource-list">
        {members.items.map((member) => (
          <li key={member.id}>
            <div>
              <strong>{member.name}</strong>
              <p>{member.email}</p>
              <span className="muted">
                {member.active ? "Active" : "Inactive"}
                {member.administrator ? " / Company administrator" : ""}
              </span>
            </div>
            <Action
              run={() => api.membership(company.id, member)}
              confirm={
                member.active
                  ? `Deactivate ${member.name} in this company? Existing assignments remain, marked inactive.`
                  : undefined
              }
            >
              {member.active ? "Deactivate" : "Reactivate membership"}
            </Action>
          </li>
        ))}
      </ul>
      <PageState query={members} />
      <h2>Invitations</h2>
      <ul className="resource-list">
        {invitations.items.map((row) => (
          <li key={row.id}>
            <div>
              <strong>{row.email}</strong>
              <p>Expires {new Date(row.expiresAt).toLocaleString()}</p>
              <span className="muted">
                {row.consumed
                  ? "Accepted"
                  : row.revoked
                    ? "Revoked"
                    : new Date(row.expiresAt).getTime() <= Date.now()
                      ? "Expired"
                      : "Pending"}
              </span>
            </div>
            {!row.revoked && !row.consumed && (
              <Action run={() => api.revokeInvitation(company.id, row)}>Revoke</Action>
            )}
          </li>
        ))}
      </ul>
      {!invitations.isPending && invitations.items.length === 0 && (
        <Empty>No invitations have been issued.</Empty>
      )}
      <PageState query={invitations} />
    </section>
  );
}
function GroupEditor({
  companyId,
  group,
  members,
}: {
  companyId: string;
  group: Model<"GroupView">;
  members: Model<"MemberView">[];
}) {
  const [selected, setSelected] = useState(group.memberIds);
  return (
    <details className="detail-row">
      <summary>
        {group.name} <span className="muted">{group.memberIds.length} active members</span>
      </summary>
      <MutationForm
        label="Save group membership"
        reset={false}
        version={group.version}
        reload={() => setSelected(group.memberIds)}
        submit={() => api.groupMembers(companyId, group, selected)}
      >
        <p>
          Select up to 100 active employees. Removing one group source does not remove other team
          grants.
        </p>
        <div className="check-list">
          {members
            .filter((m) => m.active)
            .map((m) => (
              <label key={m.id}>
                <input
                  type="checkbox"
                  checked={selected.includes(m.id)}
                  onChange={(event) => {
                    setSelected((ids) =>
                      event.target.checked ? [...ids, m.id] : ids.filter((id) => id !== m.id),
                    );
                  }}
                />
                {m.name}
              </label>
            ))}
        </div>
        {selected.some((id) => !members.some((m) => m.id === id)) && (
          <p className="muted">
            Some selected employees are on later pages. They remain selected; load more directory
            entries to edit them.
          </p>
        )}
      </MutationForm>
    </details>
  );
}
export function Groups({ companyId }: { companyId: string }) {
  const groups = usePages(["groups"], (c) => api.groups(companyId, c));
  const members = usePages(["members"], (c) => api.members(companyId, c));
  return (
    <section>
      <h1>Company groups</h1>
      <p>
        Groups organize people. A team manager must grant a group a role before it gives board
        access.
      </p>
      <MutationForm
        label="Create group"
        submit={(data) => api.createGroup(companyId, field(data, "name"))}
      >
        <label>
          Group name
          <input name="name" required maxLength={200} />
        </label>
      </MutationForm>
      <h2>Membership</h2>
      {groups.items.map((group) => (
        <GroupEditor key={group.id} companyId={companyId} group={group} members={members.items} />
      ))}
      {!groups.isPending && !groups.items.length && <Empty>No groups yet.</Empty>}
      <PageState query={groups} />
      <PageState query={members} />
    </section>
  );
}
export function TeamAdministration({ companyId }: { companyId: string }) {
  const teams = usePages(["admin-teams"], (c) => api.adminTeams(companyId, c));
  const members = usePages(["members"], (c) => api.members(companyId, c));
  return (
    <section>
      <h1>Team administration</h1>
      <p>
        This directory contains team names only, not boards or tasks. Manager recovery is available
        only when no active effective manager remains.
      </p>
      {teams.items.map((team) => (
        <details className="detail-row" key={team.id}>
          <summary>{team.name}</summary>
          <MutationForm
            label="Appoint replacement manager"
            version={team.version}
            submit={(data) => api.recover(companyId, team, field(data, "manager"))}
          >
            <label>
              Replacement manager
              <select name="manager" required defaultValue="">
                <option value="" disabled>
                  Choose an active employee
                </option>
                {members.items
                  .filter((m) => m.active)
                  .map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.name}
                    </option>
                  ))}
              </select>
            </label>
            <p className="muted">
              This grants the selected employee manager access. It does not grant you access.
            </p>
          </MutationForm>
        </details>
      ))}
      <PageState query={teams} />
      <PageState query={members} />
    </section>
  );
}
export function TeamAccess({ companyId, teamId }: { companyId: string; teamId: string }) {
  const grants = usePages(["grants", teamId], (c) => api.grants(companyId, teamId, c));
  const members = usePages(["grant-candidates", teamId], (c) =>
    api.candidates(companyId, teamId, c),
  );
  const groups = usePages(["grant-groups", teamId], (c) => api.grantGroups(companyId, teamId, c));
  const [person, setPerson] = useState("");
  const effective = useQuery({
    queryKey: ["effective", teamId, person],
    queryFn: () => api.effective(companyId, teamId, person),
    enabled: !!person,
    retry: false,
  });
  const name = (g: Model<"GrantView">) =>
    (g.principalType === "member" ? members.items : groups.items).find(
      (p) => p.id === g.principalId,
    )?.name ?? g.principalId;
  return (
    <section>
      <h1>Team access</h1>
      <p>
        The highest current direct or group role wins. Company administration never substitutes for
        a team grant.
      </p>
      <MutationForm
        label="Grant employee access"
        submit={(data) => {
          const id = field(data, "person");
          const old = grants.items.find(
            (g) => g.principalType === "member" && g.principalId === id,
          );
          return api.grant(
            companyId,
            teamId,
            "member",
            id,
            Number(field(data, "role")),
            old?.version,
          );
        }}
      >
        <label>
          Employee
          <select name="person" required defaultValue="">
            <option value="" disabled>
              Choose an employee
            </option>
            {members.items.map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
              </option>
            ))}
          </select>
        </label>
        <RoleSelect />
      </MutationForm>
      <MutationForm
        label="Grant group access"
        submit={(data) => {
          const id = field(data, "group");
          const old = grants.items.find((g) => g.principalType === "group" && g.principalId === id);
          return api.grant(
            companyId,
            teamId,
            "group",
            id,
            Number(field(data, "role")),
            old?.version,
          );
        }}
      >
        <label>
          Group
          <select name="group" required defaultValue="">
            <option value="" disabled>
              Choose a group
            </option>
            {groups.items.map((g) => (
              <option key={g.id} value={g.id}>
                {g.name}
              </option>
            ))}
          </select>
        </label>
        <RoleSelect />
      </MutationForm>
      <h2>Explicit grants</h2>
      <ul className="resource-list">
        {grants.items.map((g) => (
          <li key={g.id}>
            <div>
              <strong>{name(g)}</strong>
              <p>
                {g.principalType} / {roles[Number(g.role)]}
              </p>
            </div>
            <Action
              run={() => api.removeGrant(companyId, teamId, g)}
              confirm="Remove this access source? Other direct or group sources may still give access."
            >
              Remove source
            </Action>
          </li>
        ))}
      </ul>
      <PageState query={grants} />
      <PageState query={members} />
      <PageState query={groups} />
      <h2>Inspect effective access</h2>
      <label>
        Employee
        <select value={person} onChange={(e) => setPerson(e.target.value)}>
          <option value="">Choose an employee</option>
          {members.items.map((m) => (
            <option key={m.id} value={m.id}>
              {m.name}
            </option>
          ))}
        </select>
      </label>
      <PageState query={effective} />
      {effective.data && (
        <div role="status">
          <strong>{roles[Number(effective.data.role)]}</strong>
          <ul>
            {effective.data.sources.map((g) => (
              <li key={g.id}>
                {g.principalType}: {name(g)} / {roles[Number(g.role)]}
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
function RoleSelect() {
  return (
    <label>
      Team role
      <select name="role" defaultValue="1">
        <option value="1">Viewer - read only</option>
        <option value="2">Member - manage tasks</option>
        <option value="3">Team manager - manage tasks and access</option>
      </select>
    </label>
  );
}
