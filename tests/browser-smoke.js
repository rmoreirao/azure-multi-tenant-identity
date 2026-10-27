async (page) => {
  const origin = "http://127.0.0.1:4173";
  const companyId = "11111111-1111-4111-8111-111111111111";
  const membershipId = "22222222-2222-4222-8222-222222222222";
  const teamId = "33333333-3333-4333-8333-333333333333";
  const taskId = "44444444-4444-4444-8444-444444444444";
  const clientId = "55555555-5555-4555-8555-555555555555";
  const secondTeamId = "66666666-6666-4666-8666-666666666666";
  const otherCompanyId = "77777777-7777-4777-8777-777777777777";
  const version = "AAAAAAAAAAE=";
  const company = { id: companyId, name: "UI smoke company", membershipId, administrator: true, version };
  const otherCompany = { ...company, id: otherCompanyId, name: "Other UI company", administrator: false };
  const member = { id: membershipId, name: "Synthetic UI employee", email: "ui@example.test", active: true, administrator: true, version };
  const team = { id: teamId, name: "UI smoke team", role: 3, version };
  const requests = [];
  const unexpected = [];
  const pageErrors = [];
  const checks = [];
  await page.context().unrouteAll({ behavior: "wait" });
  page.removeAllListeners("dialog");
  page.removeAllListeners("pageerror");
  const base = `/api/v1/companies/${companyId}`;
  let created = false;
  let teamCreated = false;
  let task;
  let conflict = true;
  let pagedTeams = false;
  let denyCompany = false;
  let companyFailure = true;
  let identityKey = "mocked-ui-only";
  let authenticated = true;
  let holdTasks = false;
  let releaseTasks;
  let finishTasks;
  let connection = { clientId, read: true, write: false, active: true, teamIds: [teamId, secondTeamId], version };
  page.on("pageerror", error => pageErrors.push(error.message));
  page.on("dialog", dialog => dialog.accept());
  const assert = (condition, name) => {
    if (!condition) throw new Error(name);
    checks.push(name);
  };
  const list = items => ({ items, nextCursor: null });
  await page.context().route("**/api/**", async route => {
    const request = route.request();
    const url = request.url();
    if (!url.startsWith(`${origin}/`)) throw new Error("The UI smoke test must never target a hosted API.");
    const path = url.slice(origin.length).split("?")[0];
    const method = request.method();
    const body = request.postData() ? request.postDataJSON() : null;
    requests.push({ path, method, body, headers: request.headers() });
    const send = (json, status = 200) => route.fulfill({ status, contentType: "application/json", json });
    if (path === "/api/session") return authenticated
      ? send({ authenticated: true, displayName: identityKey === "mocked-ui-only" ? "Synthetic UI employee" : "Different UI employee", identityKey, stage: "kanban-product" })
      : send({ title: "Your session ended." }, 401);
    if (path === "/api/antiforgery") return send({ token: "mocked-ui-only" });
    if (path === "/api/v1/companies") {
      if (method === "POST") {
        company.name = body.name;
        created = true;
        if (companyFailure) { companyFailure = false; return send({ title: "Database temporarily unavailable." }, 503); }
        return send(company);
      }
      return send(list(created ? [company, otherCompany] : []));
    }
    if (path === "/api/v1/invitations/accept") return send(company);
    if (path === "/api/v1/clients") return send([{ id: clientId, name: "Synthetic client" }]);
    if (path === base) return denyCompany ? send({ title: "Company access revoked." }, 404) : send(company);
    if (path === `/api/v1/companies/${otherCompanyId}`) return send(otherCompany);
    if (path === `/api/v1/companies/${otherCompanyId}/teams`) return send(list([]));
    if (path === `${base}/setup`) return send({ company, hasTeams: teamCreated, hasInvitations: false, hasGroups: false });
    if (path === `${base}/memberships`) return send(list([member]));
    if (path === `${base}/teams`) {
      if (method === "POST") { team.name = body.name; teamCreated = true; return send(team); }
      if (pagedTeams) return send(url.includes("cursor=second-page")
        ? list([{ ...team, id: secondTeamId, name: "Second smoke team" }])
        : { items: [team], nextCursor: "second-page" });
      return send(list(teamCreated ? [team] : []));
    }
    if (path === `${base}/connections`) return send([connection]);
    if (path === `${base}/connections/${clientId}`) {
      connection = { ...body, clientId, active: true, version: "AAAAAAAAAAI=" };
      return send(connection);
    }
    if (path === `${base}/teams/${teamId}`) return send(team);
    if (path === `${base}/teams/${teamId}/board`) return send({ id: teamId, teamId, name: "Team board", version });
    if (path === `${base}/teams/${teamId}/assignees` || path === `${base}/teams/${teamId}/grant-candidates`) return send(list([member]));
    if (path.endsWith("/grant-groups") || path.endsWith("/grants") || path === `${base}/audit`) return send(list([]));
    if (path === `${base}/teams/${teamId}/tasks`) {
      if (method === "POST") {
        task = { ...body, id: taskId, companyId, teamId, boardId: teamId, status: "todo", assigneeName: null,
          assigneeState: "unassigned", createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), version };
        return send({ taskId, version, createdAt: task.createdAt });
      }
      if (holdTasks) {
        holdTasks = false;
        await new Promise(resolve => { releaseTasks = resolve; });
        await send(list([{ ...task, title: "Late previous-company content" }]));
        finishTasks();
        return;
      }
      return send(list(task ? [task] : []));
    }
    if (path === `${base}/teams/${teamId}/tasks/${taskId}/moves`) {
      task = { ...task, status: body.targetColumn, version: "AAAAAAAAAAI=" };
      return send(task);
    }
    if (path === `${base}/teams/${teamId}/tasks/${taskId}`) {
      if (method === "PATCH" && conflict) {
        conflict = false;
        task = { ...task, title: "Concurrent server title", version: "AAAAAAAAAAM=" };
        return send({ title: "The record changed. Load its current version." }, 412);
      }
      return send(task);
    }
    unexpected.push(`${method} ${path}`);
    return send({ title: "Unmocked UI request." }, 500);
  });
  await page.goto(origin);
  await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
  await page.reload();
  await page.getByRole("heading", { name: "Your companies", exact: true }).waitFor();
  await page.getByLabel("Company name", { exact: true }).fill(company.name);
  await page.getByRole("button", { name: "Create company", exact: true }).click();
  await page.getByText("Database temporarily unavailable.", { exact: true }).waitFor();
  assert(!await page.getByLabel("Company name", { exact: true }).isEditable(), "An uncertain company result preserves its immutable attempt");
  await page.reload();
  await page.getByRole("button", { name: "Retry this creation", exact: true }).click();
  await page.getByRole("heading", { name: `Set up ${company.name}` }).waitFor();
  const creationAttempts = requests.filter(r => r.method === "POST" && r.path === "/api/v1/companies");
  assert(creationAttempts.length === 2 &&
    creationAttempts[0].headers["idempotency-key"] === creationAttempts[1].headers["idempotency-key"] &&
    creationAttempts[0].body.name === creationAttempts[1].body.name, "Reload retries company creation with the same key and content");
  assert(page.url().endsWith(`/companies/${companyId}/setup`), "Company creation opens optional setup");
  assert(requests.some(r => r.method === "POST" && r.path === "/api/v1/companies" && r.headers["idempotency-key"] && r.headers["x-csrf-token"]), "Creation sends an attempt key and CSRF proof");
  assert(await page.getByRole("link", { name: "Invite employees", exact: true }).getAttribute("href") === `/companies/${companyId}/directory`, "Setup invitation link keeps company context");
  await page.getByLabel("Team name", { exact: true }).fill(team.name);
  await page.getByRole("combobox", { name: "Initial team manager", exact: true }).selectOption(membershipId);
  await page.getByRole("button", { name: "Create team", exact: true }).click();
  await page.getByText("Team created", { exact: true }).waitFor();
  assert(requests.some(r => r.method === "POST" && r.path === `${base}/teams` && r.body.initialManagerMembershipId === membershipId), "Initial team manager is explicit");
  await page.getByRole("link", { name: "Go to teams", exact: true }).click();
  await page.getByRole("link", { name: team.name, exact: true }).click();
  await page.getByRole("heading", { name: team.name, exact: true }).waitFor();
  assert(await page.getByRole("link", { name: "Company setup", exact: true }).getAttribute("href") === `/companies/${companyId}/setup`, "Deep-page company navigation is absolute");
  await page.locator(".task-creation summary").click();
  await page.getByLabel("Title", { exact: true }).fill("Keyboard smoke task");
  await page.getByLabel("Description", { exact: true }).fill("Synthetic UI-only content.");
  await page.getByRole("button", { name: "Create task", exact: true }).click();
  await page.getByRole("link", { name: "Keyboard smoke task", exact: true }).waitFor();
  await page.getByRole("combobox", { name: "Move Keyboard smoke task to", exact: true }).selectOption("inProgress");
  await page.waitForFunction(() => document.querySelector(".task-card select")?.value === "inProgress");
  assert(requests.some(r => r.path.endsWith("/moves") && r.body.targetColumn === "inProgress" && r.headers["if-match"]), "Keyboard movement sends the current version");
  await page.getByRole("link", { name: "Keyboard smoke task", exact: true }).click();
  await page.getByRole("heading", { name: "Task details", exact: true }).waitFor();
  await page.getByLabel("Title", { exact: true }).fill("Preserved local draft");
  await page.getByRole("button", { name: "Save task", exact: true }).click();
  await page.getByText("Your draft is unchanged.", { exact: false }).waitFor();
  assert(await page.getByLabel("Title", { exact: true }).inputValue() === "Preserved local draft", "Version conflict preserves the draft");
  await page.getByRole("button", { name: "Load current task (discard draft)", exact: true }).click();
  await page.waitForFunction(() => document.querySelector('input[name="title"]')?.value === "Concurrent server title");
  assert(await page.getByRole("link", { name: "Back to board", exact: true }).getAttribute("href") === `/companies/${companyId}/teams/${teamId}`, "Task back-link returns to its board");
  await page.getByRole("link", { name: "Back to board", exact: true }).click();
  await page.getByRole("heading", { name: team.name, exact: true }).waitFor();
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: "artifacts\\browser-ui-board-desktop.png", fullPage: true });
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "Desktop board has no document overflow");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: "artifacts\\browser-ui-board-mobile.png", fullPage: true });
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "Mobile board has no document overflow");
  await page.evaluate(({ companyId, membershipId, teamId }) => sessionStorage.setItem(
    `kanban.task-attempt.${companyId}.${membershipId}.${teamId}`, JSON.stringify({
      key: "expired-ui-attempt-only", startedAt: Date.now() - 25 * 60 * 60 * 1000,
      body: { title: "Expired uncertain task", description: "", assigneeMembershipId: null }
    })), { companyId, membershipId, teamId });
  await page.reload();
  await page.getByRole("button", { name: "Retry this creation", exact: true }).waitFor();
  assert(await page.getByRole("button", { name: "Retry this creation", exact: true }).isDisabled(), "Expired task attempts cannot be resubmitted");
  await page.getByRole("button", { name: "Mark resolved and clear attempt", exact: true }).click();
  assert(requests.filter(r => r.method === "POST" && r.path === `${base}/teams/${teamId}/tasks`).length === 1, "Resolving an expired attempt does not create another task");
  pagedTeams = true;
  await page.goto(`${origin}/companies/${companyId}/connections`);
  await page.getByText("Synthetic client", { exact: false }).click();
  await page.getByRole("button", { name: "Authorize connection", exact: true }).waitFor();
  assert(await page.getByRole("button", { name: "Authorize connection", exact: true }).isDisabled(), "Connection replacement waits for existing selections");
  await page.getByRole("button", { name: "Load more", exact: true }).click();
  await page.getByLabel("Second smoke team", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Authorize connection", exact: true }).click();
  await page.waitForFunction(() => document.body.textContent.includes("Saved."));
  assert(requests.some(r => r.method === "PUT" && r.path === `${base}/connections/${clientId}` && r.body.teamIds.length === 2), "Loading another page preserves both connection team selections");
  await page.goto(`${origin}/invitations#token=${"a".repeat(64)}`);
  await page.getByRole("heading", { name: "Join a company", exact: true }).waitFor();
  assert(!page.url().includes("#"), "Invitation token is scrubbed from history");
  await page.getByRole("button", { name: "Accept invitation", exact: true }).click();
  await page.waitForURL(`${origin}/companies/${companyId}`);
  assert(requests.some(r => r.path === "/api/v1/invitations/accept" && r.body.token === "a".repeat(64)), "Invitation destination survives its local exchange");
  await page.getByRole("link", { name: team.name, exact: true }).click();
  await page.getByRole("heading", { name: team.name, exact: true }).waitFor();
  denyCompany = true;
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await page.getByText("Company access revoked.", { exact: true }).waitFor();
  assert(await page.getByRole("heading", { name: team.name, exact: true }).count() === 0, "Company denial hides cached business content");
  denyCompany = false;
  pagedTeams = false;
  await page.goto(`${origin}/companies/${companyId}/teams/${teamId}`);
  await page.getByRole("link", { name: "Concurrent server title", exact: true }).waitFor();
  const heldFinished = new Promise(resolve => { finishTasks = resolve; });
  const heldRequest = page.waitForRequest(r => r.method() === "GET" && r.url().split("?")[0] === `${origin}${base}/teams/${teamId}/tasks`);
  holdTasks = true;
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await heldRequest;
  try {
    await page.getByRole("link", { name: "Switch company", exact: true }).click();
    await page.getByRole("link", { name: otherCompany.name, exact: true }).click();
    await page.getByRole("heading", { name: "Team boards", exact: true }).waitFor();
    releaseTasks();
    await heldFinished;
    assert(page.url().endsWith(`/companies/${otherCompanyId}`) &&
      await page.getByText("Late previous-company content", { exact: true }).count() === 0, "A late response cannot restore the previous company's content or navigation");
  } finally { releaseTasks?.(); }
  await page.getByRole("link", { name: "Switch company", exact: true }).click();
  await page.getByRole("link", { name: company.name, exact: true }).click();
  await page.getByRole("link", { name: team.name, exact: true }).click();
  await page.getByRole("link", { name: "Concurrent server title", exact: true }).waitFor();
  assert(await page.getByText("Late previous-company content", { exact: true }).count() === 0, "Switching back loads fresh server state instead of the discarded response");
  await page.clock.install();
  await page.clock.fastForward(16000);
  identityKey = "different-mocked-employee";
  denyCompany = true;
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await page.getByText("Different UI employee", { exact: true }).waitFor();
  await page.getByText("Company access revoked.", { exact: true }).waitFor();
  assert(await page.getByRole("link", { name: "Concurrent server title", exact: true }).count() === 0, "An identity change discards the former employee's cached content");
  await page.clock.fastForward(16000);
  authenticated = false;
  await page.evaluate(() => window.dispatchEvent(new Event("visibilitychange")));
  await page.getByRole("link", { name: "Sign in or create an account", exact: true }).waitFor();
  assert(await page.getByText("Different UI employee", { exact: true }).count() === 0 &&
    await page.getByText("Company access revoked.", { exact: true }).count() === 0, "Session expiry removes employee and company content");
  assert(unexpected.length === 0, `All UI requests are explicitly mocked: ${unexpected.join(", ")}`);
  assert(pageErrors.length === 0, `No unhandled browser exceptions: ${pageErrors.join(", ")}`);
  return { mode: "static UI with explicitly mocked HTTP responses", realProviderAccepted: false,
    realSqlJourney: false, checks, screenshots: ["artifacts\\browser-ui-board-desktop.png", "artifacts\\browser-ui-board-mobile.png"] };
}
