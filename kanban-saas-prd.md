# B2B Kanban SaaS PoC: draft PRD

Status: product source implemented and deployed for synthetic-PoC acceptance; not fully accepted or compliance-approved. Current results and limitations are in implementation specification section 13.4.
Date: 2026-10-23.
Platform: web, REST API, and remote MCP.

Companion: [solution architecture and technical specification](kanban-saas-technical-design.md).
Implementation choices: [technical implementation specification](kanban-saas-implementation-spec.md).
Product extension: [self-service company onboarding PRD](kanban-saas-self-service-onboarding-prd.md), including immediate creation without operator approval.

## 1. Product purpose and agreed constraints

Enable employees to manage their team's tasks through a kanban UI or an employee-delegated API/MCP client. Demonstrate that companies and teams remain isolated across all three interfaces.

Market fit is accepted for this exercise. The objective is a small, secure, responsive PoC, not a complete project-management platform.

| Area | Agreed requirement |
| --- | --- |
| Customers | Companies; each company is an application tenant |
| Users | Verified company creators and invited employees with work emails; corporate SSO can come later |
| Memberships | One person can belong to multiple companies and teams |
| Permissions | Team-level only; boards and tasks inherit team permissions |
| Groups | App-managed, company-scoped groups |
| Team roles | Viewer, Member, Team manager |
| Company administrators | No automatic access to team content; explicit team grants required |
| Integration identity | Employee-delegated only; no unattended/service-account access |
| MCP capabilities | Read and create/update/move tasks; no access-administration tools |
| MCP clients | VS Code, GitHub Copilot App, Claude Code |
| Workload | Up to 5 companies, 100 registered users, 10 simultaneously active users |
| Collaboration | Manual refresh is sufficient; immediate feedback for one's own edits |
| Hosting | Azure Sweden Central; keep the application warm |
| Cost | Minimize cost without compromising these requirements; no fixed budget |
| Files | No uploads or document library |
| Sensitive data | Synthetic data until the relevant customer security/compliance review approves real data |

These workload numbers are a PoC sizing envelope, not a claim of unlimited capacity or a required commercial seat limit.

Implementation decisions: accept the existing identity configuration as the synthetic-PoC baseline; no per-user or environment company-count quota; invitations expire after seven days. Retain an existing task assignment after contributor access is lost and display it as inactive. New/changed assignees must still be active contributors. A company administrator may repair a managerless team by explicitly appointing a manager without receiving content access.

## 2. Personas and user journeys

| Persona | Job to be done | Successful journey |
| --- | --- | --- |
| Company administrator | Establish and maintain the company's access structure | Create a company through self-service or receive a seeded company account, invite employees, create teams/groups, review access changes, offboard an employee |
| Team manager | Keep the team's board and access organized | Configure the team's board, grant existing employees/groups a team role, inspect effective access |
| Team member | Keep work visible and current | Sign in, select company/team, create or assign a task, move it between columns |
| Viewer | Follow progress without changing work | Open permitted boards and task details; editing remains unavailable in every interface |
| Employee using an AI client | Manage the same permitted tasks conversationally | Connect an approved client, authorize a company/team scope, read or update a task as themselves |

The AI client is not an independent privileged user.

### Core journeys

1. **Onboard a company:** an eligible verified user creates an active company immediately through self-service, without operator approval; the proposed default appoints the creator as its initial company administrator. Operator-led creation and explicit initial administrator appointment remain available. That role alone grants no team-content access. See the [self-service onboarding PRD](kanban-saas-self-service-onboarding-prd.md) for eligibility, setup, safeguards, and acceptance criteria.
2. **Admit an employee:** a company administrator issues a time-limited, single-use invitation to an approved work address. The employee authenticates with the managed identity provider, accepts the invitation, and obtains only the specified company membership. No email-domain auto-join.
3. **Grant team access:** create a company-scoped group, add company members, and grant the group a team role. The employee sees only permitted team content.
4. **Manage work:** select the company and team, open the board, create/edit a task, and move it. Another person's changes appear after Refresh.
5. **Use MCP:** the company permits an AI-client connection; the employee authorizes a company/team subset and read/write capability. The client calls task tools using employee-delegated access.
6. **Offboard:** deactivate the employee's company membership. The next UI/API/MCP request to that company is denied, without affecting their memberships in other companies.

## 3. Proposed MVP defaults

These defaults are recommendations, not additional decisions already confirmed in the interview.

