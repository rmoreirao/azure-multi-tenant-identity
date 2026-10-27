# B2B Kanban SaaS PoC: technical implementation specification

Status: product source implemented and local automated safety checks passing; deployment and authenticated product acceptance are tracked separately in section 13.4.
Date: 2026-10-27.
Product scope: [PRD](kanban-saas-prd.md).
Product extension: [self-service company onboarding PRD](kanban-saas-self-service-onboarding-prd.md); implemented behind an operator rollout switch.
System structure and contracts: [architecture and technical design](kanban-saas-technical-design.md).
Configuration template: [environment example](.env.example).
Package feed: [project npm configuration](.npmrc).

## 1. Purpose and authority

The approved full-product implementation accepts the current identity configuration as the synthetic-PoC baseline rather than reopening provider/MFA setup. Company creation has no count quota; invitations last seven days; existing assignments become inactive instead of being cleared on access loss; company administrators may explicitly repair managerless teams without acquiring content access. Implementation and acceptance status must still distinguish source code, deployed artifacts, and observed product behavior.

This document specifies the languages, frameworks, project organization, configuration, database implementation, developer workflow, and delivery conventions for the PoC. It complements the architecture rather than repeating its permission matrix, route inventory, or business model.

User-selected decisions below supersede earlier proposed implementation alternatives. Engineering defaults fill in the supporting details; they are recommendations, not additional product requirements. Neither a selected technology nor an existing identity tenant proves the OAuth/MCP client compatibility, tenant-isolation, deployment, or performance gates.

Full-product implementation, testing, and deployment were subsequently approved. Section 13.4 is the current checkpoint; sections 13.1-13.3 retain historical identity-candidate evidence and are not renewed identity-policy prerequisites.

## 2. Decisions from the technical interview

| Area | User-selected decision | Consequence / alternative not selected |
| --- | --- | --- |
| Backend | C# / ASP.NET Core | Not Node.js/TypeScript or Python/FastAPI |
| HTTP endpoints | Minimal APIs in feature-specific route groups | Not controller classes or a large endpoint-filled Program.cs |
| Browser | React + TypeScript, served by ASP.NET | Not Razor Pages, Blazor, or a separate frontend hosting service |
| Components | Fluent UI React | Not Tailwind/shadcn or Material UI |
| Persistence | EF Core plus narrowly scoped explicit SQL | Not Dapper or hand-written SQL for every query |
| Developer database | The same Azure SQL database used by the deployed PoC | No local SQL container, LocalDB, or second development database |
| SQL network | Public endpoint | Use IP allowlists, TLS, and Entra authentication; no private endpoint/VPN baseline |
| Environment count | One deployed synthetic-data PoC | No continuously deployed staging/CI database |
| CI | No GitHub Actions | Checks are developer-invoked; no automatic merge/build safety net |
| Infrastructure | Bicep, run manually from the workstation | Not Terraform or portal-only application infrastructure |
| Customer identity | Existing External ID tenant named `msrmoreiraoexternalid` | Prerequisite, not an identity tenant created by application Bicep |
| npm registry | `https://packagefeedproxy.microsoft.io/npm/` | Project-local setting; no change to global npm configuration |
| Validation budget | One focused .NET safety project plus deployed browser/REST/MCP checks | No broad component/browser suite, coverage threshold, or CI |
| MCP clients | VS Code, GitHub Copilot App, and Claude Code all remain required | User-assisted provider sign-in; no silently deferred client |
| Reactivation | Membership only; fresh access and integration grants | No automatic restoration of team/group grants, administrator status, or MCP connections |
| Deferred exercises | Backup-restore proof and formal full-dataset performance benchmark | No temporary recovery database; recovery/latency targets remain unverified |

Public SQL means a publicly routable service endpoint, not anonymous access or an unrestricted firewall. Sharing one database means development work can affect the deployed PoC; the safeguards in sections 7 and 10 are mandatory to preserve the architecture's intent.

## 3. Language, runtime, and dependency baseline

### 3.1 Runtime choices

| Technology | Engineering baseline | Version / verification policy |
| --- | --- | --- |
| .NET SDK | .NET 10 LTS; reference SDK `10.0.402` | Pin in global.json at scaffolding; allow supported patch servicing, not previews or a new feature band silently |
| Runtime / ASP.NET Core | `net10.0`; reference runtime `10.0.13` | Keep supported patches current; verify App Service Linux has the selected runtime before deployment |
| C# | C# 14, the default for .NET 10 | No `preview` or `latest` language-version override |
| EF Core | Stable 10.0.x SQL Server provider and tooling | Pin compatible published package patches; do not infer EF package numbers from the runtime patch |
| Node.js | Node 24 LTS; reference `24.22.1` | Workstation/build tooling only, not a production Node server |
| npm | Reference `11.19.1`, bundled with that Node release | One root workspace lockfile; project registry configuration |
| React | Feed reference version `19.3.1` | Pin matching react/react-dom versions; prove the full component/toolchain combination |
| TypeScript | Feed reference version `7.0.2`, project-local | Verify compatibility with the Vite/lint toolchain; never use a global compiler implicitly |
| Fluent UI | `@fluentui/react-components` reference `9.74.11` | Published React peer range is `>=16.14.0 <20.0.0`; React 19 satisfies metadata, not a complete UI compatibility test |
| Vite | Feed reference version `8.3.4` | Published Node range is `^20.19.0 || >=22.12.0`; pin the compatible React plugin during scaffolding |

The .NET support policy lists .NET 10 LTS through 2028-11-14. Reference versions are a dated starting point, not permission to stop applying supported security patches. The initial SDK/runtime versions came from official release metadata; package availability is not a complete application compatibility result.

The scaffold now restores through the selected feeds and compiles with these pinned versions. Node 24.22.1 is checksum-verified and project-local; global Node settings are unchanged. Frontend compilation is not evidence of provider/client interoperability or a completed application.

### 3.2 Supporting packages

These are proposed engineering defaults within the selected stack:

| Responsibility | Package / mechanism | Rule |
| --- | --- | --- |
| HTTP API descriptions | `Microsoft.AspNetCore.OpenApi` | Match the .NET 10 family; generate OpenAPI from endpoint contracts |
| Authentication | `Microsoft.Identity.Web`; reference published version `4.16.1` | Separate browser OIDC/cookie and delegated bearer schemes; verify External ID behavior in G1 |
| MCP over HTTP | `ModelContextProtocol.AspNetCore` `2.2.0` | Available official stable SDK on the approved NuGet feed; the earlier `2.3.0` reference was unavailable there. Client compatibility remains unproven |
| SQL persistence | `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design` | Design package/tooling does not grant runtime migration privileges |
| SQL driver | Provider-compatible `Microsoft.Data.SqlClient` | Record resolved version in the NuGet lockfile; do not replace it with an untested major |
| Azure credentials | `Azure.Identity` | Constrained local developer credentials; managed identity in App Service |
| Cookie key persistence | `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` and `Azure.Extensions.AspNetCore.DataProtection.Keys` | Shared SQL stores protected key material; Key Vault protects it; no new storage service by default |
| React server state | `@tanstack/react-query` | Company/team-aware keys and explicit cache clearing; no persistent task cache |
| React routes | `react-router` | Explicit company/team URL context; no route parameter is authorization evidence |
| Typed REST calls | `openapi-typescript` and `openapi-fetch` | Generate types from OpenAPI; keep a small transport/error wrapper |
| Drag/drop | Maintained compatible dnd-kit React package | Verify peer/API compatibility before selection; retain a Fluent UI Move to command |
| .NET tests | xUnit, `Microsoft.NET.Test.Sdk`, ASP.NET test hosting where needed | One `Kanban.SafetyTests` project; six focused safety groups, not a broad unit-test architecture |
| Frontend static checks | `@biomejs/biome` `2.5.15` and TypeScript | Available typescript-eslint peers excluded TypeScript 7; do not force unsupported peers |
| Browser validation | Focused manual/agent-driven deployed checks | No persistent Vitest/React Testing Library/Playwright suite; real provider sign-in remains required |
| Diagnostics | `Microsoft.Extensions.Logging` and `Azure.Monitor.OpenTelemetry.AspNetCore` | Allowlisted metadata; do not also enable a duplicate auto-instrumentation pipeline |

The supporting npm dependency graph and peer versions must be resolved through the supplied feed before scaffolding is accepted. Do not use `--force`, `--legacy-peer-deps`, a different registry, a preview package, or disabled TLS verification to conceal incompatibility.

### 3.3 Reproducibility

Keep global.json, Directory.Build.props, Directory.Packages.props, NuGet lockfiles, the root dotnet-tools.json manifest, the private npm workspace manifest, and one root package-lock.json together in version control when commits are requested. Use exact direct package versions and locked restores once the first graph is approved.

EF Core packages/tooling are pinned to 10.0.13. System.Security.Cryptography.Pkcs is explicitly pinned to 10.0.13 to avoid the vulnerable transitive 10.0.10 resolution. The manifests and lockfiles, rather than this summary, are the exact dependency authority.

Run npm commands from the repository root against the client workspace so the root .npmrc is applied. Do not create an independent client lockfile or silently resolve through a user/global registry. Authentication, if the feed requires it, belongs in the developer's approved credential mechanism, not the repository .npmrc.

Dependency updates are explicit, reviewed changes. Record license and support implications before adding a package. Do not add MediatR, AutoMapper, a generic repository framework, Redux, Next.js, or a second backend service without a concrete problem that the existing stack cannot solve simply.

## 4. Repository layout

Business services, adapters, migrations, generated contracts, browser features, and safety tests are implemented. Keep the small operation files together rather than introducing an empty folder for each domain:

