# Step 9B.218 — Controlled CI activation procedure

## Status

Procedure only. The merged workflow is
`.github/workflows/v0001-postgres-rehearsal.yml`. Activation is staged: the
first change may enable offline validation only, while the rehearsal job stays
unconditionally disabled. Neither this procedure nor validation activation
authorizes a dispatch, PostgreSQL execution, or production migration.

## Pre-activation administrator checks

Immediately before any separately authorized activation review, capture
read-only evidence that:

- The `main` branch ruleset is active, has no bypass actors, and requires an
  approving pull-request review for the workflow, rehearsal, validator,
  migration, and manifest changes.
- The protected `v0001-postgres-rehearsal` Environment still requires
  independent approval from `RayMCI`, prevents self-review, disables
  administrator bypass, and is restricted to `main`.
- Repository, organization, and Environment secrets and variables remain
  empty, and no secret-bearing configuration is inherited by the job.
- Actions policies retain read-only default token permissions, full-SHA action
  pinning, and GitHub-hosted-only runner use; no repository self-hosted runner
  is available to this workflow.

Do not disclose secret values. If any control cannot be verified or differs
from the expected configuration, stop; do not activate or dispatch.

## Staged activation

1. **Validation-only pull request:** Obtain separate explicit authorization to
   prepare a validation-only activation. Remove only the `false &&` from the
   `validate` job condition in
   `.github/workflows/v0001-postgres-rehearsal.yml`. Preserve its `main`
   restriction and exact `inputs.reviewed_commit == github.sha` check. Leave
   the rehearsal job condition, including its leading `false &&`, completely
   unchanged. The validation job must remain limited to the offline migration
   validation test and must not reference an Environment, service container,
   database, Docker, or migration execution. Include the focused offline safety
   tests, review the entire diff, and obtain independent pull-request approval.
   Do not combine this change with rehearsal activation or unrelated changes.
2. **Validation dispatch authorization:** After the validation-only PR is
   independently approved and merged to `main`, record the full 40-character
   SHA of the intended `main` commit. Verify the reviewed workflow and the
   fixed V0001 SQL and manifest hashes at that commit. Before dispatching,
   obtain separate, explicit, run-specific authorization for the offline
   validation run. Select `main` and provide that exact SHA. This authorization
   covers validation only; it does not authorize a rehearsal or database job.
3. **Review validation results:** Verify that the run used the intended
   `main` commit and that the offline validation job passed. Review its logs
   for unexpected steps, targets, credentials, or database/network activity.
   Preserve the run URL, commit SHA, result, and relevant logs. Stop if anything
   is unexpected; do not enable or dispatch rehearsal to investigate a failure.
4. **Separate rehearsal activation review:** Only after reviewing successful
   validation results, obtain new explicit authorization to prepare a distinct
   rehearsal-activation PR. That PR may remove only the rehearsal job's
   `false &&` gate; keep the validation condition, branch and commit checks,
   `needs.validate` success requirement, Environment, permissions, pinned
   dependencies, and fixed disposable target unchanged. Obtain independent
   pull-request approval. This PR and its approval do not authorize a workflow
   dispatch or database execution.
5. **Separate rehearsal run authorization and Environment approval:** After
   the rehearsal-activation PR is approved and merged, recheck all administrator
   controls above and identify the exact reviewed `main` commit and artifact
   hashes. Before any dispatch, obtain separate, run-specific authorization
   explicitly covering both offline validation and, if it passes, a request
   for the rehearsal Environment deployment. Dispatch only from `main` with
   the exact commit SHA. Validation must pass first. `RayMCI`, not the
   initiator, independently reviews the run, branch, SHA, workflow and
   validation result, then separately approves the protected Environment.
   Environment approval is an additional gate, not dispatch authorization. If
   any detail is unexpected, reject the Environment deployment and stop.
6. **Pre-approval configuration check:** Confirm the rehearsal job uses
   `ubuntu-24.04`, the pinned Node job image and PostgreSQL 17.11 service image,
   fixed service alias `postgres`, no published ports or persistent volumes,
   and only the synthetic bootstrap password. The harness must use fixed
   database and role names and accept no endpoint, connection-string, or
   SQL-path override. Do not add credentials or change the service target to
   address a failure.

## First-run observation and stop conditions

- Observe the run live. Confirm it runs from `main` at the recorded SHA and
  compare the run's reviewed workflow and harness with the fixed PostgreSQL
  service alias, database target, and image digests. Verify that no target
  override, published port, persistent volume, or production credential was
  introduced. The harness must report only its rollback-and-verified success
  marker after checking PostgreSQL 17, the fixed rehearsal database and roles,
  SCRAM host authentication, catalog expectations, and synthetic data
  preservation.
- These configuration and log checks do not prove exclusive network
  reachability, independent database-instance attestation, or network
  isolation. Ordinary GitHub-hosted-runner logs cannot establish those facts;
  outbound Internet access remains possible.
- Do not infer database isolation merely from the Environment approval. If the
  workflow or logs show an unexpected runner, image, target, credential,
  service, or result, cancel the run if possible, reject further approvals,
  preserve its logs, and do not retry against that target.
- On success, preserve the workflow run URL, commit SHA, approver, validation
  result, rehearsal result, and logs as review evidence. GitHub Actions is
  responsible for removing the job-scoped service when the job ends; the
  workflow does not provide an independent host-level container-removal
  attestation. Do not claim cleanup was independently verified from ordinary
  step logs.
- After the one authorized rehearsal completes, disable further rehearsal by
  reverting the rehearsal activation through a reviewed pull request, or have
  an authorized administrator disable the workflow. Verify the rehearsal
  cannot run again before closing the review. Do not rerun without new
  explicit authorization.

## Limitations

GitHub-hosted runners do not provide a complete outbound-network sandbox.
Pinned dependencies, read-only tokens, no configured secrets, the protected
Environment, and a disposable service reduce risk but do not establish
independent network isolation or prove publisher provenance. A successful
rehearsal is evidence only for this synthetic rollback test. It does not
authorize or validate a production migration.
