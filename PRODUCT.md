# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Verified users can create companies through self-service onboarding. Employees manage team tasks. Team managers arrange explicit team access. Company administrators manage their company's directory and groups without implicit team-content access.

## Product Purpose

A synthetic-data, multi-company Kanban PoC demonstrating consistent employee-delegated authorization through a browser, REST, and MCP.

## Operating Context

The authoritative product requirements are in [the PRD](kanban-saas-prd.md) and its [self-service company onboarding extension](kanban-saas-self-service-onboarding-prd.md). Architecture and implementation decisions are in the linked technical design and implementation specification. VS Code, GitHub Copilot App, and Claude Code are required MCP clients.

## Capabilities and Constraints

One board per team, with To do, In progress, and Done. Tasks have plain-text content and optional eligible assignees. Company/team context must remain clear. Manual refresh, explicit errors, safe concurrent edits, and immediate permission revocation are important. No realtime service, notifications, billing, attachments, or server-side AI.

Company creation activates immediately with no company-count quota and no implicit team access. Setup is optional. Invitations last seven days. Existing assignments are retained and shown as inactive after contributor access loss. Reactivation needs fresh grants/connections and administrator reappointment. Company administrators may explicitly recover managerless teams without receiving content access.

## Brand Commitments

The user selected Fluent UI React. Preserve the familiar Kanban interaction and the PRD's board structure. No separate product brand, logo, or commercial claims have been supplied.

## Evidence on Hand

Product services, SQL migrations, REST/MCP adapters, generated contracts, the browser UI, and focused automated safety checks are implemented. The existing identity policies remain the accepted baseline. Local automated results, hosted activation checks, interactive product journeys, and actual native-client acceptance are separate evidence; the current matrix is in section 13.4 of the implementation specification. A real native Claude OAuth attempt exposed a resource/scope mismatch before sign-in. The narrow compatibility correction retains the legacy API identifier and adds the canonical HTTPS MCP identifier; scope IDs, client IDs, grants, callbacks, issuer/audience checks, browser sign-in, and Security Defaults are preserved. A cookie-free authorization preflight now reaches the expected sign-in-required boundary, not authenticated acceptance. Synthetic demonstrations must not be represented as customer testimonials, production benchmarks, or compliance approvals.

Company self-service is now explicitly enabled in the hosted PoC for authenticated acceptance. The rollout-switch SQL regression and post-activation hosted smoke checks pass; genuine signed-in browser/API/native-client journeys remain unverified. Availability is not a claim of completed product acceptance.

## Product Principles

- Authorization is server-enforced and shared across interfaces.
- A company administrator has no implicit team-content privilege.
- Preserve user drafts and show conflicts rather than silently overwriting work.
- Keep PoC operation and Azure cost small without weakening isolation.

## Accessibility & Inclusion

Keyboard-accessible task movement must be available alongside drag/drop. Distinguish loading, empty, denied, conflict, and dependency-error states.