```text
\
  kanban-saas-prd.md
  kanban-saas-technical-design.md
  kanban-saas-implementation-spec.md
  .env                         ignored local server configuration
  .env.example                 tracked, non-secret template
  .npmrc                       project package feed
  global.json
  Directory.Build.props
  Directory.Packages.props
  Kanban.slnx
  package.json                 private npm workspace root
  package-lock.json
  src\
    Kanban.Web\                ASP.NET composition root and adapters
      Features\
        Session\
        Directory\
        Mcp\
      wwwroot\                 generated React production assets
    Kanban.Core\               domain rules and application operations
      Model.cs
      Contracts.cs
      KanbanService.cs
      AccessOperations.cs
      WorkOperations.cs
      ConnectionOperations.cs
    Kanban.Infrastructure\     EF mappings, SQL operations, Azure integrations
      Persistence\
      Migrations\
      Security\
      Operations\
      Telemetry\
  client\                      one React/Vite npm workspace
    src\
      app\
      generated\
  tests\
    Kanban.SafetyTests\
  infra\
    main.bicep
  scripts\
    dev.ps1
    verify.ps1
    generate-contracts.ps1
    inspect-environment.ps1
    deploy.ps1
  docs\
    contracts\                 generated OpenAPI and MCP schemas
    architecture\
```

Dependency direction is Web -> Core, Web -> Infrastructure for composition, and Infrastructure -> Core. Core does not depend on ASP.NET, EF, the MCP SDK, or React. Keep one deployable host; these projects are not separate microservices.

Features own their endpoint mappings and thin transport DTOs. Application operations own business orchestration. Infrastructure owns SQL/EF mechanics. Introduce interfaces only where Core needs an infrastructure boundary or a real test seam, not for every class.

## 5. Existing identity tenant and local configuration

### 5.1 Tenant prerequisite

The user reports that the External ID tenant `msrmoreiraoexternalid` has been created. Treat that name as supplied configuration, not proof of its immutable tenant ID, verified domain, geography, policies, or application registrations.

No application deployment creates, replaces, or deletes this tenant. Do not include `Microsoft.AzureActiveDirectory/ciamDirectories` provisioning in the application Bicep. The previously considered tenant-creation preview API is not needed for this baseline.

Before G1, an authorized tenant administrator must supply/verify:

| Value | Why it is needed |
| --- | --- |
| Immutable External ID tenant GUID | Token issuer and internal identity mapping |
| Actual OIDC authority/discovery metadata | Configure the supported CIAM issuer, not a URL guessed from a display name |
| Confidential browser web-client registration | Redirect URIs, sign-in/sign-out, and supported credential |
| Protected API/resource registration and actual audience | Delegated task access, scopes, and token validation |
| Supported public client registrations | Exact client callbacks/discovery/resource behavior for the three required clients |
| Sign-in user flow, MFA, verification evidence, required admin consent | Managed authentication and safe invitation acceptance |
| Tenant geography and relevant policies | Record real deployment facts; do not infer them from Sweden Central hosting |

App companies remain application records in SQL, not separate Entra tenants. The Azure hosting subscription/SQL administrator directory can differ from this External ID customer directory. Customer tokens are never reused as SQL credentials.

### 5.2 Files created with this specification

The ignored root .env contains the verified non-secret directory, registration, database, and Azure settings. The tracked .env.example remains generic. The .gitignore excludes .env and local variants while allowing the example.

| Local key | Current value / meaning |
| --- | --- |
| `ExternalId__TenantName` | `msrmoreiraoexternalid`, user-provided |
| `ExternalId__TenantId` | `0f6188cc-b440-43f2-87a5-35d9811f8c7c` |
| `ExternalId__Authority` | Verified friendly `ciamlogin.com` authority for this directory |
| `ExternalId__Issuer` | Canonical GUID-host issuer returned by provider discovery, not the friendly authority host |
| `ExternalId__WebClientId` | Actual confidential browser registration; see ignored registration evidence |
| `ExternalId__ApiClientId` | `242a2b31-70b7-4591-8131-dca9eb53e6d0` |
| `ExternalId__ApiAudience` | The API client GUID, matching its v2 access tokens |
| `ExternalId__PublicClientIds` | Comma-separated allowlist of the three actual secret-free public registrations; no browser/API IDs, duplicates, or empty GUIDs |

Double underscores follow ASP.NET configuration's environment-variable hierarchy. ASP.NET Core does **not** automatically read .env. `Import-LocalConfiguration` in `scripts\Common.ps1`, used by the local scripts, imports only approved server keys as literal values without evaluating the file or printing credentials. Blank required settings produce a clear startup/preflight error, not an anonymous or disabled-auth fallback.

The public MCP client IDs remain environment-configured, not hardcoded in application code. `ExternalId__PublicClientIds` is the existing server allowlist in both local `.env` and Azure App Service application settings; managed registration setup populates it, and deployment passes it to Bicep. It already includes the Copilot App integration's public application ID, `4084a6c1-63e9-4d2d-b0b6-c302086f50a7`, alongside the VS Code and Claude Code IDs. Retain this single allowlist rather than introduce a duplicate setting. Each integration's client ID is shared across its users; individual identity still comes from the authenticated tenant/object-ID claims.

Use ASP.NET User Secrets or the approved local credential store for a confidential web-client credential. App Service uses Key Vault-backed configuration. Do not put real secrets in tracked parameter files, example files, frontend bundles, command output, or diagnostic evidence.

### 5.3 Browser configuration boundary

The browser calls relative same-origin paths. It needs neither an identity client secret nor an access/refresh token. Only explicitly public settings may have a `VITE_` prefix.

Keep the server .env outside Vite's environment directory. Never spread process.env, server configuration, or all .env values into Vite's define/import.meta environment. A supplied tenant name is not a secret, but it still belongs to server identity configuration in this BFF design.

## 6. Backend implementation

### 6.1 Host and endpoint organization

Program.cs composes configuration, logging, authentication, authorization, antiforgery, rate limiting, EF/persistence, application operations, static assets, and feature endpoint groups. It does not contain task or grant business rules.

Use explicit feature mapping methods for the architecture's `/api/v1` routes. Attach transport authentication/validation/rate policies at the group and operation boundaries, then call the same Core operations used by MCP. Authorization for a particular company/team/resource remains inside the shared application operation.

Use System.Text.Json with documented camelCase DTOs and string status values. Reject unknown mutation fields. Validate simple field bounds at transport entry and cross-resource/permission rules in Core. Map known domain failures consistently to REST Problem Details and MCP errors; unexpected errors produce sanitized diagnostics and an explicit failure.

Do not add automatic retries around a whole request without knowing whether it committed. Never translate a database/authorization exception into an empty successful response.

### 6.2 Browser session endpoints

Implement a small BFF surface in addition to the product API:

| Path | Contract |
| --- | --- |
| `GET /bff/login` | Start maintained-library OIDC flow; validate any return path as a local allowed route |
| `GET /bff/session` | Authenticated session summary and antiforgery bootstrap; `Cache-Control: no-store` |
| `POST /bff/logout` | CSRF-protected local/provider sign-out using supported library behavior |
| `/signin-oidc` | Library-managed callback; exact local/hosted redirect URIs registered in advance |

Cookies are Secure and HttpOnly with appropriate SameSite policy. Do not expose bearer/refresh tokens in the session response. Browser mutations include the agreed antiforgery header. SameSite is not the only CSRF control.

Use explicit scheme handling. A request with an invalid bearer token must not fall back to a valid browser cookie. Delegated task routes and session-only administrative routes must remain distinguishable. API authorization failures return JSON/status codes, not a sign-in HTML page.

### 6.3 Shared application operations

Create request-scoped caller and authorized-company objects after authentication/membership resolution. Do not inject a mutable singleton current company or accept caller identity from JSON. Use explicit company/team identifiers at every operation boundary.

REST and MCP adapters map to operations such as ListPermittedTeams, ListTasks, CreateTask, UpdateTask, MoveTask, ChangeTeamGrant, and DeactivateMembership. Adapters never call each other's HTTP endpoints or duplicate effective-role calculations.

Use cancellation tokens and async I/O throughout. A cancellation during SQL work must roll back uncommitted operations and trigger the same connection cleanup as an ordinary failure. Do not run concurrent commands on a scoped DbContext.

### 6.4 Generated product contracts

`.\scripts\generate-contracts.ps1` exports OpenAPI from an isolated ASP.NET host, obtains actual tool metadata through the official MCP client SDK, and generates TypeScript with the already-pinned `openapi-typescript`. It writes `docs\contracts\openapi.json`, `docs\contracts\mcp-tools.json`, and `client\src\generated\api.d.ts`. `-Check` regenerates into ignored scratch output and rejects missing or stale snapshots without overwriting them. Line-ending differences are ignored; schema or declaration drift is not. Generated declarations are excluded from Biome formatting but their browser consumers remain type-checked.

OpenAPI includes the existing authentication/session routes and the implemented `/api/v1` company, invitation, directory, team, group, task, connection, and audit operations. It contains no environment-specific server URL. Browser requests use generated schemas, explicit errors, conditional versions, and session-bound CSRF proof.

The MCP snapshot contains `identity_probe` plus `list_teams`, `list_tasks`, `get_task`, `create_task`, `update_task`, and `move_task`, with official-SDK input/output schemas, annotations, and structured results. The probe reports `kanban-product` and implemented task authorization. Test-host signing keys remain isolated; protocol tests do not establish native-client or real-provider acceptance.

The immutable ZIP includes both JSON snapshots under `wwwroot\contracts`; published releases expose them at `/contracts/openapi.json` and `/contracts/mcp-tools.json`. Hosted validation checks exact packaged hashes and compares live OpenAPI structurally with the snapshot used for TypeScript. Published schemas establish the contract, not authenticated functional acceptance.

## 7. EF Core, SQL, and migrations

### 7.1 Mapping conventions

