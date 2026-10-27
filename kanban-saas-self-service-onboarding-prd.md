# Self-service company onboarding: draft PRD

Status: product source implemented; hosted self-service enabled for authenticated acceptance. Required end-to-end/native-client evidence remains incomplete; not compliance-approved.
Date: 2026-10-23.
Platform: browser application.
Parent product: [B2B Kanban SaaS PoC PRD](kanban-saas-prd.md).
Companions: [technical design](kanban-saas-technical-design.md) and [implementation specification](kanban-saas-implementation-spec.md).

## 1. Purpose and decision authority

Let a new or existing user create a company themselves and begin setting it up without waiting for a platform operator. Preserve the existing company/team isolation, invitation-based employee admission, and explicit team permissions.

This PRD replaces the parent documents' operator-only company-onboarding assumption. It does not replace the rest of the Kanban product requirements, expand the PoC into a production service, or claim that the application already supports this journey.

| Decision | Status |
| --- | --- |
| Users can create companies through self-service onboarding | User requested |
| An eligible verified user creates an active company immediately, without operator approval | User confirmed |
| Operator-led creation remains available alongside self-service | Retained from the request to add self-service |
| The creator becomes the new company's initial company administrator | Proposed MVP behavior |
| Company administration alone grants no team-content access | Existing product constraint |
| Employee admission remains invitation-based; no email-domain auto-join | Existing product constraint |
| Companies share the existing identity directory and application/database deployment | Existing architecture constraint |
| Accept the current identity configuration as the synthetic-PoC baseline | User confirmed |
| No per-user or environment company-count quota | User confirmed |
| Invitations expire after seven days | User confirmed |
| Retain assignments after access loss and mark them inactive | User confirmed |
| Company administrators may explicitly recover managerless teams without content access | User confirmed |

Unless identified as confirmed or existing above, the detailed behavior below is a proposed MVP requirement for review.

## 2. Users and desired outcomes

| User | Situation | Desired outcome |
| --- | --- | --- |
| New company creator | Has no application account or company membership | Authenticate, verify their address, create a company, and reach its setup page |
| Existing employee | Already belongs to one or more companies | Deliberately create a separate company without modifying existing access |
| Invited employee | Arrives through an invitation | Join the intended company without being forced to create another one |
| Company administrator | Has created an empty company | Set up a team, arrange explicit access, and invite colleagues |
| Platform operator | Supports the bounded synthetic-data PoC | Retain controlled onboarding and recovery without approving each normal creation |

The primary completion event is a committed, active company with an active initial administrator membership. Creating a first team, inviting colleagues, and creating a first task are later activation milestones, not prerequisites for company creation.

## 3. Scope

| Include | Exclude |
| --- | --- |
| Accepted provider-managed sign-up/sign-in and provider-bound address | Application-managed passwords or a custom identity provider |
| An explicit Create company entry point for new and existing users | Automatically creating a company on every sign-in |
| Minimal company-name form and immediate activation | Operator approval queues, billing, plans, trials, payment details, and commercial subscription provisioning |
| Initial administrator appointment and a resumable setup page | A new owner/super-admin role or automatic access to every team |
| Guided reuse of team, invitation, group, and access-management flows | New invitation-email delivery, notifications, or bulk employee import |
| Safe retries, abuse controls, isolation, and audit evidence | Anonymous creation, public company search, domain claiming, or verified-organization badges |
| Continued operator-led company seeding | New Entra tenants, per-company databases, Azure resources, or automatic corporate SSO setup |

Company deletion, ownership transfer, a new administrator-promotion workflow, and a general support console are outside this feature. Existing task, REST, MCP, real-data, and operational requirements remain in force.

## 4. End-to-end experience

### 4.1 Choose the correct entry point

An unauthenticated visitor can choose Create company and continue through the managed identity provider. The application explains that signing in or registering an identity does not itself create a company.

After a successful session/membership check, a user with no active companies sees Create company and guidance to open an invitation from an existing company's administrator. A user with memberships can open Create company from the company switcher. Do not interrupt their existing company workflow or change its permissions.

An invitation link preserves its intended acceptance journey through authentication. Do not redirect an invited employee into company creation merely because they have no active memberships. Do not reveal companies by matching an email domain or searching their names.

### 4.2 Authenticate and verify eligibility

New users register and existing users sign in through the configured Microsoft Entra External ID flow, under the product's authentication/MFA policy. The application never collects or stores passwords.