| Include | Defer |
| --- | --- |
| One default board per team | Multiple boards per team, custom workflows, WIP limits |
| Fixed columns: To do, In progress, Done | Sprints, dependencies, time tracking |
| Task title, plain-text description, status, optional assignee | Rich-text editing, attachments, comments, notifications |
| Company/team switchers and permitted-board navigation | Cross-company reporting or search |
| Direct and group-based team grants | Nested groups, custom roles, explicit-deny rules |
| Task filtering and bounded pagination | Dedicated search infrastructure |
| Version-aware edits and mutation/access-change audit events | Compliance-grade immutable audit storage |
| A documented REST API and a small MCP tool set | Webhooks, background automation, public integration marketplace |
| Immediate verified-user self-service and operator-led company onboarding; invitation links shared through an approved channel | Billing, anonymous company creation, domain claiming, automated invitation-email service |

New teams require an explicit initial Team manager grant. New/changed assignees must be active team contributors; retained assignments may be inactive. Tasks cannot be moved between companies or teams in this PoC.

## 4. Authorization model

| Role | Read team tasks | Create/edit/move tasks | Manage team/board settings and grants | Manage company directory/groups |
| --- | --- | --- | --- | --- |
| Viewer | Yes | No | No | No |
| Member | Yes | Yes | No | No |
| Team manager | Yes | Yes | Yes, within that team | No |
| Company administrator | Only with a separate team grant | Only with a separate team grant | Company administration does not bypass team policies | Yes |

Company administrators may create teams and arrange explicit initial manager grants. They may grant access only to active members/groups of their own company.

Effective access is the highest remaining applicable direct or group role. There are no nested groups or explicit-deny rules. The UI must show the sources of effective access: removing a direct grant does not revoke access still supplied by a group.

Protected company operations require active company membership and resource ownership within that company; team-content operations additionally require the relevant team role. Self-service company creation and invitation acceptance are narrow authenticated, verified-address exceptions to the prior-membership requirement, not general authorization bypasses. API/MCP task operations additionally require the appropriate delegated capability and, for MCP, an active company-approved, employee-authorized connection grant.

**Administrative trust limit:** an administrator who can change group memberships or arrange grants can potentially grant themselves content access. Requiring explicit grants removes an automatic bypass; it does not make the administrator incapable of self-granting. Such changes must be audited. Preventing self-grants would require a separate approval model outside this PoC.

### Minimal conceptual data model

```text
User ----< CompanyMembership >---- Company
                    |                 |
                    |                 +----< Team ----< Board ----< Task
                    |                 |
                    +----< GroupMember >---- Group

TeamGrant: company + team + company-member/group + role
Invitation: company + approved email + expiry + token hash
McpConnectionGrant: company + member + client registration + teams + capabilities
AuditEvent: company + actor + client + action + object + time + correlation
```

Users are global identities; memberships, teams, groups, invitations, grants, boards, and tasks are company-scoped. A company administrator removes a company membership, not the shared identity used by other companies.

Use an internal User ID mapped to the provider's verified immutable identity. For Entra, use the identity-directory ID plus object ID (`oid`), not email or a client-specific/pairwise `sub`. The Entra directory ID is not the application Company ID.

## 5. Recommended Azure architecture

**One modular monolith on Azure App Service Linux B1, one provisioned Azure SQL Database S0, and Microsoft Entra External ID.**

The technical interview selected one shared synthetic-data SQL database for local development and the deployed PoC, with a public SQL endpoint rather than private networking. There is no second development/CI database or GitHub Actions pipeline. The existing External ID tenant is a prerequisite, not a directory to provision for each deployment.

```text
Browser UI                         VS Code / Copilot App / Claude Code
    | HTTPS                                      | HTTPS + access token
    +---------------------+----------------------+
                          |
           Azure App Service Linux B1
           One app, one instance, Always On
           +----------------------------------+
           | Web UI / same-origin BFF         |
           | REST API: /api/v1                |
           | MCP: /mcp, Streamable HTTP       |
           +----------------------------------+
           | Shared application services     |
           | Company/team authorization      |
           | Validation, concurrency, audit  |
           +----------------------------------+
                  |                    |
                  | managed identity   +--> Application Insights
                  |                         metadata, not task content
            TLS + SQL IP firewall
                  |
           Azure SQL Database S0
           Public endpoint; shared schema

Microsoft Entra External ID <---- OIDC/OAuth sign-in and token issuance
Azure Key Vault             <---- Required application secrets/credentials
```

App, SQL, Key Vault, and the monitoring workspace should be placed in Sweden Central where supported. Select an appropriate identity geography separately: Sweden Central deployment is not a guarantee that identity records, backups, every diagnostic flow, or AI-client processing remain in Sweden.

### Why this is the small starting point