Configure mappings explicitly in Infrastructure. Preserve the architecture's company-aware composite keys, ownership foreign keys, grant uniqueness, rowversions, and tenant-first indexes.

Use no-tracking DTO projections for ordinary reads and bounded queries. Global query filters may help prevent mistakes but are not the security boundary; ownership checks, relational constraints, and RLS remain required.

Do not use EF InMemory or SQLite as evidence for SQL isolation, locking, rowversion, or RLS behavior. Do not enable DbContext pooling initially. SQL connection pooling is a separate mechanism and still requires context initialization/reset.

### 7.2 Authorized operation lifecycle

The tenant-operation boundary must:

1. Authenticate the caller and resolve membership through the restricted bootstrap path.
2. Open the EF-owned SqlConnection and set the verified company context before tenant queries.
3. For writes, begin a transaction and acquire the company coordination lock.
4. Re-check current membership, role, applicable connection grant, ownership, and assignment invariants inside that transaction.
5. Execute conditional mutations and append audit/idempotency records using that same connection and transaction.
6. Commit once; return only a confirmed result.
7. Clear SQL context and dispose the operation on every path. If reset cannot be trusted, discard the connection using a verified driver mechanism rather than return it to the pool.

Explicit SQL is limited to reviewed mechanisms EF does not express clearly: RLS policies/predicates, directory bootstrap modules, session context, coordination locking, and necessary conditional database operations. Parameterize values; no raw client-supplied SQL fragments.

Use one tested retry boundary for a complete retry-safe operation. Do not combine EF execution-strategy retries, manual transactions, pooled tenant context, and side effects without proving their interaction in G3.

### 7.3 Conditional writes and audit

Expose rowversion as an opaque encoded token and strong HTTP ETag. Conditional update predicates include company/team/object ownership and expected version. No stale-write retry with a newly fetched version.

Task creation uses the architecture's scoped idempotency record in the same transaction. A replay reauthorizes and returns only its original minimal create result. Task/access mutation and audit commit atomically.

Implement the architecture's proposed assignment-loss/reauthorization policies only after G3 resolves them. Do not silently restore old team access when reactivating a company membership.

### 7.4 Shared database safety

One Azure SQL database serves local development and the hosted PoC. Localhost is not a separate data environment. Workstation startup does not create/reset/seed the whole database.

Never call EnsureDeleted, drop/recreate the database, disable production RLS policies, or run blanket cleanup in developer/test startup. Synthetic fixture operations are explicitly bounded by known company/object IDs and a unique run identifier.

Runtime identity has no schema-modification or RLS-management privileges. The local integration-test connection must exercise equivalent restricted permissions, not quietly connect as SQL administrator. An operator identity may set up fixtures/migrations, but that is a separate, explicitly invoked phase.

### 7.5 Migration process

Use EF migrations with reviewed SQL for security objects. A migration identity with the required DDL permissions runs from the workstation over the approved public endpoint. Azure Contributor access alone is not proof of SQL DDL permission.

Before applying changes, verify subscription/server/database identity, inspect the generated SQL, review data-loss warnings, verify the backup/recovery plan, and reserve a maintenance window when compatibility requires one. Apply one migration operation at a time.

Use expand/contract changes where practical. App startup checks schema compatibility and fails readiness if incompatible; it does not run automatic migrations. An application rollback does not trigger destructive down-migrations.

## 8. React and Fluent UI implementation

### 8.1 Hosting and build

Use one private npm workspace for client. Vite builds static assets that are copied into Kanban.Web's publish output. Node/npm exist on the workstation, not as a second production process.

ASP.NET serves the SPA and APIs on one HTTPS origin. Cache content-hashed assets; avoid stale caching for index.html, sessions, or protected responses. SPA fallback must exclude `/api`, `/mcp`, `/bff`, discovery paths, and health endpoints; an unknown API route must not return a successful index.html.

The candidate serves public static files before explicit endpoint routing, so reserved-route fallbacks do not hide packaged contract files. Static serving and SPA fallback share cache options: built `/assets` files use a one-year immutable policy, the release marker uses `no-store`, and HTML/contracts require revalidation with `no-cache`. Unknown `/contracts` paths return 404 rather than the SPA. Hosted checks cover the root, an actual fallback route, an asset, and the published snapshots.

The simplest initial developer loop also uses ASP.NET HTTPS as the browser origin: Vite build/watch updates assets and dotnet watch runs the host. This deliberately avoids a second browser origin and cookie/proxy complexity. A later HMR proxy must preserve the same-origin security model rather than adding permissive CORS.

### 8.2 UI structure and server state

Use function components, strict TypeScript, FluentProvider, Fluent UI tokens, and shared accessible form/dialog primitives. No externally loaded component scripts/fonts or paid kanban control is required.

Keep company/team in route/application context and in every query key. TanStack Query owns server data; component state owns local drafts and transient interaction state. Do not add Redux or persist task responses in localStorage.

On company switch, cancel old requests, clear old company queries, and reject late results using their original company/team/selection generation. On sign-out or session identity change, clear the query client. Cached roles are display hints, never permission evidence.

Use generated API types and one small fetch wrapper for credentials, CSRF headers, correlation IDs, and Problem Details. TypeScript types are not runtime proof of valid data; validate external responses where the application depends on their shape and surface malformed responses explicitly.

### 8.3 Mutations and access UX

The first implementation favors confirmed server state over optimistic changes. Disable duplicate submits, show pending state, then apply the returned task/version after success. Preserve drafts on validation or version conflict.

Drag/drop calls the same MoveTask operation as the keyboard-accessible Move to menu. Do not implement arbitrary status strings, cross-team movement, within-column ranking, or client-side grant editing rules outside the documented scope.

Viewers have read-only controls. Distinguish empty, loading, denied, throttled, conflict, and dependency-error states. Manual Refresh is explicit; do not silently introduce polling or realtime infrastructure.

## 9. Identity and MCP implementation

Use Microsoft.Identity.Web and maintained ASP.NET handlers for the confidential browser client and API token validation. Provider-managed sign-in and MFA remain external. Scope/issuer/client-claim mappings must be verified with the actual External ID registration.

Use the official ModelContextProtocol.AspNetCore package for stateless Streamable HTTP. Tool handlers are thin typed adapters over Core operations, with bounded inputs and explicit cancellation. No MCP Apps/long-running-task extension is needed for this PoC.

Company approval and employee grant lookup use the validated registration identity, company, and current membership. Apply the same grant restrictions to REST calls made with a grant-bound registration. There is no request argument that can declare a caller exempt from connection approval.

The SDK's existence and a valid browser sign-in do not demonstrate compatibility with VS Code, GitHub Copilot App, or Claude Code. Functional acceptance must observe actual task reads/writes, renewal where observable, and next-request revocation in all three clients. This does not block implementing UI/administration on the accepted identity baseline. No shared API key, custom OAuth server, or desktop shared secret is an acceptable fallback.

Persist browser protection keys across restarts using the same SQL database and Key Vault protection, with explicitly granted runtime permissions. Key persistence objects are restricted infrastructure state, not an RLS/team bypass. Verify the exact package/credential/key-rotation arrangement during G2; do not rely on ephemeral local keys in App Service.

## 10. Developer checks without GitHub Actions

No workflow file, GitHub-hosted runner, alternative CI service, or remote execution pipeline is part of this baseline. Checks can still be automated as local commands invoked deliberately by the developer.

| Check | Tool / scope | Shared SQL behavior |
| --- | --- | --- |
| Compile and static checks | dotnet build, compiler analyzers, TypeScript, Biome | No database mutation |
| Focused safety gate | One xUnit project with a handful of cohesive scenarios | SQL cases explicitly opt in; restricted runtime-equivalent access and recorded run-owned fixtures |
| Deployed browser/REST checks | Focused manual/agent-driven scenarios | Explicit target URL/fixture IDs; no database reset or provider-credential storage in source |
| Identity/client acceptance | Exact supported real clients and provider | Interactive approved test identities; sanitized results, no token dumps |
| Performance/recovery evidence | Architecture V11 and formal V12 deferred by the user | Not run or claimed passed; no temporary recovery database in this delivery |

The six focused safety groups are isolation/RLS and pooled/cancelled/concurrent context lifecycle; direct/group roles and Viewer/admin-without-team-grant denials; revocation/reactivation and grant-bound REST bypass; authentication-scheme separation and CSRF; idempotency and atomic audit rollback; and missing/stale versions with competing writes. No coverage target or broad React/component/browser test architecture is required.

`.\scripts\verify.ps1 -Sql -SqlTarget 'tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433'` runs builds/static checks, every local safety test, and contract drift verification. Missing required groups, failures, or skips reject the gate. SQL fixtures require the exact target and record their run-owned IDs and cleanup. Local service checks use the operator credential; separate `EXECUTE AS USER` probes establish restricted runtime permissions/RLS. This is explicitly not a genuine hosted-runtime business journey. `-CandidateOnly` runs database-free identity/contract checks without full safety acceptance. Test-host signing keys and protection remain isolated; production authentication is never bypassed.

Fixture cleanup removes only recorded run-owned objects in dependency order. If cleanup fails, report the remaining identifiers; do not broaden deletion to make the suite appear successful. Do not disable constraints or RLS to hide test failures.

Any test-only authentication handler lives in test-host code, not an environment-toggle production authentication bypass. Such tests validate application behavior only. Real provider/client compatibility and the deployed security boundary require their own evidence.

The functional/security acceptance requirements remain. The user explicitly deferred V11 restore proof and the formal V12 benchmark. The first page remains bounded to at most 100 tasks, but ten-active-user/10,000-task latency and recovery targets must be reported as unverified. Future measurements must include complete authorization/audit work and ordinary load, not infer results from framework choice or throttle the workload away.

