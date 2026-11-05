# Step 9B.218 — Controlled CI activation procedure

## Status

Procedure only. The Step 9B.219 pull-request candidate is under
`.github/workflows/v0001-postgres-rehearsal.yml`; both jobs retain their
unconditional `false` gates. This document does not authorize removal of a
gate, dispatch, PostgreSQL execution, or production migration.

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

## Reviewed merge and activation

1. Review the Step 9B.219 pull request that places
   `step9b216-v0001-postgres-rehearsal.workflow.yml` at
   `.github/workflows/v0001-postgres-rehearsal.yml`. Preserve both disabled
   `false &&` job conditions, the dispatch-only trigger, protected Environment,
   `main` and reviewed-commit checks, read-only permissions, pinned image and
   action references, and absence of ports, volumes, endpoint inputs, and
   production credentials. Review the complete diff and obtain the
   independently required branch approval. Do not dispatch this inert version.
2. Obtain a separate explicit authorization to remove the two `false &&`
   clauses. Make only that activation change in a separately reviewed pull
   request. Preserve all remaining conditions and controls. Do not combine
   this with unrelated code or configuration changes.
3. After the activation pull request is merged to `main`, record the full
   40-character SHA of the exact resulting `main` commit containing the
   reviewed workflow and harness. Verify it is the intended commit and still
   contains the reviewed V0001 SQL and manifest hashes before dispatch. This
   SHA is the `reviewed_commit` input; select `main` as the dispatch branch.
   This verification is not authorization to dispatch.
4. Before any dispatch, obtain separate, run-specific authorization that
   explicitly covers starting the offline validation job and, if validation
   succeeds, requesting the rehearsal Environment deployment. A maintainer
   then dispatches with that exact SHA. Do not dispatch from another branch or
   use a different SHA. The offline validation job must pass first.
   `RayMCI` (not the initiator) independently checks the run, selected branch,
   commit SHA, workflow diff, and validation result, then separately approves
   the protected Environment. Environment approval is an additional gate, not
   a substitute for run-specific authorization. If any detail is unexpected,
   reject the Environment deployment and stop.
5. Before approval, verify from the reviewed workflow that the rehearsal job
   uses `ubuntu-24.04`, the pinned Node job image and PostgreSQL 17.11 service
   image, fixed service alias `postgres`, no published ports or persistent
   volumes, and only the synthetic bootstrap password. The harness must use
   its fixed database and role names; it must not accept an endpoint,
   connection-string, or SQL-path override. Do not add credentials or change
   the service target to address a failure.

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
- After the one authorized run completes, disable further rehearsal by
  reverting the activation-only change through a reviewed pull request, or
  have an authorized administrator disable the workflow. Verify the workflow
  is no longer dispatchable before closing the review. Do not rerun without a
  new explicit authorization.

## Limitations

GitHub-hosted runners do not provide a complete outbound-network sandbox.
Pinned dependencies, read-only tokens, no configured secrets, the protected
Environment, and a disposable service reduce risk but do not establish
independent network isolation or prove publisher provenance. A successful
rehearsal is evidence only for this synthetic rollback test. It does not
authorize or validate a production migration.
