using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtuousGateway.MigrationValidation.Tests;

public sealed class V0002MigrationValidationTests
{
    private const string ExpectedSqlSha256 = "6e9adb5a875eff118df1be2d0d0d0b9952ecfe6e03f944a23cf42c0f7272e344";
    private const string ExpectedManifestSha256 = "098edb1f72b80003031529692d7ee51f353f18a05dd3d736222f0599fd241633";

    private readonly byte[] sql =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "artifacts", "V0002.sql"));
    private readonly byte[] manifest =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "artifacts", "V0002.manifest.json"));
    private readonly ExpectedCatalog expected =
        JsonSerializer.Deserialize<ExpectedCatalog>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "expected-catalog-v0002.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("V0002 expected catalog fixture is empty.");

    [Fact]
    public void ReviewedSqlAndManifestMatchPinsAndParsedIndexModel()
    {
        Assert.Equal(ExpectedSqlSha256, Hash(sql));
        Assert.Equal(ExpectedManifestSha256, Hash(manifest));
        Assert.True(MigrationArtifactValidator.ValidateSqlEncoding(sql).IsValid);

        var structure = MigrationArtifactValidator.ValidateSqlStructure(
            Encoding.UTF8.GetString(sql),
            expected);
        Assert.True(structure.IsValid, string.Join(Environment.NewLine, structure.Errors));

        ValidateManifest(manifest);
    }

    [Fact]
    public void IndexPredicateIncludesEveryNonNullValueAndAllowsNulls()
    {
        var statements = SqlLexer.SplitStatements(Encoding.UTF8.GetString(sql));
        Assert.Single(statements);
        Assert.Equal(
            ["WHERE", "aplos_transaction_id", "IS", "NOT", "NULL"],
            statements[0].TakeLast(5).Select(token => token.Text));

        var index = Assert.Single(expected.Indexes);
        Assert.Equal("virtuous_gift_processing", index.Table);
        Assert.Equal(["aplos_transaction_id"], index.Columns);
        Assert.True(index.Unique);
        Assert.Equal("btree", index.Method);
        Assert.True(index.PredicateRequired);
    }

    [Theory]
    [InlineData("non-unique")]
    [InlineData("wrong-table")]
    [InlineData("wrong-column")]
    [InlineData("no-null-predicate")]
    [InlineData("weaker-null-predicate")]
    [InlineData("destructive-statement")]
    public void SqlStructureRejectsChangesOutsideTheReviewedIndex(string mutation)
    {
        var text = Encoding.UTF8.GetString(sql);
        var changed = mutation switch
        {
            "non-unique" => text.Replace("CREATE UNIQUE INDEX", "CREATE INDEX", StringComparison.Ordinal),
            "wrong-table" => text.Replace(
                "public.virtuous_gift_processing",
                "public.other_table",
                StringComparison.Ordinal),
            "wrong-column" => text.Replace(
                "(aplos_transaction_id)",
                "(gift_id)",
                StringComparison.Ordinal),
            "no-null-predicate" => text.Replace(
                "    WHERE aplos_transaction_id IS NOT NULL;\n",
                ";\n",
                StringComparison.Ordinal),
            "weaker-null-predicate" => text.Replace(
                "aplos_transaction_id IS NOT NULL",
                "aplos_transaction_id > 0",
                StringComparison.Ordinal),
            "destructive-statement" => text + "\nDROP TABLE public.virtuous_gift_processing;\n",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.False(MigrationArtifactValidator.ValidateSqlStructure(changed, expected).IsValid);
    }

    [Fact]
    public void ManifestPinsV0002AndRemainsUnexecutedAndTransactional()
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;

        Assert.Equal("V0002", root.GetProperty("migration_version").GetString());
        Assert.Equal(
            "migrations/V0002__unique_aplos_transaction_ids.sql",
            root.GetProperty("sql_file").GetString());
        Assert.Equal(ExpectedSqlSha256, root.GetProperty("sha256").GetString());
        Assert.Equal("public", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("automatic_execution").GetBoolean());
        Assert.Equal("unexecuted", root.GetProperty("execution_status").GetString());
        Assert.True(root.GetProperty("static_review_only").GetBoolean());
        Assert.False(root.GetProperty("database_execution_or_parsing_performed").GetBoolean());

        var transaction = root.GetProperty("transaction_mode");
        Assert.Equal("single_executor_owned_transaction", transaction.GetProperty("mode").GetString());
        Assert.False(transaction.GetProperty("transaction_wrapper_in_file").GetBoolean());
        Assert.False(transaction.GetProperty("concurrent_index_builds").GetBoolean());

        Assert.Equal(
            ["public.uq_virtuous_gift_processing_aplos_transaction_id"],
            root.GetProperty("expected_objects").GetProperty("indexes")
                .EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            root.GetProperty("prerequisites").EnumerateArray().Select(item => item.GetString()),
            item => item == "Read-only duplicate preflight and approved writer maintenance barrier");
        Assert.Contains(
            root.GetProperty("excluded").EnumerateArray().Select(item => item.GetString()),
            item => item == "Data cleanup or transaction-ID reassignment");
    }

    [Fact]
    public void V0001ArtifactsRemainByteForByteAtTheirReviewedPins()
    {
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        var v0001Sql = File.ReadAllBytes(Path.Combine(artifactDirectory, "V0001.sql"));
        var v0001Manifest = File.ReadAllBytes(Path.Combine(artifactDirectory, "V0001.manifest.json"));

        Assert.Equal(MigrationArtifactValidator.ExpectedSqlSha256, Hash(v0001Sql));
        Assert.Equal(MigrationArtifactValidator.ExpectedManifestSha256, Hash(v0001Manifest));
    }

    [Fact]
    public void V0002IsLoadedOnlyByTheStandaloneOfflineValidationProject()
    {
        var boundary = Path.Combine(AppContext.BaseDirectory, "boundary");
        var solution = File.ReadAllText(Path.Combine(boundary, "VirtuousGateway.sln"));
        Assert.DoesNotContain("MigrationValidation.Tests", solution, StringComparison.OrdinalIgnoreCase);

        var ordinaryProjects = new[]
        {
            Path.Combine(boundary, "VirtuousGateway.Api.csproj"),
            Path.Combine(boundary, "VirtuousGateway.Infrastructure.csproj"),
            Path.Combine(boundary, "VirtuousGateway.Tests.csproj")
        };
        foreach (var project in ordinaryProjects)
        {
            var content = File.ReadAllText(project);
            Assert.DoesNotContain("MigrationValidation.Tests", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("V0002__unique_aplos_transaction_ids.sql", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ValidateManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("manifest_schema_version").GetInt32());
        Assert.Equal(ExpectedSqlSha256, root.GetProperty("sha256").GetString());
        Assert.Equal("UTF-8 without BOM", root.GetProperty("file_format").GetProperty("encoding").GetString());
        Assert.Equal("LF", root.GetProperty("file_format").GetProperty("line_endings").GetString());
        Assert.True(root.GetProperty("file_format").GetProperty("final_newline").GetBoolean());
        Assert.Equal(
            "Exact SQL file bytes, including comments and whitespace",
            root.GetProperty("file_format").GetProperty("checksum_input").GetString());
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