## 11. Infrastructure and manual releases

### 11.1 Bicep scope

Declare the App Service Linux B1 plan/app with Always On, system-assigned managed identity, one SQL logical server and S0 database, narrow SQL firewall rules, Key Vault, and monitoring. Keep the existing identity tenant out of provisioning. Do not create a VNet, SQL private endpoint, private DNS zone, VPN, container registry, second application, second normal-operation database, or CI agent.

Use explicit supported resource API versions and parameterized names/region. No secrets in .bicepparam files or deployment outputs. Managed identity/RBAC assignments and SQL users are separate concerns and both need permission verification.

### 11.2 Public SQL connectivity

Use the fully qualified SQL endpoint, encrypted connections, and certificate validation (`Encrypt=True`, `TrustServerCertificate=False`). Use Entra authentication: restricted developer identity locally and the app's managed identity when hosted. Do not use the External ID customer access token for SQL.

Allow only approved workstation egress IPs and the required App Service outbound IPs. Do not use `0.0.0.0` as an "Allow Azure services" shortcut or a full-internet IP range. Document how changes to workstation networks or the App Service plan change the allowlist.

Failure to reach SQL is an explicit operational error. Do not temporarily open the firewall to the world, switch to certificate-trust bypass, embed an admin SQL password, or silently change the target database.

The selected subscription inherits `MCAPSGovDeployPolicies` / `AzureSQL_PublicNetwork_Modify`, which normally rewrites public SQL access to `Disabled`. On 2026-10-23 the user explicitly requested the resource-group exception tag `SecurityControl=Ignore` on `rg-kanban-poc-swc`. The policy recognizes that resource-group tag; it was merged without replacing other tags, and the original `Enabled` plus narrow-firewall deployment succeeded. The server's final state and an encrypted, certificate-validated, Entra-authenticated read-only SQL connection were verified.

This is a user-directed governance-control exception for the synthetic PoC, not a general production recommendation. Keep it scoped to the approved resource group; do not change management-group policy assignments, tag unrelated groups, remove SQL authentication/TLS/firewall controls, or treat it as real-data approval. The temporary perimeter alternative was removed; no private endpoint or perimeter is required by the retained design. Historical policy evidence is in ignored `artifacts\sql-network-policy.json`; current control-plane and read-only connectivity evidence is in `artifacts\sql-public-network-validation.json` and `artifacts\sql-connectivity-validation.json`.

### 11.3 Manual release contract

The current deploy.ps1 reconciles infrastructure only, with preview by default and `-ApplyInfrastructure` for scoped resource changes. Apply mode merges the explicitly requested `SecurityControl=Ignore` tag only into the approved resource group. It verifies actual SQL network state before attempting narrow firewall rules, checks the exact individual-IP allowlist and final server state, and fails closed when those differ from the approved configuration. Preview mode does not apply the tag. `artifacts\resource-outputs.json` records resource identifiers only; it is not a connectivity or application-release claim. The completed release orchestrator must:

1. Validate tool versions, the configured feed, required non-secret parameters, active hosting subscription, and Azure/SQL privileges.
2. Build/type-check/test the candidate locally and create one immutable publish artifact containing backend and React assets.
3. Preview/review Bicep changes and apply only the requested application infrastructure changes.
4. Apply the separately reviewed schema migration with the migration identity.
5. Deploy the already-built artifact, then check health, sign-in, one authorized task flow, and denial behavior.
6. Record artifact identity, dependency versions, migration version, and safe release evidence.

Do not rebuild a different artifact between verification and deployment. Preserve the previous compatible artifact for rollback. B1 is not assumed to provide a zero-downtime deployment slot; disclose and schedule interruptions.

The bounded candidate now uses local run-from-package (`WEBSITE_RUN_FROM_PACKAGE=1`) with remote builds disabled. On this Linux host, `az webapp deploy` used OneDeploy and still extracted files despite that setting; setting the flag alone was not proof of atomic activation. `release-candidate.ps1` therefore uses the supported Entra-authenticated ZIP deploy API explicitly, polls its scoped deployment status, hashes the actual active ZIP in `SitePackages`, and requests a worker restart. `validate-candidate.ps1 -WaitForReady` allows at most five minutes for the exact release marker to activate, recording startup observations before running the normal fail-closed dependency/authentication checks. A fresh marker was observed after package activation, not just the previously extracted marker. The ZIP mount is read-only; runtime keys remain in SQL and diagnostics outside the application directory. No additional storage account, SAS, publishing password, or deployment slot is introduced.

The absence of CI means the operator is responsible for running required checks before release. The approved implementation plan authorizes only its stated PoC scope; scripts do not authorize unrelated tenant/resource changes or Git commits/pushes.

### 11.4 Recovery and cost

Normal operation has one database. The user deferred the restore drill for this delivery, so no temporary recovery database is created and recovery timing is not verified. Any future drill needs explicit approval for an isolated restore database and scoped cleanup. Never restore over the shared live database to recover one company.

No separate dev/test database, private-network service, CI worker, or Node hosting service is included in normal operating cost.

`.\scripts\inspect-environment.ps1` performs a read-only inspection of the exact approved subscription/resource group. It checks the real Linux B1 plan, online S0 database capped at 5 GiB, one application database, monitoring settings, dependency health, and limited release marker. It fetches exact Sweden Central USD consumption meters from Azure's Retail Prices API and requests month-to-date actual cost filtered to this resource group. Missing/ambiguous meters, unexpected configurations, and API failures are explicit failures, not zero-cost fallbacks. It writes sanitized evidence to ignored `artifacts\environment-operations.json`; it changes no cloud resource, firewall, access, or billing setting.

The 2026-10-23 inspection returned:

| Fixed compute | Retail rate | Estimated cost per 730 hours |
| --- | --- | --- |
| One Linux App Service B1 | USD 0.018/hour | USD 13.14 |
| One primary SQL S0 database | USD 0.4839/day | USD 14.72 |
| Fixed compute subtotal | 730 hours, about 30.42 days | **USD 27.86** |

This is a retail estimate, not an invoice, spending cap, or complete project quote. Key Vault operations, monitoring ingestion/retention, network egress, backup overages, any External ID/MFA usage, and taxes are excluded. Subscription discounts/credits and actual calendar-month length can differ. The existing External ID resource is outside this hosting resource group and was not changed. Keep the PRD's full budget range provisional rather than treating the fixed-compute subtotal as an all-inclusive price.

The scoped billing query succeeded but returned no rows; this does **not** establish zero charges, particularly for newly provisioned resources before usage is posted. Monitoring has a verified 0.1-GB daily ingestion cap and 30-day retention; that ingestion cap is not a financial limit. The inventory also includes SQL's system `master` database and an Application Insights failure-anomaly rule; the system database is not a second application/development database. Resources remain provisioned and can incur charges. Stopping only the web app does not stop provisioned App Service plan or SQL charges; any teardown, scaling, or retention change requires its own explicit decision.

## 12. Engineering standards

| Area | Default |
| --- | --- |
| C# | Nullable reference types enabled, supported compiler analyzers, warnings treated as errors for owned code |
| TypeScript | Strict mode; no broad `any` casts or unchecked JSON assertions |
| Formatting | .editorconfig and project-local Biome; supported .NET formatting tools for C# |
| Time and IDs | UTC server timestamps, opaque IDs, injectable TimeProvider where rules depend on time |
| Logging | Structured templates and allowlisted fields; never task bodies, credentials, tokens, invitation links, or raw request payloads |
| Exceptions | Catch only where recovery/mapping is intentional; log safe diagnostics; no silent default or empty-success fallback |
| Configuration | Typed options validated at startup; required values fail clearly; no localhost/development authentication fallback in hosted mode |
| Secrets | Ignored local configuration/User Secrets locally; Key Vault-backed hosted values; no VITE_ secrets |
| Dependencies | Explicit need, compatible stable version, license/support review, locked restoration |
| Generated files | Generate clients/contracts from their authority; do not hand-edit generated output |

Add standard generated-output ignores when scaffolding introduces bin/obj, node_modules, build artifacts, test output, and browser authentication state. Do not commit a real .env merely because the current tenant-name value is non-secret; future edits may add credentials.

## 13. Implementation order and remaining gates

| Milestone | Concrete implementation output | Exit evidence |
| --- | --- | --- |
| 0: prerequisites/configuration | Verify the existing tenant GUID/authority/app registrations, hosting subscription, SQL target, developer permissions, and package-feed access | Missing values are explicitly resolved, not guessed; no tenant provisioning |
| 1: scaffold/infrastructure | Version-pinned .NET projects, npm workspace, contract-generation workflow, manual scripts, and one scoped Azure environment | Reproducible build; actual connectivity and privileges verified separately |
| 2: bounded identity/MCP candidate | Minimal ASP.NET candidate using the official SDK and real External ID resource; persistent protected sessions | Preliminary three-client sign-in/discovery/protocol proof and session persistence; no all-three task-support claim yet |
| 3: SQL isolation foundation | EF mappings/migrations, bootstrap principal, RLS, context lifecycle, role resolution, transactional audit/idempotency | G3 SQL tests under restricted permissions, including pooled/cancelled/concurrent requests |
| 4: retained task slice/client proof | Shared REST/MCP task operations and the small safety gate | Local protocol/safety evidence; actual three-client read/write/renewal/revocation remains functional acceptance, not a prerequisite for UI implementation |
| 5: browser, administration, and acceptance | Fluent UI board, self-service company onboarding, invitations, directory/groups/grants, connections, audit UI, and manual release | Functional/security and G4 evidence plus onboarding PRD acceptance; V11 and formal V12 explicitly deferred |
| 6: any real-data proposal | Customer-approved stronger operational/legal/security controls as required | G5 approval; the synthetic PoC is not automatically promoted |

