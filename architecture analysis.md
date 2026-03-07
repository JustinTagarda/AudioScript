# Codex Instructions: Project Assessment, Architecture Planning, Refactoring, and Improvement Preparation

Use this document as the operating instruction for assessing an existing project, understanding its current state, producing architecture planning outputs, and preparing safe, implementation-ready follow-up work.

---

## Role

You are working on an existing production codebase.

Your responsibilities are to:

1. understand the project as it currently exists,
2. assess its architecture and implementation quality,
3. identify risks, technical debt, and improvement opportunities,
4. propose a practical refactoring and improvement plan,
5. prepare future implementation work without guessing or inventing missing details.

Work from the actual repository content and any provided project documentation. Do not infer hidden systems, files, APIs, schemas, or behavior that are not present in the supplied materials.

---

## Primary Objective

Build a complete, accurate picture of the project’s current state so that architecture assessment, planning, refactoring strategy, and future implementation work are grounded in the real codebase.

Your output must help answer:

- what the system currently does,
- how it is structured,
- where responsibilities live,
- what architectural patterns are being used,
- what the main pain points and risks are,
- what should be improved first,
- how to safely evolve the codebase.

---

## Non-Negotiable Rules

- Inspect before concluding.
- Use the repository as the source of truth.
- Do not guess missing implementation details.
- Do not invent files, endpoints, schema fields, services, or architecture layers.
- Preserve the distinction between current state and proposed future state.
- Call out uncertainty explicitly whenever information is missing.
- Prefer representative evidence from real files over generic assumptions.
- Keep recommendations aligned with the existing stack, constraints, and patterns unless there is a clear reason to change them.
- Avoid recommending large refactors without explaining migration risk, dependency impact, and rollout order.
- Do not propose changes that require rewriting unrelated areas unless clearly justified.

---

## Scope of Work

Perform all of the following.

### 1. Project Discovery

Understand the project at a high level.

Determine:

- project purpose,
- major features,
- business/domain boundaries,
- main user flows,
- external systems and integrations,
- deployment shape,
- technology stack and versions where available.

### 2. Repository Mapping

Map the repository structure and explain the role of major directories and key files.

Identify:

- application entry points,
- API/controller layers,
- service/domain layers,
- data access layers,
- shared utility layers,
- UI/component layers,
- test locations,
- configuration and infrastructure files,
- schema/migration files,
- build and deployment files.

### 3. Architecture Assessment

Assess the architecture as implemented today.

Evaluate:

- layer boundaries,
- separation of concerns,
- coupling and cohesion,
- consistency of patterns,
- data flow,
- state management,
- error handling,
- validation approach,
- type safety,
- configuration management,
- observability/logging,
- performance hotspots visible from code,
- scalability constraints,
- security-sensitive areas apparent from the implementation.

### 4. Current State Summary

Produce a concise but complete summary of the current system design.

Include:

- architecture style,
- request/response flow,
- domain organization,
- important cross-cutting concerns,
- dependency hotspots,
- areas of duplication,
- fragile modules,
- missing abstractions,
- strong areas worth preserving.

### 5. Technical Debt Review

Identify debt in categories such as:

- structural debt,
- code duplication,
- unclear ownership,
- mixed concerns,
- weak abstractions,
- inconsistent naming,
- inconsistent error handling,
- test gaps,
- dead or obsolete code,
- outdated dependencies,
- migration or schema debt,
- manual or brittle integration points.

For each important debt item, explain:

- what it is,
- where it appears,
- why it matters,
- its likely impact,
- its remediation priority.

### 6. Refactoring Opportunities

Recommend refactors that are realistic and safe.

For each recommendation, describe:

- the problem,
- the target design,
- affected areas,
- expected benefit,
- migration risk,
- sequencing and prerequisites,
- whether it is low, medium, or high risk.

Prefer incremental improvements over broad rewrites unless the current design makes incremental change impractical.

### 7. Improvement Planning

Propose improvements for the next stage of development.

Include:

- maintainability improvements,
- performance improvements,
- developer experience improvements,
- test strategy improvements,
- architecture improvements,
- reliability improvements,
- security hardening opportunities,
- modernization opportunities that fit the current project.

### 8. Implementation Preparation

Prepare future implementation work by turning findings into actionable handoff material.

Create:

- a prioritized roadmap,
- implementation phases,
- per-phase goals,
- likely touched areas/files,
- dependency and migration concerns,
- acceptance criteria ideas,
- risks and blockers,
- information gaps that must be resolved before coding.

---

## Required Inputs to Review

When available, inspect these first:

### Repository and Build Context

- `package.json` or equivalent manifest
- lockfile
- TypeScript, Babel, ESLint, Prettier, or equivalent config
- workspace/monorepo config
- environment example files
- Docker and compose files
- CI/CD configuration
- README and architecture docs

### Application Structure

- app entrypoints
- server bootstrap files
- route/controller files
- representative page/component files
- service/domain modules
- utility/shared modules
- middleware
- background job or worker files

### Data Layer

- schema definitions
- ORM models
- migrations
- database access layer
- repositories or query modules

### Contract Layer

- API route definitions
- request/response schemas
- validation schemas
- DTOs and shared types
- external API client wrappers

### Quality and Operations

- tests
- logging/monitoring setup
- feature flags
- auth and permission logic
- caching and queue integrations

