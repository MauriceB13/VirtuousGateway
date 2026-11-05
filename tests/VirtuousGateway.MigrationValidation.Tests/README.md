# V0001 offline migration validation

This standalone test project validates the reviewed V0001 SQL and manifest without
connecting to PostgreSQL, invoking Docker, or loading the dormant executor. It is
intentionally not included in the solution or application test project, so ordinary
application tests do not discover or execute migration validation.

Run it explicitly with:

```powershell
dotnet test tests\VirtuousGateway.MigrationValidation.Tests\VirtuousGateway.MigrationValidation.Tests.csproj --configuration Release
```

The suite pins the exact SQL and manifest SHA-256 values, rejects invalid SQL
encoding/line endings, checks the manifest's unexecuted/nonautomatic state, and
compares parsed table, column, constraint, index, and foreign-key inventories with
an independent expected-catalog fixture. Mutation tests cover truncation, changed
definitions, missing/duplicated/renamed objects, and altered manifest requirements.

These are static artifact and model checks, not PostgreSQL parser, catalog, constraint,
transaction, rollback, or power-loss evidence. The SQL SHA-256 pin proves only that
the checked-in bytes match the reviewed digest embedded in this test source; it is
not an external approval or trusted build attestation. The suite does not grant
execution permission and does not modify the SQL or manifest.

## Future ephemeral CI rehearsal prerequisites

Before any real-engine test, independently establish a protected CI workflow and
runner isolated per job; pin and authenticate the PostgreSQL 17 image by digest;
provide synthetic-only prerequisites and credentials scoped to a disposable
instance; deny production network routes, production secrets, and persistent/shared
database volumes; and arrange cleanup by discarding only the job-owned instance.
Untrusted pull-request code must not receive migration credentials or workflow
approval privileges. A migration rehearsal needs its own explicit authorization,
reviewed executor/test harness, fresh one-job database, and clearly separated
rollback-only versus committing intent. Failed or ambiguous runs stop and discard
the instance; they are not retried against the same target.

The CI control plane, runner isolation, image provenance, network policy, secret
handling, and database behavior have not been independently established by this
offline suite. No workflow, database service, or live execution is configured here.