The existing External ID configuration is the accepted synthetic-PoC baseline. No further MFA/provider project is required by this implementation. The user-directed networking exception, narrow firewall deployment, operator SQL connectivity, managed application registrations, and managed-identity SQL/Key Vault access remain in place. Authenticated product journeys and the actual native-client matrix remain acceptance work.

Reactivation requires fresh grants/connections and explicit administrator reappointment. Invitations last seven days. Existing assignments are retained and displayed as inactive after contributor access loss. Company administrators may explicitly recover managerless teams without content access. Company creation has no per-user or environment count quota; workload sizing is not admission policy.

Self-service company activation is immediate when the rollout switch is enabled. Initial administration, durable caller-bound retry receipts, optional setup, request throttling, and last-admin protection are implemented. The accepted provider-bound address is used without inventing verification claims or changing identity policy. Implementation does not substitute for observed SQL isolation or client acceptance.

### 13.1 Historical identity-candidate checkpoints

The following records describe 23-26 October evidence. The accepted identity-baseline decision and current product checkpoint in section 13.4 supersede their remaining-work and prerequisite statements; do not restart the old MFA-policy project.

**26 October update:** the user demonstrated working interactive delegated Microsoft Graph PowerShell authentication with all five setup scopes and a successful Security Defaults read returning `true`. The user explicitly requires Security Defaults to remain enabled. No migration or competing Conditional Access policy is authorized; the existing `-Apply` safeguard remains in place. The original app-scoped MFA requirement is not considered satisfied by this decision. Device-code sign-in failed with `530035`; do not repeat it or disable protections to make it work.

The new `-PreflightOnly` mode subsequently completed against the real provider through the supported terminal canvas in the user's existing PowerShell process. It verified the approved operator through `/me`, read all provider settings using GET only, and disconnected its Graph context. Security Defaults remained enabled; the email method was enabled with External ID OTP availability `default` and no exclusions; Conditional Access and authentication-flow collections were empty at that preflight. No provider configuration was written by the preflight. The user's process-scoped context was not copied into agent tool processes or exported as a token.

The user subsequently approved creating only the PoC sign-up/sign-in flow and linking the four sign-in applications, with Security Defaults, authentication-method settings, and Conditional Access unchanged. This is preliminary identity-shell/probe setup, not acceptance of weaker MFA for the product. The separate `-Apply -UserFlowOnly` mode enforces that write scope, requires enabled Security Defaults, checks managed-flow ownership and exact application membership, and verifies the protected provider settings remain unchanged. It explicitly leaves strict MFA acceptance and runtime browser/native-client proof false.

**Actual provider setup completed at 15:09:18 UTC:** flow `88f658c1-1289-4e3a-bc3f-452ab917b49f` is linked to exactly the browser, VS Code, Claude Code, and Copilot App registrations. Normal delegated Graph sign-in succeeded in a fresh PowerShell process. The creation response omitted embedded identity-provider navigation data; setup retained the created flow ID and resumed using the documented provider-relationship GET, without creating another flow. Final Security Defaults, email-method settings, and Conditional Access match the original pre-creation snapshot, including enabled defaults, email OTP availability `default`, and no CA policies. Evidence is in ignored `artifacts\identity-user-flow-validation.json` and `artifacts\identity-user-flow-creation-preflight.json`. Employee browser/MFA/native-client authentication was unverified at that point; later user-assisted browser confirmation is recorded below. All 18 limited hosted-candidate checks passed again at 15:13:17 UTC, including live SQL/session protection health.

The hosted SQL failure is resolved. Private application-console diagnostics established SQL error `40615`: the app actually used `57.174.35.62`, which belongs to its 43 possible outbound addresses but not its 25 advertised current addresses. With explicit user approval, only that single address was added as `app-outbound-25`; the other possible addresses were not automatically allowed. The separately approved workstation rule now allows `167.220.197.139` instead of `167.220.197.244`, and operator SQL access works. All 27 rules remain individual addresses with no broad Azure-services exception. Runtime principal SID/grants were inspected and left unchanged.

Current candidate `identity-candidate-20261026T153445Z-d92883da` (ZIP SHA-256 `35B6EC5DFB3675D231FC2471AA5A44A8386DB0ECA3492A5CC7C75F93F56D7F55`) is deployed with its active package verified. Its health probe executes read-only `SELECT 1` rather than suppressing connection exceptions through `CanConnectAsync`; framework telemetry retains the actual error while public `/health` failures remain generic HTTP 503. The existing third identity test also verifies this exception/public-response boundary. Managed-identity SQL/session protection and all 18 hosted candidate checks passed at 15:38:09 UTC. This is not complete provider/MFA/native-client or product acceptance.

The user reported creating a real browser account and seeing `Signed in as unknown`. The session endpoint previously returned the `name` claim unchanged, including that literal placeholder. The current candidate treats blank/`unknown` values as unavailable and selects a usable name, then email, then `preferred_username`, otherwise the existing generic `Employee` label. This reads existing signed-session claims, so it supports already-issued cookies without a directory profile edit or another Graph permission. These labels are presentation only: they are not verified-address evidence and never replace tenant/object-ID authorization. Eight label cases were added within the existing browser-boundary test; the suite still contains only three tests. After deployment, the user clicked **Check session** in their existing signed-in browser and confirmed the email address is now displayed. This resolves the label bug and supplies user-assisted basic browser sign-in confirmation, not strict MFA, native-client, or company/task acceptance. Evidence is in ignored `artifacts\browser-sign-in-user-validation.json`; no cookie/token contents were inspected.

Evidence is in ignored `artifacts\provider-security-defaults-decision.json`, `artifacts\approved-workstation-firewall-update.json`, `artifacts\sql-runtime-diagnostics.json`, `artifacts\hosted-sql-failure-diagnostic.json`, `artifacts\application-egress-diagnostic.json`, `artifacts\approved-application-firewall-update.json`, `artifacts\hosted-sql-health-recovery.json`, and the current `artifacts\candidate-live-validation.json`. The earlier `requirements-reassessment.json` records the pre-recovery failure. Earlier successes and failures below are historical.

#### Historical candidate work through 23 October

The .NET/React/Fluent UI identity shell, separate browser/bearer authentication, CSRF handling, protected MCP identity probe, dependency health check, local scripts, Bicep, and dependency lockfiles exist. Candidate compilation, frontend static checks, and production bundling pass. The reviewed `20261023104755_InitialSessionKeys` migration is applied. The runtime SQL user has CONNECT and only SELECT/INSERT on session-key storage, without UPDATE/DELETE, schema/user administration, or db_owner.

The remaining candidate contract scaffold is implemented: generated OpenAPI, consumed browser declarations, official-SDK MCP input/output schemas and local structured probe invocation, deterministic drift checks, and packaged public JSON snapshots. The same three tests remain the entire current local suite. No business contracts or provider/native-client acceptance are inferred.

App Service B1, SQL S0, Key Vault, monitoring, and scoped key/secret roles were provisioned in `rg-kanban-poc-swc` (Sweden Central). Following the user's explicit `SecurityControl=Ignore` instruction, the resource group is tagged and SQL is `Ready` with `publicNetworkAccess=Enabled`. The 26 single-IP firewall rules match the approved workstation and 25 actual App Service outbound IPs; no broad Azure-services exception is enabled. Both operator SQL connectivity and actual hosted managed-identity dependency health pass, with mandatory SQL TLS and certificate validation. The temporary perimeter alternative was removed.

The hosted check exposed and corrected a bootstrap defect: for the Entra service principal, SQL's external-user SID must use the managed identity application/client ID (`b54a481c-0e48-48d7-91ac-32901ae7a4b0`), not its directory object ID (`7b45ecb3-f821-45e9-aae7-644896aa4e7a`). The latter remains correct for Azure RBAC assignments. Operator impersonation had verified permissions but could not detect this login-mapping error. Only the exact deployment-owned, ownership-free user with the recorded old SID and expected grants was transactionally recreated; no application tables or data were deleted. `migrate` now requires `--runtime-client-id` and fails on an unexpected existing SID. Current evidence is in ignored `artifacts\runtime-session-persistence.json`; the earlier `runtime-sql-bootstrap.json` is historical impersonation evidence, not successful login proof.

The preserved External ID tenant now has one API, one confidential browser application, and three secret-free public-client candidates. Managed setup was successfully reapplied. API v2 delegated scopes are `tasks.read` and `tasks.write`; native-client tenant consent is restricted to this API and grants no company/team access. Bearer validation now requires `azp` to match one of these three actual public registrations, rejects explicit application identity and an empty employee object ID, and retains issuer, audience, tenant, signature, expiry, and scope checks. A registered client ID is not proof of binary authenticity or company/team permission. The allowlist is wired through local configuration, registration setup, Bicep, and deployment. The browser credential is stored in Key Vault and local ASP.NET User Secrets, never in source or published assets. Actual non-secret application IDs and secret references are in ignored `artifacts\identity-applications.json`. Exact source/documented VS Code and Claude Code callbacks are now registered; Copilot App remains unconfigured, as detailed below.

The official Azure CLI cannot acquire the newly requested Graph policy scopes: nonbroker acquisition returned `AADSTS65002`, requiring Microsoft API-owner preauthorization rather than another operator permission grant. The unused operator-specific consent was restored; the shared official CLI enterprise application was retained. `scripts\configure-identity-policy.ps1` instead uses installed Microsoft Graph PowerShell with a process-scoped delegated session, default preview, and optional device-code sign-in. It preserves existing security defaults, other policies, and method exclusions. Device-code requests were stopped when the operator was unavailable. A subsequent normal-browser attempt also returned without an authenticated Graph context; the full provider preflight still has not run. The script now distinguishes missing context from wrong-directory/app-only context and cleans up any returned context on rejection. Three local mocked guard checks confirmed zero provider requests for those invalid cases; they are not authentication or provider-policy proof. Provider flow/MFA configuration and real sign-in remain blocked on the operator; credentials or tokens must never be sent in chat.