- App Service B1 supports a warm, predictable application without operating a container platform. Deploy to the built-in runtime rather than adding a container registry solely for this PoC.
- SQL naturally models memberships, groups, grants, tasks, and transactional updates. S0 provides a low fixed starting cost without auto-pause/resume delays.
- UI, REST, and MCP are adapters over the same application services, not separate implementations of security and business rules.
- SQL uses its public endpoint with certificate-validated TLS, Entra authentication, and firewall rules limited to approved developer and App Service outbound IPs. Do not enable unrestricted internet access or the broad "Allow Azure services" exception. SQL private endpoints, private DNS, and VNet integration are not part of this selected PoC baseline.
- Managed identity authenticates the application to SQL and Key Vault. The runtime database principal is least-privileged; migrations use a separate deployment identity.
- App Service and SQL have monitoring and capacity limits. Scale the demonstrated bottleneck before adding services.

Do not initially add Kubernetes, API Management, Redis, Cosmos DB, Service Bus, SignalR, Front Door/WAF, microservices, or a database per company. A customer security review may subsequently require an edge/WAF or a different isolation model.

There is no server-side LLM and no Azure OpenAI/Foundry requirement. MCP exposes tools to the user's existing AI client.

**Tradeoffs:** one application instance can interrupt service during restarts/releases; this is not an end-to-end HA design. Companies share database capacity and operational blast radius. SQL backup restore is database-wide; selective company recovery requires restoring separately and controlled extraction, not overwriting the live shared database.

The developer also shares that SQL database: migrations and integration tests can affect the deployed PoC. Use explicitly scoped synthetic fixtures, never automated database reset, and refresh firewall rules when approved outbound IPs change. Public SQL exposure and shared development increase operational risk and must be reconsidered before real-data approval.

## 6. Security baseline

| Risk | Required control |
| --- | --- |
| Cross-company object access | Validate active membership and company/team ownership on every read and write, including lists, filters, search, assignment, and MCP tools |
| Missed tenant filters | Company ID on tenant-owned rows, company-aware foreign keys, tenant-first indexes, and SQL row-level security as defense in depth |
| Incorrect SQL security context | Establish context only after membership validation; initialize/reset context for pooled connections; include write-block predicates and fail closed |
| Stale authorization | Resolve current memberships/grants server-side for each operation; do not treat long-lived token group claims as current app permissions |
| Account takeover | Managed authentication and MFA; no passwords handled or stored by the application |
| Token misuse | Validate issuer, signature, audience, expiry, delegated scopes, and supported client registration; never accept an ID token as API authorization |
| Browser attacks | Same-origin BFF, Secure/HttpOnly cookies, suitable SameSite policy, CSRF protection, CSP, escaped plain-text task content |
| Agent overreach | Company/team-bounded connection grants, separate task read/write capabilities, no permission-administration or bulk-write tools |
| Prompt injection | Treat task content/tool output as untrusted data; enforce all restrictions on the server rather than relying on agent behavior or client confirmations |
| Accidental disclosure | Do not log task descriptions, tokens, authorization headers, or request bodies; restrict audit/diagnostic access |
| Abuse/noisy neighbors | Bounded page sizes and input lengths; per-user/company rate and concurrency limits |
| Operational compromise | Least-privilege Azure access, protected deployment credentials, MFA for operators, controlled exceptional support access |

Row-level security is not a substitute for team authorization, and does not protect against a fully compromised application or privileged database operator.

For the initial managed sign-in, a supported low-complexity option is email/password with email-code MFA, configured at the provider. Email OTP alone is not MFA. Entra currently requires SMS for a second factor when email OTP is the first factor; passkeys have additional eligibility/custom-domain requirements. Choose stronger phishing-resistant sign-in or corporate SSO before real-data use if the customer's review requires it.

Record task mutations and access changes with actor, client, company, team/object IDs, timestamp, and correlation ID. Commit business mutations and their audit events atomically. A database audit table is not a claim of tamper-proof compliance logging.

Removing access blocks subsequent requests. It cannot retract data already displayed, downloaded, or placed in an AI conversation.

## 7. REST and MCP contract

REST is versioned and documented with OpenAPI. Resource paths explicitly identify company and team; these IDs are selectors, never proof of permission.

Use the official MCP SDK with stateless Streamable HTTP for bounded request/response task tools:

```text
list_teams(companyId)
list_tasks(companyId, teamId, filters, cursor, limit)
get_task(companyId, teamId, taskId)
create_task(companyId, teamId, fields, idempotencyKey)
update_task(companyId, teamId, taskId, fields, expectedVersion)
move_task(companyId, teamId, taskId, targetColumn, expectedVersion)
```

