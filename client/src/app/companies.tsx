import { Button } from "@fluentui/react-components";
import { useMutation, useQuery } from "@tanstack/react-query";
import { type FormEvent, useState } from "react";
import { Link, useNavigate } from "react-router";
import { api, type Model } from "./api";
import { Empty, Feedback, field, MutationForm, PageState, usePages } from "./shared";

type Attempt = { key: string; name: string };
export function Companies({ identityKey }: { identityKey: string }) {
  const companies = usePages(["companies"], api.companies);
  const navigate = useNavigate();
  const storage = `kanban.company-attempt.${identityKey}`;
  const [storageError, setStorageError] = useState<Error | null>(null);
  const [attempt, setAttempt] = useState<Attempt | null>(() => {
    const saved = localStorage.getItem(storage);
    if (!saved) return null;
    const value: unknown = JSON.parse(saved);
    if (
      !value ||
      typeof value !== "object" ||
      !("key" in value) ||
      typeof value.key !== "string" ||
      !("name" in value) ||
      typeof value.name !== "string"
    )
      throw new Error("The saved company attempt is invalid. Do not retry with a new key.");
    return { key: value.key, name: value.name };
  });
  const create = useMutation({
    mutationFn: (value: Attempt) => api.createCompany(value.name, value.key),
    onSuccess: (company) => {
      localStorage.removeItem(storage);
      setAttempt(null);
      navigate(`/companies/${company.id}/setup`);
    },
  });
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = attempt ?? {
      key: crypto.randomUUID(),
      name: field(new FormData(event.currentTarget), "name").trim(),
    };
    if ([...value.name].length < 1 || [...value.name].length > 100) {
      setStorageError(new Error("Use a company name between 1 and 100 characters."));
      return;
    }
    try {
      localStorage.setItem(storage, JSON.stringify(value));
    } catch {
      setStorageError(
        new Error(
          "Your browser could not preserve this attempt for safe retries. Creation was not sent.",
        ),
      );
      return;
    }
    setStorageError(null);
    setAttempt(value);
    create.mutate(value);
  }
  return (
    <section>
      <div className="page-heading">
        <div>
          <h1>Your companies</h1>
          <p>Select a company, or create one with immediate access as its administrator.</p>
        </div>
      </div>
      <div className="company-layout">
        <div>
          <h2>Companies you belong to</h2>
          <ul className="resource-list">
            {companies.items.map((c) => (
              <li key={c.id}>
                <Link to={`/companies/${c.id}`}>{c.name}</Link>
                <span className="muted">
                  {c.administrator ? "Company administrator" : "Employee"}
                </span>
              </li>
            ))}
          </ul>
          {!companies.isPending && !companies.error && companies.items.length === 0 && (
            <Empty>
              You do not belong to a company yet. Create one, or ask an administrator for an
              invitation.
            </Empty>
          )}
          <PageState query={companies} />
        </div>
        <section className="creation-panel">
          <h2>Create a company</h2>
          <p>No approval queue. Team access is granted separately.</p>
          <form className="editor-form" onSubmit={submit}>
            <label>
              Company name
              <input
                name="name"
                required
                maxLength={200}
                defaultValue={attempt?.name}
                readOnly={attempt !== null}
                autoComplete="organization"
              />
            </label>
            <Feedback error={storageError ?? create.error} />
            {attempt && !create.isSuccess && (
              <p className="muted">
                This attempt is saved. Retrying uses the same key and cannot create a duplicate
                company.
              </p>
            )}
            <Button type="submit" appearance="primary" disabled={create.isPending}>
              {create.isPending
                ? "Creating..."
                : attempt
                  ? "Retry this creation"
                  : "Create company"}
            </Button>
          </form>
        </section>
      </div>
    </section>
  );
}
export function InvitationPage({ signedIn }: { signedIn: boolean }) {
  const navigate = useNavigate();
  const [token] = useState(() => {
    const incoming = new URLSearchParams(location.hash.slice(1)).get("token");
    if (incoming) {
      if (!/^[a-fA-F0-9]{64}$/.test(incoming)) throw new Error("The invitation link is invalid.");
      sessionStorage.setItem("kanban.invitation", incoming);
      history.replaceState(null, "", "/invitations");
    }
    return sessionStorage.getItem("kanban.invitation");
  });
  const accept = useMutation({
    mutationFn: () => api.accept(token ?? ""),
    onSuccess: (company) => {
      sessionStorage.removeItem("kanban.invitation");
      navigate(`/companies/${company.id}`);
    },
  });
  return (
    <section className="identity-panel">
      <h1>Join a company</h1>
      <p>
        Use the account whose email address received this invitation. Joining does not grant team or
        administrator access.
      </p>
      {!token ? (
        <Empty>Open the complete invitation link shared by the company administrator.</Empty>
      ) : !signedIn ? (
        <Button as="a" href="/auth/login?returnTo=%2Finvitations" appearance="primary">
          Sign in to accept
        </Button>
      ) : (
        <Button appearance="primary" disabled={accept.isPending} onClick={() => accept.mutate()}>
          Accept invitation
        </Button>
      )}
      <Feedback error={accept.error} />
    </section>
  );
}
export function Setup({ company }: { company: Model<"CompanyView"> }) {
  const setup = useQuery({
    queryKey: ["setup"],
    queryFn: () => api.setup(company.id),
    retry: false,
  });
  const members = usePages(["members"], (cursor) => api.members(company.id, cursor));
  return (
    <section>
      <h1>Set up {company.name}</h1>
      <p>Your company is active. These steps are optional; return here whenever you are ready.</p>
      <PageState query={setup} />
      {setup.data && (
        <ol className="setup-list">
          <li>
            <strong>{setup.data.hasTeams ? "Team created" : "Create your first team"}</strong>
            <p>
              Choose an initial manager explicitly. Company administrators cannot see boards without
              a team grant.
            </p>
          </li>
          <li>
            <Link to={`/companies/${company.id}/directory`}>Invite employees</Link>
            <p>Share a seven-day invitation using your approved channel.</p>
          </li>
          <li>
            <Link to={`/companies/${company.id}/groups`}>Arrange groups</Link>
            <p>Groups only grant access when a team manager gives them an explicit role.</p>
          </li>
        </ol>
      )}
      <h2>Create a team</h2>
      <MutationForm
        label="Create team"
        submit={(data) => api.createTeam(company.id, field(data, "name"), field(data, "manager"))}
      >
        <label>
          Team name
          <input name="name" required maxLength={200} />
        </label>
        <label>
          Initial team manager
          <select name="manager" required defaultValue="">
            <option value="" disabled>
              Choose an active employee
            </option>
            {members.items
              .filter((m) => m.active)
              .map((m) => (
                <option key={m.id} value={m.id}>
                  {m.name}
                  {m.id === company.membershipId ? " (you)" : ""}
                </option>
              ))}
          </select>
        </label>
        <p className="muted">
          Only the chosen employee receives manager access. One default board is created with the
          team.
        </p>
      </MutationForm>
      <PageState query={members} />
      <Link to={`/companies/${company.id}`}>Go to teams</Link>
    </section>
  );
}