A later read-only check confirmed the existing CLI operator against Graph `/me` without new scopes, consent, or role changes. Conditional Access and authentication-flow collections were readable and empty. Security-defaults and email-method reads returned 403, and the four required policy scopes were absent. This partial directory access does not satisfy the safe provider preflight or prove write permission. Official Graph module 2.20.0 imports in both PowerShell Core and Windows PowerShell; no runtime incompatibility or cause of the normal-browser missing context was established. Another availability check confirmed the operator remains unavailable. Evidence: ignored `artifacts\identity-access-preflight.json`; no policy writes or pending authentication process.

An immutable identity candidate is deployed at [the PoC application](https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net). Current release: `identity-candidate-20261023T145248Z-273b9a27`, ZIP SHA-256 `5EB44AC93679C0A0FB24D12D3AE3311157D4E1B91A3D7D2722E002967F5EA0F2`. Entra-authenticated ZIP publishing and active-package hashing succeeded without enabling SCM/FTP basic authentication. Its distinct new release marker is served by the hosted worker; dependency health is 200; anonymous session/antiforgery and MCP operations return 401; invalid bearer tokens return 401; untrusted MCP origins return 403; reserved unknown API/contract routes return 404. Both public contract snapshots match their exact packaged hashes, and live OpenAPI structurally matches the generated snapshot used by TypeScript. Root/fallback HTML and contract revalidation, immutable asset caching, and uncached release metadata pass. The browser renders the unchanged unsigned identity shell and its explicit product limitations. Protected-resource metadata uses the actual API scopes. Browser login returns the actual HTTPS provider code/PKCE challenge with the expected client/callback, state, and nonce; the provider sign-in form was observed previously. None of this is completed authentication or MFA proof. Evidence: `artifacts\candidate-live-validation.json` and the release's `deployment.json`. The platform previously reported hosted .NET `10.0.12`, distinct from the workstation runtime/package baseline.

The first contract candidate, `144736Z-727e7cda`, failed live validation: its SPA fallback missed the cache policy and reserved routing hid static contract files. Static serving now precedes explicit routing, and fallback uses the same static-file options. A new artifact was built rather than modifying the failed ZIP. The current release passed all 18 hosted HTTP checks after bounded activation waiting; old-worker responses and transient startup timeouts are recorded, not treated as acceptance.

The preceding `134900Z-2264707f` deployment initially returned HTTP 500 on dynamic routes and recovered after a diagnostic restart. Its original exception was not captured, so no specific application-code root cause is claimed. The now-verified package activation avoids mixed-file publishing; upload acknowledgement is deliberately separate from worker readiness and live acceptance. Scoped filesystem warning/container diagnostics were enabled during recovery.

The same encrypted SQL key had previously been verified across a deployment restart in `artifacts\runtime-session-persistence.json`. A fresh operator metadata read was denied because SQL now observed workstation egress `87.212.162.31`, rather than the approved `167.220.197.244`. The original 26 single-IP rules were checked and left unchanged. Current hosted SQL/key-protection health passes, but the latest operator key-identity recheck is blocked; neither a new key-continuity proof nor a real authenticated browser-cookie restart is claimed.

Kanban/domain authorization, six-group safety cases, generated business contracts, authenticated browser/API acceptance, all-three MCP proof, and full product deployment validation are not complete. Copilot App UI automation is blocked by built-in policy; its actual configuration/sign-in must be user-assisted or use an independently supported authorized mechanism, not a workaround. SDK capability documentation alone is not installed-client acceptance. Existing resources remain provisioned and may incur charges. Management-group policy assignments, unrelated resource groups, and Git history were not changed. No second database was created.

### 13.2 Current operator entry points

Run from the repository root. Keep all target resources in the approved environment.

- `.\scripts\configure-identity-apps.ps1` previews managed registration setup; `-Apply` reapplies it. Supply native callback URI parameters only after the exact client callback is established.
- `.\scripts\configure-identity-policy.ps1` previews provider setup without prompting. Configuration and `-PreflightOnly` reuse a current process-scoped delegated Graph context when available, otherwise initiate normal interactive authentication. Tenant, authentication type, all five displayed setup scopes, and the approved operator's `/me` are checked before provider configuration; an incompatible existing context is rejected without replacing it. `-PreflightOnly` reads security-defaults, email-method, Conditional Access, and authentication-flow settings into ignored `artifacts\identity-preflight.json`, without provider writes. It can complete with Security Defaults enabled, is mutually exclusive with configuration modes, and disconnects its verified process-scoped Graph context afterward. If a long-lived terminal returns without an authenticated context, use a fresh PowerShell process for normal interactive `Connect-MgGraph` and run setup in that same process; do not export tokens or use device code.
- The explicitly approved `.\scripts\configure-identity-policy.ps1 -Apply -UserFlowOnly` mode creates only the managed password sign-up/sign-in flow and its four application links. It does not patch authentication methods or create/change Conditional Access; enabled Security Defaults are required, and protected settings are compared before/after. It refuses unexpected flow ownership or application membership and preserves partial IDs for safe reruns. Successful readback is saved to ignored `artifacts\identity-user-flow-validation.json`, with strict MFA and runtime sign-in acceptance explicitly false. Without `-Apply`, `-UserFlowOnly` is a non-authenticating preview.
- `-Apply` without `-UserFlowOnly` still stops while Security Defaults are enabled. That full MFA-policy path remains unauthorized under the user's keep-enabled decision. Do not use `-UseDeviceCode`, which Security Defaults block, or disable tenant controls to unblock a command.
- For an explicitly reviewed migration, first dot-source `scripts\Common.ps1` and call `Import-LocalConfiguration`, then run `dotnet run --project src\Kanban.Web --configuration Release --no-build --no-restore --no-launch-profile -- migrate --runtime-client-id b54a481c-0e48-48d7-91ac-32901ae7a4b0 --runtime-principal-name app-kanban-poc-2rza5wd265s3g`. Verify the managed identity's application ID against its service principal before reuse if the app identity changes.
- `.\scripts\deploy.ps1 -DeveloperIp 167.220.197.139 -VerifiedAppOutboundRules @{ 'app-outbound-25' = '57.174.35.62' }` previews the current approved infrastructure, including the one SQL-observed extra application address. Exceptional rules require explicit input, individual IPv4 addresses, distinct names, and membership in this app's live possible-outbound set; this does not authorize all possible addresses. Reverify workstation egress before reuse. `-ApplyInfrastructure` applies only the approved resource group and its explicit policy tag; it does not publish code.
- `.\scripts\inspect-environment.ps1` checks the actual approved hosting footprint, health, monitoring cap/retention, exact retail compute meters, and scoped month-to-date billing without changing Azure. Empty billing results are reported as unavailable usage evidence, not zero charges.
- `.\scripts\generate-contracts.ps1` regenerates product OpenAPI, MCP schemas, and browser declarations using the isolated host and existing pinned tooling. `-Check` rejects drift without changing snapshots; neither mode accesses Azure SQL.
- `.\scripts\verify.ps1 -CandidateOnly` runs local build/static and database-free identity/contract checks. Full product verification requires `-Sql -SqlTarget 'tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433'` and rejects skipped/failed SQL cases.
- `.\scripts\release-candidate.ps1 -Product -SqlTarget 'tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433'` runs the full local gate, publishes the product once, and writes an immutable ZIP/hash manifest, local safety evidence, and reviewed migration script under ignored `artifacts\releases`. It retains `productAccepted=false`; local checks are not hosted/native-client acceptance.
- `.\scripts\release-candidate.ps1 -ArtifactPath <exact-built-ZIP>` previews publishing; add `-Apply` to upload that same hash-verified artifact through ZIP deploy, verify the active ZIP, and restart the worker. Required package settings must already be applied.
- `.\scripts\validate-candidate.ps1 -ArtifactPath <exact-built-ZIP> -WaitForReady` waits boundedly for exact worker activation, then checks health, HTTP denials, metadata, and the real provider challenge. For contract-enabled artifacts it also verifies published snapshot hashes, actual OpenAPI equivalence, static/SPA cache policies, and unknown-contract 404 behavior. It writes sanitized candidate evidence only after success. Provider sign-in, MFA, client proof, and product acceptance remain separate.

### 13.3 Native-client preparation, not authenticated acceptance

For product calls, first sign in through the browser, have a company administrator approve the exact client registration, and authorize your own permitted teams/read-write subset under **Your connections**. Company administration alone gives no board access; select an explicit initial manager or obtain a team grant. These application grants do not replace the client's native provider sign-in.

| Client | Prepared configuration and callback | Observed status / remaining gate |
| --- | --- | --- |
| VS Code `1.143.0`, commit `2d0b7506a36c1d6db9a466440131a8e01eab2289` | `.vscode\mcp.json` uses its actual public `oauth.clientId`; exact-commit local OAuth source uses `http://127.0.0.1:33418/`, now registered | Sign-in and `identity_probe` have not run. The client falls back to a random port if 33418 is occupied; that fallback is not registered or verified. The separate hosted `vscode.dev` callback was not added. |
| Claude Code `2.1.301` | Native CLI added repository-local `kanban-poc`, with its actual public client ID and fixed registered `http://localhost:43129/callback` | `claude mcp get kanban-poc` confirms the configuration and reports **Needs authentication**. No login or tool proof. A native issuer-stamp/isolation warning is not a successful sign-in and private credential files were not inspected. |
| GitHub Copilot App | Secret-free public registration exists; actual callback/configuration remains unverified and no callback is registered | Direct app UI automation is policy-blocked. Requires user-assisted or independently supported authorized setup; SDK documentation is not app proof. |

