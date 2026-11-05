using System.Security.Cryptography;
using System.Text;
using VirtuousGateway.MigrationRehearsal;

namespace VirtuousGateway.MigrationRehearsal.Tests;

public sealed class RehearsalPolicyTests
{
    [Fact]
    public async Task PreviouslyDisabledHarnessStillRefusesExecution()
    {
        Assert.Equal(3, await Program.Main([]));
    }

    [Theory]
    [InlineData("--host=localhost")]
    [InlineData("--database=production")]
    [InlineData("--sql-path=./other.sql")]
    [InlineData("--connection-string=Host=postgres;Database=vg_v0001_rehearsal")]
    public void CiHarnessRejectsAllArguments(string argument)
    {
        Assert.Null(CiPolicy.ValidateExecutionContext([argument], _ => "unused"));
    }

    [Fact]
    public void CiHarnessRequiresReviewedMainDispatchOnLinux()
    {
        var values = ApprovedContext();

        var context = CiPolicy.ValidateExecutionContext([], values.GetValueOrDefault);

        Assert.NotNull(context);
        Assert.Equal(values["VG_BOOTSTRAP_PASSWORD"], context.BootstrapPassword);
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS", "false")]
    [InlineData("GITHUB_EVENT_NAME", "push")]
    [InlineData("GITHUB_REF", "refs/heads/feature")]
    [InlineData("RUNNER_OS", "Windows")]
    [InlineData("VG_REVIEWED_COMMIT", "0000000000000000000000000000000000000000")]
    [InlineData("GITHUB_SHA", "not-a-commit")]
    [InlineData("VG_BOOTSTRAP_PASSWORD", "")]
    public void CiHarnessFailsClosedOnInvalidRuntimeContext(string key, string value)
    {
        var values = ApprovedContext();
        values[key] = value;

        Assert.Null(CiPolicy.ValidateExecutionContext([], values.GetValueOrDefault));
    }

    [Fact]
    public void CiHarnessRejectsMissingRuntimeContext()
    {
        Assert.Null(CiPolicy.ValidateExecutionContext([], _ => null));
    }

    [Fact]
    public void ReviewedArtifactsPassOfflineHashAndManifestValidation()
    {
        var artifacts = CiPolicy.LoadAndValidateArtifacts(
            Path.Combine(AppContext.BaseDirectory, "artifacts", "V0001.sql"),
            Path.Combine(AppContext.BaseDirectory, "artifacts", "V0001.manifest.json"));

        Assert.Equal(3, artifacts.Tables.Count);
        Assert.Equal(42, artifacts.Constraints.Count);
        Assert.Equal(4, artifacts.ExplicitIndexes.Count);
        Assert.Contains("CREATE TABLE public.accounting_operations", artifacts.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("manifest")]
    public void CiHarnessRejectsModifiedArtifactsOffline(string artifact)
    {
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        var sql = File.ReadAllBytes(Path.Combine(artifactDirectory, "V0001.sql"));
        var manifest = File.ReadAllBytes(Path.Combine(artifactDirectory, "V0001.manifest.json"));
        if (artifact == "sql")
        {
            sql = [.. sql, (byte)' '];
        }
        else
        {
            manifest = Encoding.UTF8.GetBytes("{}");
        }

        WithTemporaryArtifacts(sql, manifest, (sqlPath, manifestPath) =>
        {
            Assert.Throws<InvalidDataException>(
                () => CiPolicy.LoadAndValidateArtifacts(sqlPath, manifestPath));
        });
    }

    [Fact]
    public void CiHarnessRejectsTruncatedSqlOffline()
    {
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        var sql = File.ReadAllBytes(Path.Combine(artifactDirectory, "V0001.sql"));
        var manifest = File.ReadAllBytes(Path.Combine(artifactDirectory, "V0001.manifest.json"));

        WithTemporaryArtifacts(sql[..^1], manifest, (sqlPath, manifestPath) =>
        {
            Assert.Throws<InvalidDataException>(
                () => CiPolicy.LoadAndValidateArtifacts(sqlPath, manifestPath));
        });
    }

    [Fact]
    public void ProposalIsOutsideActiveWorkflowsAndUnconditionallyDisabled()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ false &&", workflow, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/main'", workflow, StringComparison.Ordinal);
        Assert.Contains("inputs.reviewed_commit == github.sha", workflow, StringComparison.Ordinal);
        Assert.Contains("validate:", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: validate", workflow, StringComparison.Ordinal);
        Assert.Contains("needs.validate.result == 'success'", workflow, StringComparison.Ordinal);
        Assert.Contains("environment:\n      name: v0001-postgres-rehearsal", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.StartsWith("# PROPOSED ONLY. Keep outside .github/workflows.", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInWorkflowDiffersOnlyByValidationGateFromDisabledProposal()
    {
        var proposal = ReadWorkflow();
        var activeCandidate = ReadActiveWorkflow();
        const string proposalHeader =
            "# PROPOSED ONLY. Keep outside .github/workflows.\n# Do not remove the disabled gate or execute without separate authorization.\n";
        const string activeHeader =
            "# Validation-only activation candidate. Keep rehearsal unconditionally disabled.\n# Do not dispatch validation without separate authorization and review.\n";
        const string disabledValidationCondition =
            "if: ${{ false && github.ref == 'refs/heads/main' && inputs.reviewed_commit == github.sha }}";
        const string enabledValidationCondition =
            "if: ${{ github.ref == 'refs/heads/main' && inputs.reviewed_commit == github.sha }}";
        const string proposedValidationComment =
            "# Fail-closed draft gate. Removing false requires separate approval.";
        const string activeValidationComment =
            "# Validation-only activation; retain the main and reviewed-commit checks.";
        const string oldCheckoutPin = "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683";
        const string newCheckoutPin = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1";
        const string oldSetupDotnetPin = "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9";
        const string newSetupDotnetPin = "actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73";

        Assert.StartsWith(activeHeader, activeCandidate, StringComparison.Ordinal);
        var proposedBody = proposal[proposalHeader.Length..];
        Assert.Equal(1, proposedBody.Split(disabledValidationCondition, StringSplitOptions.None).Length - 1);
        Assert.Equal(
            proposedBody
                .Replace(proposedValidationComment, activeValidationComment, StringComparison.Ordinal)
                .Replace(disabledValidationCondition, enabledValidationCondition, StringComparison.Ordinal)
                .Replace(oldCheckoutPin, newCheckoutPin, StringComparison.Ordinal)
                .Replace(oldSetupDotnetPin, newSetupDotnetPin, StringComparison.Ordinal),
            activeCandidate[activeHeader.Length..]);
        Assert.Contains(enabledValidationCondition, activeCandidate, StringComparison.Ordinal);
        Assert.Equal(1, activeCandidate.Split("if: ${{ false &&", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "if: ${{ false && needs.validate.result == 'success' && github.ref == 'refs/heads/main' && inputs.reviewed_commit == github.sha }}",
            activeCandidate,
            StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", activeCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", activeCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", activeCandidate, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationOnlyWorkflowIsReviewedMainCommitRestrictedAndReadOnly()
    {
        var workflow = ReadActiveWorkflow();
        var validation = GetJobBlock(workflow, "validate", "rehearsal");

        Assert.Contains(
            "if: ${{ github.ref == 'refs/heads/main' && inputs.reviewed_commit == github.sha }}",
            validation,
            StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains("    permissions:\n      contents: read", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions:\n  contents: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions:\n  id-token:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationJobCannotStartDatabaseServiceOrExecuteMigration()
    {
        var validation = GetJobBlock(ReadActiveWorkflow(), "validate", "rehearsal");

        Assert.DoesNotContain("services:", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres:", validation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", validation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("DATABASE_URL", validation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PGHOST", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet run", validation, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test tests/VirtuousGateway.MigrationValidation.Tests/VirtuousGateway.MigrationValidation.Tests.csproj",
            validation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInWorkflowUsesNode24ActionsAndPreservesRehearsalContainer()
    {
        var workflow = ReadActiveWorkflow();

        Assert.Equal(
            2,
            workflow.Split("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            workflow.Split("actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "node:20-bookworm@sha256:cacf10e99285cbbc891452e31249c1b5ec3ba225f40028fae946b75aeaf1b66a",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("test \"$(node --version | cut -d. -f1)\" = \"v20\"", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "if: ${{ false && needs.validate.result == 'success' && github.ref == 'refs/heads/main' && inputs.reviewed_commit == github.sha }}",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalPinsImagesActionsAndLimitsWorkflowPermissions()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("runs-on: ubuntu-24.04", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "node:20-bookworm@sha256:cacf10e99285cbbc891452e31249c1b5ec3ba225f40028fae946b75aeaf1b66a",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "postgres:17.11-bookworm@sha256:73bedc9eaa0121b92b9b26bb52e5141b3f6f37479c7ef913c0160455763ca80a",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet-version: 8.0.427", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("REVIEW_REQUIRED", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalUsesPrivateEphemeralScramServiceWithoutTargetsOrSecrets()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("services:\n      postgres:", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify pinned job-container prerequisites", workflow, StringComparison.Ordinal);
        Assert.Contains("command -v git", workflow, StringComparison.Ordinal);
        Assert.Contains("command -v sh", workflow, StringComparison.Ordinal);
        Assert.Contains("command -v curl", workflow, StringComparison.Ordinal);
        Assert.Contains("dpkg-query -W libicu72 libssl3 zlib1g libgcc-s1 libstdc++6", workflow, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_HOST_AUTH_METHOD: scram-sha-256", workflow, StringComparison.Ordinal);
        Assert.Contains("--auth-host=scram-sha-256", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("trust", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ports:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("volumes:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DATABASE_URL", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PGHOST", workflow, StringComparison.Ordinal);
        Assert.Contains("VG_REVIEWED_COMMIT", workflow, StringComparison.Ordinal);
        Assert.Contains("VG_BOOTSTRAP_PASSWORD", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CiHarnessHasNoConfigurableDatabaseTargetAndUsesLimitedRole()
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "ci-source");
        var program = File.ReadAllText(Path.Combine(sourceDirectory, "Program.cs"));
        var rehearsal = File.ReadAllText(Path.Combine(sourceDirectory, "CiMigrationRehearsal.cs"));
        var project = File.ReadAllText(Path.Combine(sourceDirectory, "CiMigrationRehearsal.csproj"));

        Assert.Contains("Host = \"postgres\"", rehearsal, StringComparison.Ordinal);
        Assert.Contains("Database = DatabaseName", rehearsal, StringComparison.Ordinal);
        Assert.Contains("Pooling = false", rehearsal, StringComparison.Ordinal);
        Assert.Contains("NOCREATEDB NOCREATEROLE", rehearsal, StringComparison.Ordinal);
        Assert.Contains("SET password_encryption = 'scram-sha-256'", rehearsal, StringComparison.Ordinal);
        Assert.Contains("VerifyStoredScramPasswordAsync", rehearsal, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL PRIVILEGES ON DATABASE", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("REVOKE CONNECT, TEMP ON DATABASE postgres", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("REVOKE CONNECT, TEMP ON DATABASE template1", rehearsal, StringComparison.Ordinal);
        Assert.Contains("has_database_privilege(current_user, current_database(), 'CREATE')", rehearsal, StringComparison.Ordinal);
        Assert.Contains("has_database_privilege(current_user, current_database(), 'TEMP')", rehearsal, StringComparison.Ordinal);
        Assert.Contains("has_database_privilege(current_user, db.datname, 'CREATE')", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("has_database_privilege(current_user, db.datname, 'TEMP')", rehearsal, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync(IsolationLevel.ReadCommitted)", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable(\"PGHOST\"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable(\"DATABASE_URL\"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("PostgresTestDatabase", rehearsal, StringComparison.Ordinal);
        Assert.Contains("Npgsql", project, StringComparison.Ordinal);
    }

    [Fact]
    public void V0001SqlAndManifestPinsRemainUnchanged()
    {
        var sql = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "artifacts", "V0001.sql"));
        var manifest = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "artifacts", "V0001.manifest.json"));

        Assert.Equal(CiPolicy.SqlSha256, Hash(sql));
        Assert.Equal(CiPolicy.ManifestSha256, Hash(manifest));
        Assert.DoesNotContain("PostgresTestDatabase", ReadWorkflow(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ApprovedContext()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_EVENT_NAME"] = "workflow_dispatch",
            ["GITHUB_REF"] = "refs/heads/main",
            ["GITHUB_SHA"] = sha,
            ["VG_REVIEWED_COMMIT"] = sha,
            ["RUNNER_OS"] = "Linux",
            ["VG_BOOTSTRAP_PASSWORD"] = "synthetic-only"
        };
    }

    private static void WithTemporaryArtifacts(
        byte[] sql,
        byte[] manifest,
        Action<string, string> assertion)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vg-ci-rehearsal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sqlPath = Path.Combine(directory, "V0001.sql");
        var manifestPath = Path.Combine(directory, "V0001.manifest.json");
        try
        {
            File.WriteAllBytes(sqlPath, sql);
            File.WriteAllBytes(manifestPath, manifest);
            assertion(sqlPath, manifestPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string GetJobBlock(string workflow, string jobName, string nextJobName)
    {
        var start = workflow.IndexOf($"  {jobName}:\n", StringComparison.Ordinal);
        var end = workflow.IndexOf($"  {nextJobName}:\n", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate workflow jobs '{jobName}' and '{nextJobName}'.");
        return workflow[start..end];
    }

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflow", "step9b216-proposal.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadActiveWorkflow() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflow", "active-candidate.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}

internal static class CiPolicy
{
    internal static string SqlSha256 => VirtuousGateway.CiMigrationRehearsal.RehearsalPolicy.SqlSha256;
    internal static string ManifestSha256 => VirtuousGateway.CiMigrationRehearsal.RehearsalPolicy.ManifestSha256;

    internal static VirtuousGateway.CiMigrationRehearsal.ExecutionContext? ValidateExecutionContext(
        IReadOnlyList<string> args,
        Func<string, string?> getEnvironmentVariable) =>
        VirtuousGateway.CiMigrationRehearsal.RehearsalPolicy.ValidateExecutionContext(args, getEnvironmentVariable);

    internal static VirtuousGateway.CiMigrationRehearsal.MigrationArtifacts LoadAndValidateArtifacts(
        string sqlPath,
        string manifestPath) =>
        VirtuousGateway.CiMigrationRehearsal.RehearsalPolicy.LoadAndValidateArtifacts(sqlPath, manifestPath);
}