Creation uses the accepted existing provider-managed sign-up assurance and a provider-bound address for this synthetic PoC. No new MFA/provider-policy or separate email-verification project blocks implementation. A missing usable address is an explicit error. The display-name fallback is not an address or immutable identity key; this baseline is not a production verification/compliance claim.

Map the verified immutable directory/object identity to the internal User ID. Email is contact/eligibility data, not the identity key. A user does not need an existing company membership to create a new company.

Verification proves address ownership, not legal employment, authority to represent a business, or ownership of an email domain. The UI must not imply otherwise.

### 4.3 Name the company

Collect only the company display name. Proposed MVP validation: trim surrounding whitespace and accept 1-100 Unicode scalar values, consistently counted by browser and server. Treat the value as plain text.

Explain before submission: "You will become this company's administrator. Team access is granted separately." Keep the synthetic-data-only notice visible.

Company names are not globally unique and do not reserve a domain or claim an existing organization. A same-name submission creates a separate company with a different opaque ID; it never joins or overwrites an existing company. Do not disclose other companies through duplicate-name suggestions or errors.

Do not require a domain, logo, industry, legal address, credit card, first team, or colleague invitation to finish this step.

### 4.4 Create and activate

The explicit Create company action must:

1. Revalidate the current authenticated identity, accepted provider-bound address, and creation controls on the server.
2. Create the company, its mutation-coordination state, and the creator's active company membership with company-administrator status.
3. Record company creation and initial administrator appointment with the correct actor, company, channel, and correlation metadata.
4. Commit that company-scoped state atomically, then select the new company and show its setup page.

The company is active immediately after commit; there is no pending-approval state for an accepted submission. If identity resolution needs to create a global User record, that identity record alone does not count as a completed company or confer access.

Derive the creator from the authenticated session. The request cannot nominate another user, choose an existing Company ID, supply privileged role flags, or request team/MCP permissions. Do not create a default team, board, group, invitation, client approval, or connection grant as a side effect.

### 4.5 Guide setup without blocking access

The setup page identifies the selected company, confirms the administrator role, and explains the distinction between administration and team-content access. Show contextual actions rather than a compulsory tour:

| Action | Behavior |
| --- | --- |
| Create your first team | Ask for a team name and an explicit initial Team manager selection |
| Invite colleagues | Open the existing invitation flow; explain that accepting grants company membership, not board access |
| Set up groups and team access | Open existing company-scoped group/grant management |
| Continue later | Leave the active company intact; allow setup to be reopened |

If the creator wants to use the first board, they explicitly select their own active membership as the team's initial Team manager. It is not an implicit privilege of company creation or administrator status.

Team creation commits the team, its single default board, and the explicit initial manager grant together. The board retains To do, In progress, and Done. A team-creation failure does not undo the previously created company.

Optional setup work can be skipped and resumed. Derive progress from current authorized company state, not stale browser completion flags. Returning to setup must not recreate a company/team, reissue invitation secrets, or restore revoked permissions. An administrator without a team grant sees a valid no-permitted-teams state, not a permission bypass.

### 4.6 Admit employees and enable integrations separately

Reuse time-limited, single-use, verified-recipient invitations. Share invitation links through the existing approved channel; this feature does not add an email service. Acceptance creates only the intended company membership. Direct/group grants remain the explicit path to team access.

AI-client approval is off by default. If enabled later, each employee separately authorizes a company/team/capability-bounded connection that cannot exceed their current access. Company creation neither approves an MCP client nor changes any existing connections.

## 5. Access and tenancy invariants

The initial administrator is an ordinary global user with an explicit membership in the newly created company, not a platform operator or a new cross-company owner role. Existing memberships and grants remain unchanged.

Company creation is a deliberately narrow operation that does not require prior membership. It must not introduce a general authorization/RLS bypass, unrestricted company-directory access, or runtime schema privileges. Subsequent reads and writes use the existing current-membership, ownership, team-role, and connection checks.

Self-service creation is browser-session-only with CSRF protection. Employee task bearer tokens and MCP tools cannot create companies or appoint administrators. The future technical contract must make this boundary explicit rather than infer it from a hidden UI action.

Company discovery returns only active memberships. Company switching clears old requests/data and rejects late responses using the original selection context. Sign-out or an identity change clears onboarding/session state so another signed-in person cannot submit or view the previous person's work.

Administrator self-grants remain possible through explicit audited team access changes under the parent product's trust model. Do not describe the absence of automatic access as prevention of deliberate self-granting.