All clients target `https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net/mcp`. VS Code's local `servers` schema must not be confused with other clients' `mcpServers` schema.

For the current Copilot App setup, select HTTP, use that MCP URL, leave custom authorization headers empty, and supply the Copilot App public application ID above in its OAuth Client ID field. This client-side setting is separate from the backend environment allowlist: the desktop app does not inherit Azure App Service variables or automatically expand a server `.env` reference. Keep explicit client configuration for now; no automatic-registration gateway or custom OAuth server is introduced. The exact Copilot App callback must still be registered and native sign-in verified; the environment setting alone does not establish client compatibility.

For Claude, the configured native command was `claude mcp add --transport http --scope local --client-id 26120dc3-5c5e-4a8a-944c-5911a5d834f0 --callback-port 43129 kanban-poc https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net/mcp`. The cached CLI is invoked without a global install: dot-source `scripts\Common.ps1`, call `Use-ProjectNode`, then use `npm exec --offline --package=@anthropic-ai/claude-code@2.1.301 -- claude ...`. With the accepted provider configuration, `claude mcp login kanban-poc` requires the employee's own interactive sign-in. Do not substitute a shared secret or employee app-only token.

### 13.4 Current product checkpoint - 27 October

Company onboarding, invitations/directory, groups and explicit team access, managerless recovery, boards/tasks, approvals/connections, filtered auditing, browser routes, REST adapters, and six business MCP tools are implemented. Existing identity policies and Security Defaults remain the accepted baseline. A narrow, backward-compatible API identifier alias and matching MCP scope metadata correct the native compatibility failure described below; application/scope IDs, grants, callbacks, issuer/audience checks, and browser sign-in are preserved. Company creation has no count quota. The self-service switch defaults to off for staged rollout.

Four reviewed business migrations are applied to the existing `kanban` database on `tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433`; the latest is `20261027104453_ProfileErrors`. They preserve session keys and restrict runtime table/module permissions. The final procedure update gives an uninitialized delegated profile its own structured SQL error; known transient dependency failures become safe `DEPENDENCY_UNAVAILABLE` responses, not successful fallbacks.

| Evidence surface | Current result and boundary |
| --- | --- |
| Local automated SQL/REST/MCP | The packaging gate passes all 29 tests with zero failures/skips, plus a warning-free .NET build, frontend lint/typecheck/build, and generated-contract drift checks. All 29 also pass against this ZIP's exact Core/Infrastructure/Web DLLs across eight groups. An additional targeted real-SQL rollout-switch test now passes against those same archived DLLs; it verifies disabled creation, committed retries, existing-company access, content conflicts, and safe re-enabling with the previously rejected key. This is one supplemental passing test, not a relabeled full 30-test run. Packaging refuses differing tested/published runtime DLL hashes. Earlier evidence remains with its original release. |
| Restricted SQL permissions/RLS | Real Azure SQL probes use `EXECUTE AS USER`, not an in-memory provider. They verify reads, write blocks, bootstrap denials, session cleanup, and readiness under the configured restricted user. Local service fixtures otherwise use the operator credential; this is not a genuine hosted-runtime business journey. |
| Actual hosted business components | New private run `48f47d1eb2684aa59e37f8edb5c75ac8` passed 13 checks inside the real App Service container on .NET 10.0.14 using its system-assigned managed identity. Exact newly mounted DLL hashes, restricted SQL principal/permissions, company/profile/invitation/team bootstrap, setup/default board, cross-company denial, groups, tasks/ETags/idempotency, connections/revocation, inactive assignments, recovery, and filtered audits passed. Callers, clock, and cursor keys are test-controlled; no provider sign-in or HTTP/MCP/browser/native-client journey is implied. |
| Browser component journeys | The earlier 25-check `tests\browser-smoke.js` result is bound to this release by identical JavaScript/CSS bundle hashes in `frontend-evidence-binding.json`; it is not represented as a new browser run. It covers uncertain company creation/reload with the identical key/content, held late company responses, fresh state on switching back, identity-cache replacement, session-expiry content removal, company/setup/team/task flows, conflict/keyboard/navigation, invitations, connection paging, retry expiry, responsive layout, and denials. HTTP responses are mocked; no actual provider or SQL journey is implied. |
| Interactive local/hosted product journeys | Blocked: the user was unavailable for synthetic-account sign-in. Existing basic identity sign-in evidence is retained, but is not substituted for company/task acceptance. |
| Artifact/deployment | The exact tested ZIP is activated on the existing Azure application; all 21 hosted smoke checks passed initially, after the private component probe, and after enabling company self-service. Managed-identity SQL/Key Vault/session readiness, product schema/tenant-read readiness, contracts, HTTP denials, aligned metadata, caching, and code/PKCE challenge are verified. Bounded activation retries observed the previous worker and transient timeouts before readiness. Provider-authenticated business writes are not established by these checks. |
| Native clients | The original real Claude Code 2.1.301 code/PKCE flow failed with `invalid_target` / `AADSTS9010010` before sign-in. After the narrow alias correction and new deployment, a fresh official CLI invocation in a real terminal emits matching HTTPS resource/scope URIs and reaches the normal provider email form, not the resource error. The user was unavailable to sign in; the attempt was canceled and its callback released. The stored-credential issuer-stamp warning is recorded, not treated as authentication success. VS Code and Copilot App remain unverified; Copilot App callback/setup remains user-assisted. No client has passed business read/write/renewal/revocation acceptance. |
| Data and operational scope | Synthetic fixtures only; all recorded SQL fixture manifests are cleaned. The private cloud runner was removed and `dotnet Kanban.Web.dll` restored. Both release and supplemental-test single-address SQL firewall rules were removed, restoring the original allowlist exactly. Post-rollout control-plane checks rehashed the unchanged active ZIP and verified ordinary startup and the enabled self-service setting. Earlier pre-rollout records correctly retain the then-disabled setting. No new infrastructure, identity-policy changes, real-data approval, restore drill, or formal full-dataset benchmark. |

Active release: `kanban-product-20261027T141228Z-a79e0911`. ZIP SHA-256: `09AE8A14B03E99BC3DC351193A5FCF50B531E5A881915388ADA8E3BF0569856D`. Target: [existing PoC application](https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net), resource group `rg-kanban-poc-swc`. The active hosted ZIP hash was rechecked after the runtime probe; Core, Infrastructure, and Web DLL hashes match the tested/published copies. Marker and manifest both include `mcpResourceScopesAligned=true`. The previous product release `kanban-product-20261027T105938Z-99d411f7`, its failed native attempt, and the earlier identity package remain preserved. No rebuild occurred between packaging and deployment.

The application is an **unaccepted candidate with self-service enabled for authenticated acceptance**, not a completed full-feature sign-off. Following the approved rollout sequence, `Application__SelfServiceEnabled=true` was set after the initial smoke checks and the supplemental real-SQL switch test passed. The ordinary worker was restarted; all 21 hosted checks passed again, the setting was read back, and the exact active ZIP was rehashed. No other setting, identity policy, or SQL schema/permission was changed for activation. The switch's code default remains `false`; its actual hosted value is now explicitly `true`.

The shared browser's actual session returned 401. The repaired native flow reached the normal provider email form, but the user remained unavailable to complete sign-in. The resource mismatch and disabled feature switch are no longer blockers; genuine employee authentication and remaining native setup/acceptance still are. The native attempt was canceled, its listener released, and its terminal exited. Browser panels are back at the hosted landing page for a fresh later challenge. No task-owned local API/preview helper remains running. Authenticated users can now attempt company creation; that availability is not misrepresented as a passed end-to-end journey.

Current portable evidence is under `artifacts\releases\kanban-product-20261027T141228Z-a79e0911`: the immutable ZIP/manifest, reviewed migrations, `local-safety.json`, `product-safety.trx`, `deployment.json`, `initial-live-validation.json`, `runtime-component-safety.trx`, `hosted-runtime-components.json`, `hosted-runtime-cleanup.json`, `hosted-runtime-execution.json`, `post-runtime-live-validation.json`, `post-runtime-control-state.json`, `resource-fix-execution.json`, `frontend-evidence-binding.json`, `browser-ui-recovery-smoke.json`, `mcp-resource-registration.json`, `mcp-resource-protocol-preflight.json`, `pre-fix-native-oauth-failure.json`, `native-oauth-preflight.json`, `rollout-switch-safety.trx`, `rollout-switch-execution.json`, `self-service-rollout.json`, `post-rollout-live-validation.json`, `post-rollout-control-state.json`, and `product-acceptance.json`. `product-acceptance-before-self-service-rollout.json` preserves the earlier disabled-switch checkpoint. Historical evidence remains with its original release rather than being relabeled as new execution. All acceptance flags remain false where a real browser/provider/native-client scenario has not run. No tokens, authorization codes, PKCE verifier/state, passwords, or private credential-store contents are included in these reports. The legacy session endpoint's stage label is not a product-release or acceptance signal; use `/release.json` and the acceptance evidence.

The historical bootstrap supplement encountered a changed workstation SQL egress address. Its temporary firewall rule allowed only that one address and was removed in `finally`; the original allowlist was compared and restored exactly. Earlier firewall and RLS-aware test-inspection failures remain preserved separately and are not passing evidence. Its final targeted two-test and full 29-test runs passed, and both bootstrap cases now pass in this release's full source and exact-archive suites. The new release operation likewise restored its own temporary single-address rule and cleaned all recorded SQL fixtures. No SQL schema or permission change was needed for the MCP correction.