Only permitted teams/tasks are returned. Mutation fields are allowlisted; callers cannot change company/team ownership. Retried creates use idempotency protection. Stale writes return an explicit version conflict rather than silently overwriting another employee's work.

MCP clients use employee-delegated OAuth authorization code flow with PKCE, protected-resource discovery, correct resource/audience handling, and narrow task capabilities. Do not build a custom OAuth server, pass through unrelated tokens, or distribute a shared client secret to desktop clients.

For known clients, prefer pre-registered public OAuth clients where supported. VS Code documents an OAuth client-ID setting; Claude Code documents preconfigured public clients and fixed callback ports. Do not assume the GitHub Copilot App behaves identically to Copilot CLI, or that Entra and every client support the same discovery/registration flow.

**First implementation gate:** demonstrate sign-in, task read/write, token renewal, and access revocation in the exact supported versions of all three clients. If the provider/client combination is incompatible, resolve that before expanding the app; do not bypass it with a shared API key. No all-three compatibility claim is made by this proposal.

Company approval of AI-client use is off by default. An employee's connection grant selects a company, allowed teams, and read/write capabilities, and can never exceed their current team access. Do not use a process-global "current company" for MCP sessions.

Task content returned through MCP may be transmitted to the client's AI provider. Company approval must consider destination, retention, training use, subprocessors, and applicable contracts. A public OAuth client ID labels a registration; it is not cryptographic proof of the client binary's authenticity.

## 8. Low-fidelity UI

### Board: explicit company/team context

```text
+--------------------------------------------------------------------------------+
| Kanban     Company [Acme v]                              Alice [Account v]     |
| PoC: synthetic data only                                                       |
+-------------------+------------------------------------------------------------+
| TEAMS             | Acme / Delivery / Team board                               |
| > Delivery        | Your role: Member              [Refresh] [+ New task]       |
|   Operations      | Filter [All assignees v]  Last loaded 09:41                 |
|                   +------------------+-------------------+---------------------+
| ADMINISTRATION*   | TO DO (2)        | IN PROGRESS (1)   | DONE (1)            |
| People & groups   |                  |                   |                     |
| Team access*      | K-12 Fix login   | K-09 Add API      | K-04 Setup board    |
| Audit*            | Assigned: Alice  | Assigned: Bob     | Assigned: Alice     |
| AI connections    |                  |                   |                     |
|                   | K-15 Write docs  |                   |                     |
|                   | Unassigned       |                   |                     |
+-------------------+------------------+-------------------+---------------------+
```

`*` Navigation appears only when the relevant administrative permission exists.

Open a task in a detail drawer; retain company/team context. Provide a keyboard-accessible "Move to" action as an alternative to dragging. Viewers receive read-only controls. Show "No tasks yet" only after an authorized successful empty response, never after an access or network error.

Switching company clears previous company data and pending requests. Saving shows success only after server confirmation. On conflict, preserve the employee's draft and offer loading the latest version.

### Team access: explain effective permissions

```text
+------------------------------------------------------------------------------+
| Acme / Delivery / Access                                     [+ Add grant]    |
+-------------------------+----------------------+-----------------------------+
| Principal               | Type                 | Team role                   |
+-------------------------+----------------------+-----------------------------+
| Delivery staff          | Company group        | Member                      |
| Auditors                | Company group        | Viewer                      |
| alex@acme.example        | Company member       | Team manager                |
+-------------------------+----------------------+-----------------------------+
| Check effective access [alice@acme.example________________] [Check]           |
| Result: Member, through group "Delivery staff"                                |
| Company administrator status does not itself grant task access.              |
+------------------------------------------------------------------------------+
```

### Employee authorization of an approved MCP connection

```text
+--------------------------------------------------------------------+
| Authorize task access: Claude Code                                  |
| Company: Acme                                                       |
| Allowed team: [Delivery v]                                          |
| [x] Read tasks   [x] Create, edit, and move tasks                     |
|                                                                    |
| Cannot manage people, groups, or permissions.                       |
| Access never exceeds your current team role.                        |
| Task content may leave Sweden through your AI provider.             |
|                                                                    |
| [Cancel]                                             [Authorize]   |
+--------------------------------------------------------------------+
```

This is application-side connection approval, not a replacement for the identity provider's OAuth flow or required provider-admin consent.

## 9. Success criteria and performance targets

These are proposed acceptance criteria, not measured results.