Protect the last active administrator against ordinary deactivation/demotion that would strand a company. Any exceptional administrator recovery remains a controlled, audited operator action; it is not an invitation, same-domain, or self-service ownership-claim shortcut. Reactivation retains the existing requirement for explicit administrator reappointment and fresh grants/connections.

## 6. Reliability, errors, and resumability

Company creation needs server-enforced duplicate-submission protection, not just a disabled button. Bind a creation attempt to the verified caller, operation, request identifier, and canonical submitted content before a Company ID exists. Retrying the same attempt returns the same outcome; reusing its identifier with different content returns a conflict. Never replay another user's result.

Replaying a committed attempt does not consume another creation allowance or create/reactivate memberships. Return its result only as current access permits; a retry cannot undo later deactivation or restore administrator status.

Concurrent attempts must preserve duplicate-submission protection and request-rate controls; there is no company-count quota. A failed company-creation transaction leaves no partial company, administrator membership, or success audit. A committed attempt whose response was lost remains discoverable/retryable without creating another company.

| State | Required experience |
| --- | --- |
| Loading session or memberships | Show progress; do not claim the user has no companies |
| Verification required | Explain how to verify; preserve non-sensitive form input within the same identity context |
| Invalid name | Show a field-specific accessible error; preserve the value |
| Submission in progress | Show pending feedback and prevent accidental repeated activation |
| Session expired or identity changed | Require authentication and recheck eligibility; do not silently submit as another person |
| SQL/provider unavailable | Show an explicit dependency error and a safe retry path; no local success fallback |
| Commit outcome unknown after timeout | Reconcile/retry the existing attempt; do not start a fresh creation automatically |
| Creation disabled, capacity reached, or throttled | Explain the applicable restriction and permitted next action without exposing other companies |
| Company created; optional setup failed | Confirm that the company exists and let the user retry only the failed setup action |
| Returning after permissions changed | Reauthorize; do not use saved setup progress to restore access |

Preserve a typed company name across recoverable errors within the same identity context. Do not store credentials, tokens, invitation links, or company/task responses in persistent browser storage. Do not render denied or failed requests as successful empty states.

## 7. Abuse, capacity, and privacy

Self-service has no fixed per-user or environment company-count limit. Retain request-rate/concurrency protection and bounded inputs. This does not promise unlimited storage or throughput, authorize automatic infrastructure scaling, or remove tenant isolation.

The existing envelope of up to 5 companies, 100 registered users, and 10 concurrently active users remains a sizing assumption, not an enforced company-creation quota. Do not silently reinterpret it as a proven unlimited public SaaS capacity or add paid infrastructure per company.

Provide an operator-controlled switch to stop new self-service creations without disabling normal access to existing companies. Requests rejected by limits do not create placeholder companies or enter an approval queue. Dependency/verification/control failures must fail closed.

Audit successful company creation and initial administrator appointment in the same transaction as the business change. Log rejected attempts only with allowlisted operational metadata and bounded volume. Do not log passwords, tokens, raw invitation links, request bodies, or unnecessary email/name data. Avoid a new analytics service; synthetic/manual journey evidence is sufficient for this PoC.

Creating a company does not authorize confidential/regulated data, approve AI-provider processing, or establish legal/domain ownership. Existing customer approval and real-data gates still apply.

## 8. Experience and accessibility

Use the existing React/Fluent UI application, shared visual language, and clear company context. The primary flow is a short form followed by a useful setup page, not a new marketing site, separate brand, or mandatory multi-screen tutorial.

All actions must work with keyboard and assistive technology. Provide associated labels, field errors, appropriate focus after navigation/errors, and announced pending/success states. Preserve usability on narrow screens and at zoom; do not rely on color alone.

Use distinct language for identity registration, company creation, invitation acceptance, and team grants. Do not call a registered identity a company member before a membership exists, or describe an administrator as able to see all boards. Make optional setup actions visibly skippable.

## 9. Acceptance criteria

These are required release evidence, not claims of passing tests.