The native OAuth failure was a concrete compatibility finding, not a renewed MFA-policy gate. Under the renewed direction to make decisions and finish autonomously, the narrow correction was applied: retain `api://242a2b31-70b7-4591-8131-dca9eb53e6d0`, add `https://app-kanban-poc-2rza5wd265s3g.azurewebsites.net/mcp` to the same API's identifiers, and advertise that URL's `/tasks.read` and `/tasks.write` scopes plus `offline_access`. Readback verifies the API configuration and scope IDs are unchanged. Existing clients, grants, callbacks, token version, issuer/audience validation, browser authentication, MFA, and Security Defaults were not reconfigured. A cookie-free public authorization check returns the expected `302 login_required`, not `invalid_target`; the actual installed native CLI independently reaches the email form with these parameters. Neither result is completed authentication. [MCP requires the canonical resource indicator](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization#resource-parameter-implementation); it was not stripped, and no shared secret or custom OAuth bridge was introduced. [The documented v2-token identifier-URI behavior](https://learn.microsoft.com/en-us/entra/identity-platform/identifier-uri-restrictions) allowed the alias without a policy exemption.

For an existing registration, `.\scripts\configure-identity-apps.ps1 -McpResourceOnly` previews only this identifier alignment; add `-Apply` to patch `identifierUris` and verify readback. This guarded mode returns before browser/client/credential/consent setup. Do not rerun full identity bootstrap for this correction. Build a new immutable candidate for metadata changes; do not modify an existing ZIP. `release-candidate.ps1` records the tested runtime hashes and refuses differing published DLLs; `validate-candidate.ps1` checks URL-rooted scopes for aligned manifests while retaining legacy-manifest validation.

Onboarding traceability below records supporting evidence, **not blanket sign-off** of each criterion:

| Criteria | Supporting evidence | Remaining acceptance |
| --- | --- | --- |
| SO-01, SO-02, SO-03 | `ProductBoundaryTests` browser-cookie creation/replay; `MutationSafetyTests` initial parallel creation yields one company and one membership, with no teams; actual-runtime component bootstrap and isolated UI setup flow | Real provider registration/new-user and existing-user browser journeys |
| SO-04, SO-08, SO-09, SO-17 | Role, identity, product-boundary, SQL ownership, mixed-header, unknown-field, and CSRF checks; hosted anonymous denials | Real signed-in browser/native-client permission matrix |
| SO-05, SO-15 | Transactional explicit-manager team creation; setup/default board read through the actual-runtime service and restricted SQL connection; mocked optional-setup and team navigation | Real interrupted/resumed browser setup, including the actual first-board journey |
| SO-06, SO-07 | SQL invitation recipient/expiry/revocation/consumption and explicit missing-profile checks; mocked invitation exchange/history scrubbing | Actual sign-in return, recipient identity changes, and provider-profile browser handling |
| SO-10, SO-14 | Separate same-name companies and immutable identity mapping; wrong-company IDs/cursors/assignments denied, including actual-runtime company isolation; request throttling returns 429/Retry-After; no count-quota branch/configuration | Real multi-company browser navigation; this does not establish unlimited hosting capacity |
| SO-11, SO-13 | Durable HTTP/SQL replay, parallel company/task deduplication, and changed-content conflicts pass in the current source and exact-archive suites; the earlier targeted `parallel-company-retries.trx` remains with its original release. Mocked uncertain company result and reload reuse the identical creation key/content. | Actual dropped-response/browser restart reconciliation |
| SO-12 | Shared mutation/audit rollback plus two targeted real-SQL company-bootstrap checks: null audit metadata fails the final audit insert under the runtime-equivalent SQL user, leaving no extra company, membership, or receipt; a lock on only the fixture employee's creation resource produces `503 DEPENDENCY_UNAVAILABLE` with no receipt. Both recover using the same key and create exactly one company with its two audit events. No schema or permission changes are used. | Authenticated browser error display and recovery for these failures |
| SO-16 | Identity/company-scoped caches and cancellation; mocked refresh denial hides cached content; held old-company response is discarded across switching; identity replacement and session expiry remove former content | Real session expiry/sign-out and late-response identity/company switching |
| SO-18, SO-19 | Real operator create/appointment audit checks, last-admin protection, and actual-runtime narrow managerless recovery without administrator content access | User-assisted recovery walkthrough and operational sign-off |
| SO-20, SO-21 | No implicit teams/connections on creation; explicit approval/grant tests; mocked keyboard, responsive, conflict/denial and retry UI checks; SQL dependency-error mapping; synthetic-data notices | Authenticated hosted integration and accessibility/error journeys |

Architecture traceability: V1 has local service/HTTP, actual-runtime component, and mocked UI support, with the real end-to-end journey pending; V2-V5, V8-V10, V14-V15 have the focused SQL/role/revocation/protocol and private managed-identity component evidence above, with provider-authenticated hosted counterparts pending. V7 combines the SQL competing-version test, actual-runtime task ETag conflict, and mocked draft-retention check. V13 now also has mocked late-company-response, identity replacement, and session-expiry checks alongside keyboard and denial/conflict support; real signed-in switching remains unverified. V6 is blocked on actual native-client setup/sign-in. V11 and the formal V12 benchmark remain deferred; the exact V12 100-task page bound was verified with 101 SQL tasks.

To repeat the private component check, run `.\scripts\test-hosted-runtime.ps1 -ArtifactPath <existing-product-ZIP> -SqlTarget 'tcp:sql-kanban-poc-2rza5wd265s3g.database.windows.net,1433'` for a non-mutating preview; add `-Apply` to execute. Its maintenance guard requires self-service creation to be disabled deliberately before execution; do not run it against the currently enabled rollout without first arranging that maintenance step. The script binds the runner to the ZIP's exact DLLs and now requires at least 30 passing safety checks across eight groups, including the new rollout-switch test. It uploads only under private application data, temporarily replaces `dotnet Kanban.Web.dll` with a one-off wrapper, then restores startup, verifies readiness, cleans fixtures, and removes the private runner. It does not modify the ZIP, public routes, authentication, provider settings, publishing policy, or self-service flag. Re-enable the rollout switch and repeat hosted checks after an authorized maintenance run. The CLI entry point exists only in the safety-test artifact. Earlier failed preflights remain historical evidence; the current successful 13-check run is the new release-bound run named above.

The workstation's pinned SDK is also available locally at `.tools\dotnet-10.0.402`; in a fresh shell, set `DOTNET_ROOT` to that directory and prepend it to `PATH` when the global SDK does not satisfy `global.json`. No global SDK settings were changed.

Operator product commands run only from the approved workstation credential, never through an HTTP/MCP endpoint. Import local configuration first, then use `dotnet run --project src\Kanban.Web --configuration Release --no-build --no-restore --no-launch-profile -- create-company --directory-id <accepted-External-ID-tenant-GUID> --employee-object-id <existing-employee-oid> --employee-name <synthetic-name> --employee-email <provider-bound-address> --company-name <name> --idempotency-key <stable-16-128-character-key>`. Reuse the same key/content after an uncertain result. The atomic company/admin/receipt/base audit is followed by an idempotent operator audit transaction; do not describe both transactions as one atomic commit.

For exceptional administrator appointment use the same command prefix with `appoint-administrator --company-id <company-GUID> --membership-id <same-company-membership-GUID>`. It is company-locked, audited with the operator object ID, and grants no team-content access. Reactivation does not revive historical team/group/connection grants.

To repeat the isolated UI-only smoke check, build the client, serve `node .\node_modules\vite\bin\vite.js preview .\client --host 127.0.0.1 --port 4173 --strictPort` using the pinned Node runtime, and run `playwright-cli -s=kanban-ui-smoke open about:blank --browser=msedge` followed by `playwright-cli -s=kanban-ui-smoke --raw run-code --filename=tests\browser-smoke.js`. The runner intercepts only this local preview's API requests and uses synthetic fixtures; it must never be treated as provider authentication or a hosted permission check. Close the named browser and preview process afterward.

## 14. Sources and version evidence

References consulted on 2026-10-23; they establish publisher documentation/metadata, not an end-to-end working application:

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) and [official .NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
- [C# language/target version mapping](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-versioning).
- [ASP.NET Core App Service configuration and runtime discovery](https://learn.microsoft.com/en-us/azure/app-service/configure-language-dotnetcore).
- [App Service run-from-package](https://learn.microsoft.com/en-us/azure/app-service/deploy-run-package), [ZIP deployment and Entra authentication](https://learn.microsoft.com/en-us/azure/app-service/deploy-zip), and [Kudu ZIP deploy implementation](https://github.com/Azure-App-Service/KuduLite/blob/3854066bb50c3a1933aebbc4143c06c47535be68/Kudu.Services/Deployment/PushDeploymentController.cs).
- VS Code exact-commit [native OAuth provider](https://github.com/microsoft/vscode/blob/2d0b7506a36c1d6db9a466440131a8e01eab2289/src/vs/workbench/api/node/extHostAuthentication.ts) and [loopback server](https://github.com/microsoft/vscode/blob/2d0b7506a36c1d6db9a466440131a8e01eab2289/src/vs/workbench/api/node/loopbackServer.ts); [Claude Code MCP authentication/configuration](https://code.claude.com/docs/en/mcp).
- [Node.js release metadata](https://nodejs.org/dist/index.json).
- [React publisher versions](https://react.dev/versions), [TypeScript project-local compiler guidance](https://www.typescriptlang.org/download/), and [Vite guide](https://vite.dev/guide/).
- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), [HTTP SDK package](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore), and [Microsoft.Identity.Web package](https://www.nuget.org/packages/Microsoft.Identity.Web).
- [Existing External ID tenant and application setup concepts](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-create-external-tenant-portal) and [sign-in user flows](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-user-flow-sign-up-sign-in-customers).
- [User-supplied npm feed](https://packagefeedproxy.microsoft.io/npm/).

Machine-readable contract publication, dependency restores, a runtime, or an automated acceptance result must not be claimed until the corresponding implementation output exists and has been exercised.
