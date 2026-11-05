using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtuousGateway.CiMigrationRehearsal;

internal sealed record ExecutionContext(string BootstrapPassword);

internal sealed record MigrationArtifacts(
    string Sql,
    IReadOnlySet<string> Tables,
    IReadOnlySet<string> Constraints,
    IReadOnlySet<string> ExplicitIndexes);

internal static class RehearsalPolicy
{
    internal const string SqlSha256 =
        "c1eb364eaf13a1e5ee07bd04001c3bda936aa46e81d4f47e54ad52fe0a180227";
    internal const string ManifestSha256 =
        "efd2413cf4353c8eed9e1904919d34f2f0229c7abc401cbd25616b67b6322682";

    internal static ExecutionContext? ValidateExecutionContext(
        IReadOnlyList<string> args,
        Func<string, string?> getEnvironmentVariable)
    {
        if (args.Count != 0
            || getEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || getEnvironmentVariable("GITHUB_EVENT_NAME") != "workflow_dispatch"
            || getEnvironmentVariable("GITHUB_REF") != "refs/heads/main"
            || getEnvironmentVariable("RUNNER_OS") != "Linux")
        {
            return null;
        }

        var sha = getEnvironmentVariable("GITHUB_SHA");
        var reviewedCommit = getEnvironmentVariable("VG_REVIEWED_COMMIT");
        var password = getEnvironmentVariable("VG_BOOTSTRAP_PASSWORD");
        if (!IsCommitSha(sha)
            || !string.Equals(sha, reviewedCommit, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new ExecutionContext(password);
    }

    internal static MigrationArtifacts LoadAndValidateArtifacts(string sqlPath, string manifestPath)
    {
        var sqlBytes = File.ReadAllBytes(sqlPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        RequireHash(sqlBytes, SqlSha256, "V0001 SQL");
        RequireHash(manifestBytes, ManifestSha256, "V0001 manifest");

        if (sqlBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
            || sqlBytes.Contains((byte)'\r')
            || sqlBytes.Length == 0
            || sqlBytes[^1] != (byte)'\n')
        {
            throw new InvalidDataException("V0001 SQL encoding or line-ending policy failed.");
        }

        var sql = new UTF8Encoding(false, true).GetString(sqlBytes);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        if (root.GetProperty("automatic_execution").GetBoolean()
            || root.GetProperty("execution_status").GetString() != "unexecuted"
            || !root.GetProperty("static_review_only").GetBoolean()
            || root.GetProperty("sha256").GetString() != SqlSha256
            || root.GetProperty("postgresql_baseline").GetProperty("major").GetInt32() != 17
            || root.GetProperty("schema").GetString() != "public")
        {
            throw new InvalidDataException("V0001 manifest policy validation failed.");
        }

        var expectedObjects = root.GetProperty("expected_objects");
        var tables = expectedObjects.GetProperty("tables")
            .EnumerateArray()
            .Select(table => QualifiedObjectName(table.GetProperty("name").GetString()))
            .ToHashSet(StringComparer.Ordinal);
        var constraints = expectedObjects.GetProperty("tables")
            .EnumerateArray()
            .SelectMany(table => table.GetProperty("constraints").EnumerateArray())
            .Select(constraint => constraint.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var indexes = expectedObjects.GetProperty("explicit_indexes")
            .EnumerateArray()
            .Select(index => QualifiedObjectName(index.GetString()))
            .ToHashSet(StringComparer.Ordinal);

        if (tables.Count != 3 || constraints.Count != 42 || indexes.Count != 4)
        {
            throw new InvalidDataException("V0001 manifest object inventory validation failed.");
        }

        return new MigrationArtifacts(sql, tables, constraints, indexes);
    }

    private static void RequireHash(byte[] bytes, string expected, string artifact)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected)))
        {
            throw new InvalidDataException($"{artifact} SHA-256 does not match the reviewed pin.");
        }
    }

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static string QualifiedObjectName(string? value)
    {
        const string prefix = "public.";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("V0001 manifest contains an unexpected object namespace.");
        }

        return value[prefix.Length..];
    }
}
