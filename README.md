# VirtuousGateway

VirtuousGateway is a .NET 10 API that provides a secure integration layer between
Virtuous/Make and the Aplos API.

## Configuration

VirtuousGateway uses standard ASP.NET Core configuration. Environment variables
override values in `appsettings.json`.

Nested configuration keys use double underscores (`__`) in environment
variable names.

### Required production secrets

The following values must be supplied securely by the deployment environment.
Do not commit their values to source control.

| Environment variable | Purpose |
| --- | --- |
| `Security__ApiKey` | Bearer API key required for protected VirtuousGateway endpoints |
| `Aplos__ClientId` | Aplos API client identifier |
| `Aplos__PrivateKey` | Base64 PKCS#8 private key used to decrypt Aplos access tokens |

### Transaction posting safety switch

`Aplos__AllowTransactionPosting` controls whether VirtuousGateway may create
transactions in Aplos.

Production deployments should initially use:

`Aplos__AllowTransactionPosting=false`

Enable transaction posting only after deployment configuration and connectivity
have been verified.

### Deployment configuration

Customer-specific configuration must be supplied by the deployment environment.
Tracked configuration does not contain customer-specific Virtuous organization,
Aplos account mapping, or database values.

| Environment variable | Purpose |
| --- | --- |
| `Aplos__BaseUrl` | Aplos API base URL |
| `Virtuous__OrganizationId` | Expected Virtuous organization ID |
| `TransactionMapping__DepositAccountNumber` | Aplos deposit account number |
| `TransactionMapping__IncomeAccountNumber` | Aplos income account number |
| `TransactionMapping__FundId` | Aplos API fund ID |
| `ProcessingLedger__ConnectionString` | PostgreSQL connection string used for the Virtuous gift processing ledger |
| `Gateway__Name` | Gateway name reported by the liveness endpoint |
| `Gateway__Version` | Gateway version reported by the liveness endpoint |

The Virtuous organization ID and all transaction mapping values must be greater
than zero. The gateway validates these values during startup rather than relying
on customer-specific defaults in source control.

## Per-customer deployment

VirtuousGateway uses one application image with isolated runtime configuration for
each customer. Customer-specific credentials, Virtuous organization IDs,
transaction mappings, and PostgreSQL connection strings must not be built into
the image or committed to source control.

`compose.yaml` provides the deployment template. `.env.example` documents the
runtime configuration required for a customer deployment.

To prepare a customer configuration, copy the example file to an ignored
customer-specific file:

```powershell
Copy-Item .env.example .env.customer-name
```

Populate `.env.customer-name` with that customer's configuration and secrets.
Files matching `.env.*` are ignored by Git, except for the tracked
`.env.example`.

Each customer deployment should use a unique Compose project name:

```powershell
docker compose `
    -p aplos-customer-name `
    --env-file .env.customer-name `
    up -d
```

The project name isolates the customer's Compose container and network
resources. Each deployment must also use its own gateway API key, Aplos
credentials, Virtuous organization ID, transaction mapping, and PostgreSQL
database credentials.

If multiple customer deployments run on the same Docker host, assign a unique
`GATEWAY_PORT` to each deployment. Production platforms that provide routing
or customer-specific hostnames may handle external port routing separately.

New customer configurations should leave:

```text
Aplos__AllowTransactionPosting=false
```

until the customer's configuration, accounting mappings, health endpoints, and
integration behavior have been verified. Enabling transaction posting is a
deliberate activation step.

Validate a customer configuration before deployment:

```powershell
docker compose `
    -p aplos-customer-name `
    --env-file .env.customer-name `
    config --quiet
```

The deployment template rejects missing required customer configuration during
Compose interpolation.

## Health endpoints

### `GET /health`

Public liveness endpoint. Confirms that the API process is running.

### `GET /health/ready`

Public readiness endpoint. Confirms that required operational configuration is
present without contacting Aplos or exposing secret values.

A ready gateway returns HTTP `200`.

A gateway missing operational configuration returns HTTP `503`.

A missing `Security__ApiKey` is treated as a fatal security configuration error
rather than a normal readiness failure.

## Runtime data

VirtuousGateway uses PostgreSQL as a durable Virtuous gift processing ledger.

Configure the PostgreSQL connection through the deployment environment:

`ProcessingLedger__ConnectionString=Host=<host>;Port=5432;Database=<database>;Username=<username>;Password=<password>`

The connection string should be supplied as an environment variable or deployment
secret and must not be committed to source control.

The processing ledger persists gift processing state independently of the gateway
container, including completed processing and outcomes that require
reconciliation. This allows the gateway container to be replaced or restarted
without losing its durable processing history.

`.env` files and the local `Secrets` directory are excluded from source control.

## Secret handling

Do not store the gateway API key, Aplos client ID, Aplos private key, decrypted
Aplos access tokens, or other credentials in:

- `appsettings.json`
- `appsettings.Development.json`
- `launchSettings.json`
- `.env` files committed to source control
- application logs

Use the deployment platform's secret-management or environment-variable
facility instead.