If some of these are missing, state that clearly and continue based only on what is available.

---

## Method

Follow this order.

### Phase 1: Orient

1. Read the top-level project metadata and documentation.
2. Identify the stack and execution model.
3. Identify the main modules and system boundaries.
4. Summarize the apparent architecture before diving deeper.

### Phase 2: Inspect Representative Files

Inspect the most important files that reveal project patterns.

Look for:

- how requests enter the system,
- where domain logic lives,
- how data is validated,
- how data is persisted,
- how errors are handled,
- how shared logic is reused,
- how tests mirror behavior.

### Phase 3: Map Dependencies and Flows

Trace representative flows such as:

- UI to API to service to database,
- external integration to adapter to domain logic,
- form submission to validation to persistence,
- auth or permission checks through the stack.

### Phase 4: Assess Quality

Evaluate what is working well and what is risky.

Prioritize findings that affect:

- correctness,
- maintainability,
- scaling,
- development speed,
- change safety.

### Phase 5: Plan Improvements

Convert findings into a staged improvement plan.

Prefer:

- high-impact, low-risk improvements first,
- refactors that reduce future implementation cost,
- changes that strengthen system boundaries,
- improvements that can be validated incrementally.

---

## Assessment Framework

Use the following framework when reasoning about the system.

### A. Architecture

Assess:

- is the architecture explicit or accidental,
- are module boundaries clear,
- are responsibilities well placed,
- is domain logic isolated from framework details,
- are infrastructure concerns leaking into business logic,
- are UI and data concerns overly coupled,
- are there strong patterns worth standardizing.

### B. Code Organization

Assess:

- directory clarity,
- naming consistency,
- discoverability,
- dependency direction,
- duplication,
- presence of god modules or god services,
- whether abstractions are meaningful or unnecessary.

### C. Data and Contracts

Assess:

- schema quality,
- validation completeness,
- type consistency across layers,
- request/response shape consistency,
- migration safety,
- data ownership clarity.

### D. Reliability

Assess:

- error handling patterns,
- retry/idempotency behavior where relevant,
- fallback behavior,
- configuration safety,
- logging and debuggability,
- operational visibility.

### E. Testability

Assess:

- existing test coverage by layer,
- confidence level for refactors,
- missing unit/integration/e2e coverage,
- brittleness of current testing strategy,
- fast feedback quality.

### F. Scalability and Performance

Assess only from evidence visible in the codebase.

Look for:

- heavy queries,
- repeated data fetching,
- missing caching opportunities,
- synchronous bottlenecks,
- coupling that blocks horizontal scaling,
- oversized modules that limit team scaling.

### G. Security and Safety

Assess visible concerns such as:

- auth boundaries,
- permission checks,
- secrets handling,
- validation gaps,
- unsafe input handling,
- risky logging,
- insecure defaults.

Do not claim vulnerabilities unless they are supported by actual evidence from the code or configuration.

---

## Output Requirements

Return results in the exact structure below.

# 1. Executive Summary

Provide a compact overview of:

- what the system appears to be,
- its current architectural style,
- its main strengths,
- its main weaknesses,
- the most important next steps.

# 2. Project Snapshot

Document:

- purpose,
- stack,
- main modules,
- major flows,
- external dependencies,
- deployment context if visible.

# 3. Current Architecture

Describe the architecture as implemented today.

Include:

- system layers,
- module boundaries,
- request/data flow,
- where business logic lives,
- where infrastructure concerns live,
- cross-cutting concerns.

# 4. Repository Map

Provide a concise tree-style or sectioned explanation of major directories and important files.

# 5. Findings

Group findings by severity or category.

For each finding provide:

- title,
- evidence,
- impact,
- recommendation.

# 6. Technical Debt List

Provide a prioritized list of debt items with rationale.

# 7. Refactoring Recommendations

For each recommendation include:

- goal,
- suggested target structure,
- files/areas likely involved,
- expected benefit,
- risk level,
- suggested rollout order.

# 8. Improvement Roadmap

Provide phased recommendations such as:

- Phase 1: quick wins,
- Phase 2: structural cleanup,
- Phase 3: deeper architecture improvements,
- Phase 4: modernization or scale preparation.

# 9. Implementation Handoff Notes

Convert the assessment into practical next-step instructions for future coding work.

Include:

- concrete task candidates,
- likely touched files or areas,
- validation targets,
- test expectations,
- assumptions that must not be made,
- missing information that must be gathered first.

# 10. Open Questions and Uncertainties

List anything that cannot be concluded safely from the available materials.

---

## Quality Bar

Your work must be:

- evidence-based,
- explicit about uncertainty,
- grounded in the current codebase,
- oriented toward practical implementation,
- safe for production-minded planning,
- detailed enough that future implementation prompts can be written from it.

---

## Do Not Do These

- Do not provide generic best-practice advice detached from the repository.
- Do not recommend rewriting the project without strong justification.
- Do not collapse current-state analysis and future-state proposals into one unclear summary.
- Do not hide uncertainty.
- Do not assume missing files behave a certain way.
- Do not propose new architecture layers unless you explain why the current structure fails and how migration should happen.

---

## Final Instruction

Base every conclusion on inspected project materials.

Where evidence is incomplete, say exactly what is missing and what should be reviewed next before implementation or major refactoring begins.

