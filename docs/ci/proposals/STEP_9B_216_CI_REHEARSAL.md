# Step 9B.216 — disabled PostgreSQL 17.11 rehearsal proposal

## Status: offline implementation only; activation and execution are not authorized

The source proposal is
[`step9b216-v0001-postgres-rehearsal.workflow.yml`](step9b216-v0001-postgres-rehearsal.workflow.yml);
the Step 9B.219 pull-request candidate is
[`v0001-postgres-rehearsal.yml`](../../../.github/workflows/v0001-postgres-rehearsal.yml).
The candidate is dispatch-only and retains two unconditional `false` job gates,
so no job starts. Rehearsal execution remains unauthorized.
The existing disabled `VirtuousGateway.MigrationRehearsal` entry point remains
unchanged. The separate `VirtuousGateway.CiMigrationRehearsal` project is
proposed only for the gated disposable service design; do not activate or run it
outside separately authorized CI review.

## Workflow boundary

- One `workflow_dispatch` input is accepted: the reviewed commit SHA. The job
  requires `main` and exact equality with the dispatched `github.sha`; there
  are no endpoint, database, SQL path, role, image, or connection-string inputs.
- The rehearsal job references the pre-existing
  `v0001-postgres-rehearsal` Environment. Its current reviewer, self-review,
  administrator-bypass, and `main` branch controls were checked via the
  read-only GitHub API. Actions settings and secret inventories still require
  administrator evidence.
- The runner is GitHub-hosted `ubuntu-24.04`. The Node 20 Bookworm job
  container uses the verified Linux/amd64 official-image manifest digest. The
  full Debian Node image derives from `buildpack-deps`, providing Node 20,
  `sh`, Git, and common SDK prerequisites. The pinned `actions/checkout` and
  `actions/setup-dotnet` revisions declare the Node.js 24 JavaScript runtime,
  which is supplied by the GitHub Actions runner. The Node 20 job container
  provides the shell environment; its Node.js 20 preflight check validates the
  container's `node` executable, not the JavaScript action runtime. The
  rehearsal job has not been executed, so this runtime behavior has not been
  empirically confirmed in that job.
- The service is official PostgreSQL 17.11 Bookworm pinned to the verified
  Linux/amd64 manifest digest. GitHub creates a fresh service container for
  each job and connects it to the job-container bridge network; the proposal
  publishes no database ports and mounts no persistent volumes.
- Host authentication is configured for SCRAM-SHA-256. The synthetic bootstrap
  password is intentionally non-secret and usable only in the throwaway job.
  The migrator password is generated in-process for each run. No production
  credentials, data, endpoints, cloud permissions, OIDC, or shared database
  resources are included.
- The standalone offline-validation job must succeed before the rehearsal job
  can start and provision PostgreSQL. The rehearsal process independently
  verifies the exact V0001 SQL and manifest hashes and the manifest
  execution-disabled fields before opening a database connection. A validation
  or rehearsal error fails the job; the same service must never be reused or
  retried.

GitHub-hosted runners do not provide a complete outbound-network sandbox.
Workflow permissions and the absence of production credentials reduce impact
but do not prevent arbitrary Internet egress or dependency supply-chain risk.
Image digests pin registry content, not the trustworthiness of publisher
provenance. The database boundary depends on GitHub's runner/service lifecycle
and workflow control plane; runtime environment checks in the harness prevent
accidental use but are not independent attestation.

## Database privilege and transaction model

The bootstrap login is the initial superuser of the fresh service only. It
confirms PostgreSQL 17, UTF8, fixed database/role, SCRAM host rules, and an empty
`public` schema; then it creates one synthetic prerequisite table and row, a
fresh random-password migration login with a SCRAM-SHA-256 verifier, and only
the schema/table grants needed for V0001. Database-level privileges are revoked
from `PUBLIC` on the rehearsal database only; the migrator receives `CONNECT`
on that database. No ACL changes are made to `postgres` or `template1`. The
migrator is checked for lack of database `CREATE` and `TEMP` on the rehearsal
database, and lack of database `CREATE` on other databases. PostgreSQL's
default `PUBLIC` `TEMP` privilege on other databases is left unchanged;
temporary objects are session-scoped and do not persist. The role is also
checked for superuser, `CREATEDB`, `CREATEROLE`, replication, `BYPASSRLS`, and
role memberships. The migration role is cluster-scoped by PostgreSQL, but
exists only in the job-disposable service instance.

The migrator applies the exact hash-verified SQL in one read-committed
transaction. The harness compares the migration tables, constraints, and
indexes with the reviewed manifest and confirms that synthetic prerequisite
data is unchanged. It rolls back and verifies migration objects are absent and
the prerequisite row remains. Any failure exits without reconnect/retry; GitHub
disposes of the job and service container.

The roles and synthetic objects exist only in the disposable service cluster
and are removed with it. PostgreSQL roles are cluster-wide by nature; therefore
the design is safe only while that cluster is genuinely job-exclusive and not
backed by persistence or shared infrastructure. The harness does not perform
independent target attestation and must not be pointed at another endpoint.

## Immutable dependency references

- PostgreSQL 17.11 official `library/postgres:17.11-bookworm`, Linux/amd64:
  `sha256:73bedc9eaa0121b92b9b26bb52e5141b3f6f37479c7ef913c0160455763ca80a`.
  OCI index: `sha256:665dc7b37ac52a17e298511414c21d23ffa4e6a6ef6225b3e5d59c7afb35e994`.
- Official `node:20-bookworm`, Linux/amd64:
  `sha256:cacf10e99285cbbc891452e31249c1b5ec3ba225f40028fae946b75aeaf1b66a`.
  OCI index: `sha256:8f693eaa7e0a8e71560c9a82b55fd54c2ae920a2ba5d2cde28bac7d1c01c9ba5`.
- `actions/checkout` v4.2.2:
  `11bd71901bbe5b1630ceea73d27597364c9af683`.
- `actions/setup-dotnet` v4.3.1:
  `67a3573c9a986a3f9c594539f4ab511d57bb3ce9`.
- .NET SDK: exact version `10.0.403`, installed by the pinned setup action.

The OCI digests were read from the registry manifest headers; the checkout and
setup-dotnet SHAs resolve from their GitHub tag references. These immutable
identifiers still require an administrator/reviewer to retain provenance and
platform evidence before any activation review.

## Required administrator evidence before any activation review

1. Confirm the `main` ruleset is active, has the intended bypass list, and
   requires appropriate independent review for workflow, harness, validator,
   migration, and manifest changes. The current rule does not require
   code-owner approval.
2. Verify repository and organization Actions policies: allowed actions,
   full-SHA requirements, GitHub-hosted-only runner policy, external contributor
   approval rules, default token permissions, and PR-approval permissions.
3. Inventory repository, organization, and Environment secrets/variables by
   name and scope without disclosing values; confirm no production/cloud/database
   credential is available to this job and identify who can edit the Environment.
4. Record the approved image/action provenance, .NET SDK release, permitted
   outbound egress assumptions, and log retention/review owners.

No Actions settings or Environment settings were changed. No workflow,
container, database, or migration was executed. This report does not authorize
activation, database rehearsal, or production migration.