| Area | PoC passes when |
| --- | --- |
| Product journey | An administrator onboards employees, grants a group access, and an employee creates/moves a task through the UI |
| Tenant isolation | Two companies with overlapping user identities cannot read or mutate each other's unauthorized data by changing URLs, IDs, payloads, filters, or MCP arguments |
| Team isolation | A company member without a team grant cannot read the team's tasks; Viewer cannot mutate through any interface |
| Revocation | Deactivating company membership or removing the last effective team/connection grant denies the next relevant request |
| Integration | VS Code, GitHub Copilot App, and Claude Code each complete employee sign-in, permitted task operations, renewal, and revoked-access handling |
| Concurrency | Simultaneous task edits cause an explicit version conflict, not silent lost updates |
| Audit | Mutations and access changes identify the correct company, employee, client, and action without recording task bodies |
| Recovery | A database backup can be restored into an isolated recovery database without overwriting the live shared database |
| Responsiveness | With 10 concurrently active users, target p95 ordinary backend reads/writes under 500 ms and board load under 2 seconds from a nearby European client |

Use a stated dataset for the latency target: up to 10,000 tasks total, a bounded first board page of at most 100 tasks, and indexed company/team queries. Exclude interactive identity-provider sign-in, AI inference, and deliberate throttling from the backend target. Measure browser load separately, including network time. Warm hosting does not guarantee zero latency during deployments or platform restarts.

## 10. Running-cost estimate

Public PAYG USD rates retrieved on 2026-10-23, excluding tax and customer-specific agreements:

| Component | Monthly planning amount |
| --- | --- |
| App Service Linux B1, one instance | About $13.14 at $0.018/hour and 730 hours |
| Azure SQL Database S0 | About $14.72 at $0.4839/day and 730/24 days |
| Entra External ID core | Within the published first-50,000-MAU free tier at this PoC size; add-ons excluded |
| Key Vault, monitoring, transfer | Additional usage-based costs; allowance rather than a verified quote |

Verified application/database floor: approximately **$28/month**. Use **$40-70/month as a preliminary whole-environment planning allowance**, and calculate network/monitoring and chosen authentication extras before provisioning. This is not a price guarantee.

Exclude AI-client subscriptions, support plans, taxes, custom domains, paid identity add-ons, additional environments, and future security services from that allowance. Avoid verbose content logging; configure Azure budgets/alerts, remembering that alerts are not a hard spending cap.

The selected public-endpoint, single-database design does not include SQL private-endpoint/DNS/VPN costs or a separate development database. The preliminary whole-environment allowance has not been recalculated into a new quote.

Always On plus provisioned SQL deliberately trades a small idle floor for consistent responsiveness. Do not assume serverless auto-pause is the cheapest option while also requiring a warm application and database.

## 11. Explicit real-data gates

Before admitting real confidential or regulated data, the relevant company must approve:

- Actual data classification, applicable laws/contracts, controller/processor responsibilities, and whether shared-database isolation is acceptable.
- Residency and transfer requirements covering identity, backups, diagnostics, and AI clients, not only the app/database region.
- Required authentication strength, onboarding/offboarding procedures, retention, deletion/export, recovery objectives, support access, and incident response.
- Approved MCP clients and AI-provider terms, destinations, retention/training behavior, and required agreements.
- Any stronger controls such as phishing-resistant MFA/SSO, immutable audit storage, a WAF, stronger availability, or dedicated data isolation.

Until then, the PoC is a synthetic-data demonstration, not a generally compliant container for arbitrary sensitive company information.

## Sources

- [Azure App Service Linux pricing](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/) and [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices): Sweden Central B1 Linux Consumption rate, and SQL Database Single Standard S0 Consumption rate.
- [Azure SQL firewall rules](https://learn.microsoft.com/en-us/azure/azure-sql/database/firewall-configure) and [App Service outbound IP addresses](https://learn.microsoft.com/en-us/azure/app-service/overview-outbound-ip-addresses).
- [Entra External ID identity providers](https://learn.microsoft.com/en-us/entra/external-id/customers/concept-authentication-methods-customers), [MFA combinations and limitations](https://learn.microsoft.com/en-us/entra/external-id/customers/concept-multifactor-authentication-customers), and [pricing](https://azure.microsoft.com/en-us/pricing/details/microsoft-entra-external-id/).
- [MCP authorization specification, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).
- [VS Code MCP configuration and OAuth](https://code.visualstudio.com/docs/agents/reference/mcp-configuration).
- [Claude Code MCP authentication and preconfigured clients](https://code.claude.com/docs/en/mcp#use-pre-configured-oauth-credentials).
- [GitHub Copilot CLI MCP configuration](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers). This is CLI evidence, not a substitute for verifying the GitHub Copilot App.
