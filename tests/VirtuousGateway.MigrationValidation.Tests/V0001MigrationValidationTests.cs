using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VirtuousGateway.MigrationValidation.Tests;

public sealed class V0001MigrationValidationTests
{
    private readonly ExpectedCatalog expected = MigrationArtifactValidator.LoadExpectedCatalog();
    private readonly byte[] sql = File.ReadAllBytes(MigrationArtifactValidator.SqlArtifactPath);
    private readonly byte[] manifest = File.ReadAllBytes(MigrationArtifactValidator.ManifestArtifactPath);

    [Fact]
    public void ReviewedSqlAndManifestMatchIndependentPinsAndCatalogModel()
    {
        Assert.Equal(MigrationArtifactValidator.ExpectedSqlSha256, Hash(sql));
        Assert.Equal(MigrationArtifactValidator.ExpectedManifestSha256, Hash(manifest));
        Assert.True(MigrationArtifactValidator.ValidateArtifacts(sql, manifest, expected).IsValid,
            string.Join(Environment.NewLine, MigrationArtifactValidator.ValidateArtifacts(sql, manifest, expected).Errors));
    }

    [Fact]
    public void SqlUsesStrictUtf8LfAndFinalNewline()
    {
        Assert.True(MigrationArtifactValidator.ValidateSqlEncoding(sql).IsValid);
        Assert.Contains("UTF-8 without BOM", Encoding.UTF8.GetString(manifest), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BOM")]
    [InlineData("CRLF")]
    [InlineData("NoFinalNewline")]
    [InlineData("InvalidUtf8")]
    public void SqlEncodingViolationsAreRejected(string mutation)
    {
        var changed = mutation switch
        {
            "BOM" => [0xEF, 0xBB, 0xBF, .. sql],
            "CRLF" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(sql).Replace("\n", "\r\n", StringComparison.Ordinal)),
            "NoFinalNewline" => sql[..^1],
            "InvalidUtf8" => [.. sql, 0xFF],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.False(MigrationArtifactValidator.ValidateSqlEncoding(changed).IsValid);
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("edit")]
    [InlineData("remove constraint")]
    [InlineData("rename table")]
    public void AlteredOrTruncatedSqlDoesNotMatchReviewedArtifact(string mutation)
    {
        var changedText = Encoding.UTF8.GetString(sql);
        changedText = mutation switch
        {
            "truncate" => changedText[..(changedText.Length / 2)],
            "edit" => changedText.Replace("operation_type = 1", "operation_type = 2", StringComparison.Ordinal),
            "remove constraint" => changedText.Replace(
                "CONSTRAINT ck_accounting_operations_origin",
                "CONSTRAINT ck_accounting_operations_removed",
                StringComparison.Ordinal),
            "rename table" => changedText.Replace(
                "CREATE TABLE public.accounting_operations",
                "CREATE TABLE public.renamed_operations",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.False(MigrationArtifactValidator.ValidateArtifacts(
            Encoding.UTF8.GetBytes(changedText), manifest, expected).IsValid);
    }

    [Fact]
    public void StaticSqlStructureMatchesExpectedTablesColumnsConstraintsIndexesAndDependencies()
    {
        var result = MigrationArtifactValidator.ValidateSqlStructure(Encoding.UTF8.GetString(sql), expected);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void StaticSqlStructurePinsConstraintAndIndexDefinitions()
    {
        var text = Encoding.UTF8.GetString(sql);
        var changedConstraint = text.Replace("operation_type = 1", "operation_type = 2", StringComparison.Ordinal);
        var changedIndex = text.Replace(
            "event_kind IN ('confirmed_success', 'manual_confirmed_posted', 'legacy_projected')",
            "event_kind IN ('confirmed_success', 'manual_confirmed_posted')",
            StringComparison.Ordinal);

        Assert.False(MigrationArtifactValidator.ValidateSqlStructure(changedConstraint, expected).IsValid);
        Assert.False(MigrationArtifactValidator.ValidateSqlStructure(changedIndex, expected).IsValid);
    }

    [Fact]
    public void StaticModelRejectsMissingDuplicateRenamedAndChangedSchemaObjects()
    {
        var text = Encoding.UTF8.GetString(sql);
        var mutations = new[]
        {
            text.Replace("source_record_id text COLLATE \"C\" NOT NULL,\n", "", StringComparison.Ordinal),
            text + "\nCREATE TABLE public.accounting_operations ( operation_id uuid NOT NULL );\n",
            text.Replace("ck_accounting_operations_origin", "ck_accounting_operations_renamed", StringComparison.Ordinal),
            text.Replace("operation_type = 1", "operation_type = 2", StringComparison.Ordinal),
            text.Replace("uq_accounting_evidence_success", "uq_accounting_evidence_renamed", StringComparison.Ordinal),
            text.Replace("event_kind IN ('confirmed_success', 'manual_confirmed_posted', 'legacy_projected')",
                "event_kind IN ('confirmed_success', 'manual_confirmed_posted')", StringComparison.Ordinal)
        };

        foreach (var mutation in mutations)
            Assert.False(MigrationArtifactValidator.ValidateArtifacts(
                Encoding.UTF8.GetBytes(mutation), manifest, expected).IsValid);
    }

    [Fact]
    public void StaticParserRejectsUnexpectedStatementsAndDuplicateDeclarations()
    {
        var text = Encoding.UTF8.GetString(sql);
        var withDml = text + "\nINSERT INTO public.accounting_operations (operation_id) VALUES ('00000000-0000-0000-0000-000000000001');\n";
        const string constraint = "    CONSTRAINT ck_accounting_receipts_version CHECK (schema_version = 1),";
        var withDuplicateConstraint = text.Replace(constraint, $"{constraint}\n{constraint}", StringComparison.Ordinal);
        const string index = "CREATE INDEX ix_accounting_evidence_operation_recorded\n    ON public.accounting_operation_evidence (operation_id, recorded_at_utc, event_id);";
        var withDuplicateIndex = text.Replace(index, $"{index}\n\n{index}", StringComparison.Ordinal);
        var statements = new[]
        {
            withDml,
            withDuplicateConstraint,
            withDuplicateIndex,
            text.Replace("CREATE TABLE public.accounting_receipts", "CREATE TABLE public.accounting_operations", StringComparison.Ordinal)
        };

        foreach (var statement in statements)
            Assert.False(MigrationArtifactValidator.ValidateSqlStructure(statement, expected).IsValid);
    }

    [Fact]
    public void ManifestIsNonAutomaticAndDescribesUnexecutedPostgreSql17Transaction()
    {
        var root = JsonNode.Parse(manifest)!.AsObject();
        Assert.False(root["automatic_execution"]!.GetValue<bool>());
        Assert.Equal("unexecuted", root["execution_status"]!.GetValue<string>());
        Assert.Equal(17, root["postgresql_baseline"]!["major"]!.GetValue<int>());
        Assert.Equal("single_executor_owned_transaction", root["transaction_mode"]!["mode"]!.GetValue<string>());
        Assert.False(root["transaction_mode"]!["transaction_wrapper_in_file"]!.GetValue<bool>());
        var result = MigrationArtifactValidator.ValidateManifestSemantics(manifest, expected);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("auto-execution")]
    [InlineData("executed-status")]
    [InlineData("wrong-baseline")]
    [InlineData("missing-table")]
    [InlineData("duplicate-constraint")]
    [InlineData("missing-prerequisite")]
    [InlineData("duplicate-property")]
    public void ManifestPolicyAndInventoryMutationsAreRejected(string mutation)
    {
        var json = Encoding.UTF8.GetString(manifest);
        var changed = mutation switch
        {
            "auto-execution" => json.Replace("\"automatic_execution\":  false", "\"automatic_execution\":  true", StringComparison.Ordinal),
            "executed-status" => json.Replace("\"execution_status\":  \"unexecuted\"", "\"execution_status\":  \"applied\"", StringComparison.Ordinal),
            "wrong-baseline" => json.Replace("\"major\":  17", "\"major\":  16", StringComparison.Ordinal),
            "missing-table" => json.Replace("\"name\":  \"public.accounting_receipts\",", "\"name\":  \"public.other_receipts\",", StringComparison.Ordinal),
            "duplicate-constraint" => json.Replace(
                "\"ck_accounting_operations_uuid\",",
                "\"pk_accounting_operations\",",
                StringComparison.Ordinal),
            "missing-prerequisite" => json.Replace(
                "\"Existing public.virtuous_gift_processing table with gift_id bigint primary key\",\n",
                "",
                StringComparison.Ordinal),
            "duplicate-property" => json.Replace(
                "\"execution_status\":  \"unexecuted\",",
                "\"execution_status\":  \"unexecuted\",\n    \"execution_status\":  \"unexecuted\",",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.False(MigrationArtifactValidator.ValidateManifestSemantics(
            Encoding.UTF8.GetBytes(changed), expected).IsValid);
    }

    [Fact]
    public void FixtureModelsEmptyCatalogAndMigrationCompatibilityWithoutExecutingSql()
    {
        Assert.Equal(17, expected.PostgreSqlMajor);
        Assert.Equal("UTF8", expected.DatabaseEncoding);
        Assert.Equal("public", expected.Schema);
        Assert.Equal(0, expected.NewTableRowsAfterCreate);
        Assert.Equal("virtuous_gift_processing", expected.Prerequisite.Table);
        Assert.Equal("gift_id", expected.Prerequisite.Column);
        Assert.True(expected.Prerequisite.PrimaryKeyRequired);
        Assert.Equal(3, expected.Tables.Count);
        Assert.Equal(4, expected.Indexes.Count);
        Assert.Equal(4, expected.ForeignKeys.Count);
        Assert.All(expected.Tables, table => Assert.NotEmpty(table.Columns));
        Assert.All(expected.Tables, table => Assert.NotEmpty(table.Constraints));
    }

    [Fact]
    public void MigrationRemainsOutsideOrdinaryBuildStartupAndTestDiscovery()
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
            Assert.DoesNotContain("V0001__add_accounting_operation_receipt_evidence.sql", content, StringComparison.OrdinalIgnoreCase);
        }

        var dedicatedProject = File.ReadAllText(
            Path.Combine(boundary, "VirtuousGateway.MigrationValidation.Tests.csproj"));
        Assert.DoesNotContain("ProjectReference", dedicatedProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", dedicatedProject, StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
