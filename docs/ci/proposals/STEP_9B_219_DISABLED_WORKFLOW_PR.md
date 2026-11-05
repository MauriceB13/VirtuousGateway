# Step 9B.219 — Disabled workflow pull-request handoff

## Status

Local pull-request preparation only. The workflow candidate is under
`.github/workflows/v0001-postgres-rehearsal.yml`; both validation and rehearsal
jobs retain unconditional `false` gates. Do not dispatch, remove a gate, or
interpret this handoff as authorization to run Actions, PostgreSQL, or a
migration.

## Proposed pull request

**Title:** Add disabled PostgreSQL 17 migration rehearsal workflow

**Description:**

> Adds the reviewed Step 9B.216 PostgreSQL 17.11 rollback rehearsal as a
> manually dispatched GitHub Actions workflow, while retaining unconditional
> disabled gates on both jobs. Includes the dedicated CI rehearsal project,
> offline safety tests, artifact validation, and review/activation
> documentation.
>
> No GitHub Actions job, Docker container, PostgreSQL connection, or migration
> was executed for this change. This pull request does not authorize removing
> either gate or dispatching the workflow. Any later activation and first run
> require separate review and explicit authorization.

## Intended changed-file scope

- `.gitattributes` (preserves LF byte hashes for the pinned V0001 artifacts on
  Windows checkouts)
- `.github/workflows/v0001-postgres-rehearsal.yml`
- `database/postgresql/migrations/V0001__add_accounting_operation_receipt_evidence.sql`
- `database/postgresql/manifests/V0001.json`
- `tools/VirtuousGateway.CiMigrationRehearsal/`
- `tests/VirtuousGateway.MigrationRehearsal.Tests/`
- `tests/VirtuousGateway.MigrationValidation.Tests/`
- `tools/VirtuousGateway.MigrationRehearsal/` (existing disabled harness and
  fail-closed entry point referenced by the regression tests)
- `docs/ci/proposals/step9b210-v0001-postgres-rehearsal.workflow.yml` (legacy
  workflow proposal checked by the regression tests)
- `docs/ci/proposals/STEP_9B_216_CI_REHEARSAL.md`
- `docs/ci/proposals/step9b216-v0001-postgres-rehearsal.workflow.yml`
- `docs/ci/proposals/STEP_9B_218_ACTIVATION_PROCEDURE.md`
- `docs/ci/proposals/STEP_9B_219_DISABLED_WORKFLOW_PR.md`

The Step 9B.210 workflow proposal and disabled harness referenced by the tests
must also be retained. The V0001 SQL and manifest are included as exact
reviewed artifacts and must remain byte-for-byte unchanged. Other worktree
changes are unrelated and are not part of this intended pull-request scope.

## Review checklist

- [ ] Both jobs still contain unconditional `false &&` conditions.
- [ ] Trigger is `workflow_dispatch` only; no `push` or `pull_request` trigger.
- [ ] Runner is GitHub-hosted Linux; rehearsal job requires the
      `v0001-postgres-rehearsal` Environment.
- [ ] `main` branch and exact reviewed-commit conditions remain intact.
- [ ] Workflow and job permissions remain `contents: read`; no OIDC or write
      permission is added.
- [ ] All actions and container images use the reviewed full SHA/digest pins.
- [ ] No arbitrary database endpoint, connection string, SQL path, port
      publication, persistent volume, production credential, or production
      data is introduced.
- [ ] Service uses PostgreSQL 17.11 with SCRAM-SHA-256 and only synthetic
      database content.
- [ ] Harness verifies fixed V0001 SQL and manifest hashes before database
      access; bootstrap and migration roles remain separate and least
      privileged.
- [ ] Migration remains transactional, checks catalog and synthetic data,
      rolls back, and verifies migration objects are absent.
- [ ] Existing disabled harness remains disabled; historical archive remains
      untouched and unexecuted.
- [ ] Administrator Actions settings, runner policies, secret inventory, and
      branch ruleset bypass controls are verified before any separately
      authorized activation review.

## Offline validation evidence

To be filled with results for the proposed change:

- Rehearsal safety tests: 24 passed.
- Migration artifact validation tests: 24 passed.
- CI rehearsal project build: succeeded with zero warnings and zero errors.
- `git diff --check`: passed.
- V0001 SQL SHA-256:
  `c1eb364eaf13a1e5ee07bd04001c3bda936aa46e81d4f47e54ad52fe0a180227`.
- V0001 manifest SHA-256:
  `efd2413cf4353c8eed9e1904919d34f2f0229c7abc401cbd25616b67b6322682`.

No Actions workflow, container, database, or migration was executed.