| ID | Scenario | Pass condition |
| --- | --- | --- |
| SO-01 | New eligible user | Completes the accepted provider registration/sign-in flow, then creates an active company without an operator |
| SO-02 | Existing member creates another company | Receives administrator status only in the new company; existing memberships, roles, and data are unchanged |
| SO-03 | Initial state | Exactly one company and active initial administrator membership exist for the accepted attempt; no automatic team/content/integration grants |
| SO-04 | Company admin versus team access | Creator cannot read team content without an explicit team grant, including through REST/MCP |
| SO-05 | First team | An explicit initial manager is required; team, default board, and grant succeed or fail together |
| SO-06 | Invited employee | Authentication returns to invitation acceptance; no company is created and no team access is inferred |
| SO-07 | Anonymous caller or unusable provider profile | Creation is denied before any company state is committed; display-name fallback cannot supply the provider-bound address |
| SO-08 | Task-token or MCP caller | Cannot create companies or appoint administrators using delegated task capabilities |
| SO-09 | Forged creator, role, or company | Request cannot appoint a different user, adopt an existing company, or obtain extra privileges |
| SO-10 | Duplicate names and shared email domain | Separate companies remain isolated; neither name nor domain joins, claims, or reveals another company |
| SO-11 | Double-click, retry, and parallel replay | One creation attempt produces one company; retries return its result; changed content conflicts and another user cannot replay it |
| SO-12 | Database/audit failure | No partial company/administrator/success audit is committed; a safe explicit error is shown |
| SO-13 | Response lost after commit | Same-attempt retry or reconciliation finds the committed company without duplication |
| SO-14 | Request controls and parallel attempts | No per-user/environment company-count cap; configured request-rate protection and duplicate-submission isolation hold under concurrency; rejected attempts create nothing |
| SO-15 | Optional setup skipped or interrupted | Company remains active; return uses current permissions and state without recreating resources or restoring revoked grants |
| SO-16 | Session expiry, sign-out, identity/company switch | No stale submission, cached cross-company content, or cross-user onboarding result is accepted |
| SO-17 | Browser request forgery | Company creation requires valid session-bound CSRF proof |
| SO-18 | Operator-led onboarding | Existing controlled creation/appointment remains available and preserves tenancy, explicit grants, and auditing |
| SO-19 | Administrator recovery boundary | Ordinary access changes cannot remove the last active admin; exceptional recovery is controlled and audited |
| SO-20 | Integrations and data policy | Client approval stays off; no connection is created and synthetic-data restrictions remain visible |
| SO-21 | UI and error states | Keyboard/narrow-screen journeys succeed; validation, empty, denied, throttled, and dependency failures remain distinguishable |

## 10. Success measures

Demonstrate a synthetic user with no memberships creating a company and reaching its administrator setup page without operator intervention. Separately demonstrate first-team creation with an explicit manager grant, employee invitation/acceptance, and a permitted task operation.

Record company-creation completion separately from first-board, first-invitation-accepted, and first-task milestones. A form submission or successful identity sign-in alone is not completion. Use the acceptance matrix to establish isolation, retry behavior, and failure handling rather than inferring correctness from a happy-path screen.

Retain the parent product's responsiveness targets and existing deferral of the formal full-dataset benchmark. No conversion-rate, completion-time, production-capacity, or compliance claim is made by this PRD.

## 11. Dependencies and decisions before release

| Gate | Required resolution |
| --- | --- |
| Identity | Preserve the accepted External ID baseline and provider-bound address; verify safe return to creation versus invitation acceptance, without a new MFA/provider-policy prerequisite |
| Domain/isolation foundation | Executable Company/User/membership schema, restricted creation/discovery operations, RLS/context behavior, and transactional audit |
| Technical contracts | Document the browser-only create operation, caller-bound pre-company retry contract/retention, validation, errors, and bounded attempt-result discovery |
| Request policy | No company-count quotas; configure request-rate/concurrency protection, safe errors, and the self-service rollout switch |
| Product defaults | Review initial-admin appointment, name bound, optional setup behavior, and last-administrator protection/recovery |
| Existing setup flows | Team/board/grant creation, invitations, company switcher, directory/groups, and permission-aware navigation work under the existing model |
| Release evidence | Demonstrate SO-01 through SO-21 and the applicable existing safety/identity/client gates; no change to the all-three MCP compatibility requirement |

The approved implementation includes the domain, isolation, REST/MCP, and browser flows. Availability is controlled by `Application__SelfServiceEnabled`, now explicitly `true` in the hosted PoC after smoke checks and a real-SQL switch/retry regression. Code, configuration availability, and automated checks are not authenticated browser or native-client acceptance. Current evidence and remaining sign-off are in implementation specification section 13.4. This PRD does not authorize identity-policy changes, infrastructure expansion, or real-data use.
