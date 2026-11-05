using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtuousGateway.MigrationValidation.Tests;

internal sealed class ExpectedCatalog
{
    public int PostgreSqlMajor { get; set; }
    public string DatabaseEncoding { get; set; } = "";
    public string Schema { get; set; } = "";
    public int NewTableRowsAfterCreate { get; set; }
    public PrerequisiteSpec Prerequisite { get; set; } = new();
    public List<TableSpec> Tables { get; set; } = [];
    public List<IndexSpec> Indexes { get; set; } = [];
    public List<ForeignKeySpec> ForeignKeys { get; set; } = [];
}

internal sealed class PrerequisiteSpec
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public string Type { get; set; } = "";
    public bool PrimaryKeyRequired { get; set; }
}

internal sealed class TableSpec
{
    public string Name { get; set; } = "";
    public string DefinitionSha256 { get; set; } = "";
    public List<ColumnSpec> Columns { get; set; } = [];
    public List<NamedObjectSpec> Constraints { get; set; } = [];
}

internal sealed class ColumnSpec
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Nullable { get; set; }
}

internal sealed class NamedObjectSpec
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
}

internal sealed class IndexSpec
{
    public string Name { get; set; } = "";
    public string DefinitionSha256 { get; set; } = "";
    public string Table { get; set; } = "";
    public bool Unique { get; set; }
    public string Method { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public bool PredicateRequired { get; set; }
}

internal sealed class ForeignKeySpec
{
    public string Table { get; set; } = "";
    public string Constraint { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public List<string> ReferencedColumns { get; set; } = [];
}

internal sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

internal static class MigrationArtifactValidator
{
    internal const string ExpectedSqlSha256 =
        "c1eb364eaf13a1e5ee07bd04001c3bda936aa46e81d4f47e54ad52fe0a180227";
    internal const string ExpectedManifestSha256 =
        "efd2413cf4353c8eed9e1904919d34f2f0229c7abc401cbd25616b67b6322682";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ExpectedCatalog LoadExpectedCatalog() =>
        JsonSerializer.Deserialize<ExpectedCatalog>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "expected-catalog-v0001.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Expected catalog fixture is empty.");

    internal static ValidationResult ValidateArtifacts(
        byte[] sqlBytes,
        byte[] manifestBytes,
        ExpectedCatalog expected)
    {
        var errors = new List<string>();
        ValidatePinnedHash(sqlBytes, ExpectedSqlSha256, "SQL", errors);
        ValidatePinnedHash(manifestBytes, ExpectedManifestSha256, "manifest", errors);

        string sql;
        try
        {
            sql = ValidateSqlEncoding(sqlBytes, errors);
        }
        catch (DecoderFallbackException)
        {
            errors.Add("SQL is not valid UTF-8.");
            return new(errors);
        }

        ValidateManifest(manifestBytes, expected, errors);
        errors.AddRange(ValidateSqlStructure(sql, expected).Errors);
        return new(errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static ValidationResult ValidateSqlEncoding(byte[] sqlBytes)
    {
        var errors = new List<string>();
        try
        {
            _ = ValidateSqlEncoding(sqlBytes, errors);
        }
        catch (DecoderFallbackException)
        {
            errors.Add("SQL is not valid UTF-8.");
        }
        return new(errors);
    }

    internal static ValidationResult ValidateManifestSemantics(
        byte[] manifestBytes,
        ExpectedCatalog expected)
    {
        var errors = new List<string>();
        ValidateManifest(manifestBytes, expected, errors);
        return new(errors);
    }

    internal static ValidationResult ValidateSqlStructure(string sql, ExpectedCatalog expected)
    {
        var errors = new List<string>();
        IReadOnlyList<IReadOnlyList<SqlToken>> statements;
        try
        {
            statements = SqlLexer.SplitStatements(sql);
        }
        catch (FormatException exception)
        {
            return new([$"SQL tokenization failed: {exception.Message}"]);
        }

        var tables = new List<ParsedTable>();
        var indexes = new List<ParsedIndex>();
        foreach (var statement in statements)
        {
            if (IsKeyword(statement, 0, "CREATE") && IsKeyword(statement, 1, "TABLE"))
            {
                if (TryParseTable(statement, out var table))
                    tables.Add(table);
                else
                    errors.Add("A CREATE TABLE statement has an unsupported or malformed shape.");
            }
            else if (IsKeyword(statement, 0, "CREATE") &&
                     (IsKeyword(statement, 1, "INDEX") ||
                      (IsKeyword(statement, 1, "UNIQUE") && IsKeyword(statement, 2, "INDEX"))))
            {
                if (TryParseIndex(statement, out var index))
                    indexes.Add(index);
                else
                    errors.Add("A CREATE INDEX statement has an unsupported or malformed shape.");
            }
            else
            {
                errors.Add("The migration contains a non-table/index statement.");
            }
        }

        if (statements.Count != expected.Tables.Count + expected.Indexes.Count)
            errors.Add("The migration statement count does not match the reviewed catalog model.");

        AddDuplicateErrors(tables.Select(table => table.Name), "table", errors);
        AddDuplicateErrors(indexes.Select(index => index.Name), "index", errors);
        CompareTables(tables, expected, errors);
        CompareIndexes(indexes, expected, errors);
        CompareForeignKeys(tables, expected, errors);
        return new(errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static string SqlArtifactPath =>
        Path.Combine(AppContext.BaseDirectory, "artifacts", "V0001.sql");

    internal static string ManifestArtifactPath =>
        Path.Combine(AppContext.BaseDirectory, "artifacts", "V0001.manifest.json");

    private static string ValidateSqlEncoding(byte[] bytes, List<string> errors)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            errors.Add("SQL must not contain a UTF-8 BOM.");

        string sql = StrictUtf8.GetString(bytes);
        if (sql.Contains('\r'))
            errors.Add("SQL must use LF line endings only.");
        if (!sql.EndsWith('\n'))
            errors.Add("SQL must end with a newline.");
        if (!StrictUtf8.GetBytes(sql).AsSpan().SequenceEqual(bytes))
            errors.Add("SQL bytes do not round-trip through strict UTF-8.");
        return sql;
    }

    private static void ValidatePinnedHash(byte[] bytes, string expected, string label, List<string> errors)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            errors.Add($"{label} SHA-256 does not match the reviewed pin.");
    }

    private static void ValidateManifest(
        byte[] manifestBytes,
        ExpectedCatalog expected,
        List<string> errors)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestBytes);
        }
        catch (JsonException)
        {
            errors.Add("Manifest is not valid JSON.");
            return;
        }

        using (document)
        {
            FindDuplicateProperties(document.RootElement, "$", errors);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Manifest root must be an object.");
                return;
            }

            var root = document.RootElement;
            RequireString(root, "description", "Add empty accounting operation, normalized receipt, and evidence tables", errors);
            RequireString(root, "migration_version", "V0001", errors);
            RequireString(root, "sql_file", "migrations/V0001__add_accounting_operation_receipt_evidence.sql", errors);
            RequireString(root, "sha256", ExpectedSqlSha256, errors);
            RequireString(root, "schema", expected.Schema, errors);
            RequireBoolean(root, "automatic_execution", false, errors);
            RequireString(root, "execution_status", "unexecuted", errors);
            RequireBoolean(root, "static_review_only", true, errors);
            RequireBoolean(root, "database_execution_or_parsing_performed", false, errors);
            ValidateExactProperties(root, [
                "manifest_schema_version", "migration_version", "description", "sql_file", "sha256",
                "file_format", "postgresql_baseline", "schema", "prerequisites", "transaction_mode",
                "automatic_execution", "execution_status", "expected_objects", "excluded",
                "static_review_only", "database_execution_or_parsing_performed"
            ], "$", errors);

            if (!TryProperty(root, "manifest_schema_version", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out var schema) || schema != 1)
                errors.Add("Manifest schema version is not the reviewed version.");

            if (!TryProperty(root, "postgresql_baseline", out var baseline) ||
                !TryProperty(baseline, "major", out var major) ||
                major.ValueKind != JsonValueKind.Number ||
                !major.TryGetInt32(out var majorValue) || majorValue != expected.PostgreSqlMajor)
                errors.Add("Manifest PostgreSQL baseline does not match the catalog model.");
            ValidateExactProperties(baseline, ["major", "scope"], "$.postgresql_baseline", errors);

            if (!TryProperty(root, "file_format", out var format))
                errors.Add("Manifest file_format is missing.");
            else
            {
                RequireString(format, "encoding", "UTF-8 without BOM", errors);
                RequireString(format, "line_endings", "LF", errors);
                RequireBoolean(format, "final_newline", true, errors);
                RequireString(format, "checksum_input", "Exact SQL file bytes, including comments and whitespace", errors);
                ValidateExactProperties(format,
                    ["encoding", "line_endings", "final_newline", "checksum_input"],
                    "$.file_format", errors);
            }

            if (!TryProperty(root, "transaction_mode", out var transaction))
                errors.Add("Manifest transaction_mode is missing.");
            else
            {
                RequireString(transaction, "mode", "single_executor_owned_transaction", errors);
                RequireBoolean(transaction, "transaction_wrapper_in_file", false, errors);
                RequireString(transaction, "isolation", "read_committed", errors);
                RequireBoolean(transaction, "concurrent_index_builds", false, errors);
                RequireString(transaction, "on_error", "Rollback entire migration; do not continue or retry posting", errors);
                ValidateExactProperties(transaction,
                    ["mode", "transaction_wrapper_in_file", "isolation", "concurrent_index_builds", "on_error"],
                    "$.transaction_mode", errors);
            }

            ValidatePrerequisites(root, expected, errors);
            ValidateManifestObjects(root, expected, errors);
        }
    }

    private static void ValidatePrerequisites(JsonElement root, ExpectedCatalog expected, List<string> errors)
    {
        if (!TryProperty(root, "prerequisites", out var prerequisites) ||
            prerequisites.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Manifest prerequisites are missing or malformed.");
            return;
        }

        var values = prerequisites.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
        var required = new[]
        {
            "Separate explicit execution authorization",
            "Independently attested target and dedicated migration credentials",
            $"PostgreSQL {expected.PostgreSqlMajor} with UTF8 database encoding",
            "Verified public schema, ownership, search_path, and default privileges",
            $"Existing public.{expected.Prerequisite.Table} table with {expected.Prerequisite.Column} {expected.Prerequisite.Type} primary key",
            "No conflicting expected objects; do not infer schema from names alone",
            "Approved executor, durable version/checksum tracking, bounded migration lock, and legacy-writer maintenance barrier"
        };
        foreach (var item in required)
            if (!values.Contains(item, StringComparer.Ordinal))
                errors.Add($"Manifest prerequisite is missing: {item}");
        CompareNameList(values, required, "manifest prerequisites", errors);
        AddDuplicateErrors(values, "manifest prerequisite", errors);
    }

    private static void ValidateManifestObjects(JsonElement root, ExpectedCatalog expected, List<string> errors)
    {
        if (!TryProperty(root, "expected_objects", out var objects))
        {
            errors.Add("Manifest expected_objects is missing.");
            return;
        }

        if (!TryProperty(objects, "tables", out var manifestTables) ||
            manifestTables.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Manifest expected tables are missing or malformed.");
            return;
        }

        var expectedTables = expected.Tables.ToDictionary(table => $"{expected.Schema}.{table.Name}", StringComparer.Ordinal);
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in manifestTables.EnumerateArray())
        {
            var name = GetString(table, "name");
            if (name is null || !seenTables.Add(name))
            {
                errors.Add("Manifest table names are missing or duplicated.");
                continue;
            }
            if (!expectedTables.TryGetValue(name, out var specification))
            {
                errors.Add($"Manifest includes unexpected table '{name}'.");
                continue;
            }
            if (!TryProperty(table, "constraints", out var constraints) ||
                constraints.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"Manifest constraints for '{name}' are missing or malformed.");
                continue;
            }
            var names = constraints.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
            CompareNameList(names, specification.Constraints.Select(item => item.Name), $"constraints for {name}", errors);
            ValidateExactProperties(table, ["name", "constraints"], $"$.expected_objects.tables[{name}]", errors);
        }
        CompareNameList(seenTables, expected.Tables.Select(table => $"{expected.Schema}.{table.Name}"), "manifest tables", errors);

        if (!TryProperty(objects, "explicit_indexes", out var explicitIndexes) ||
            explicitIndexes.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Manifest explicit indexes are missing or malformed.");
            return;
        }
        var indexNames = explicitIndexes.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
        CompareNameList(indexNames, expected.Indexes.Select(index => $"public.{index.Name}"), "manifest indexes", errors);

        RequireString(objects, "constraint_backing_indexes",
            "Primary-key and unique constraints create their own indexes; no extra tables or user-defined triggers", errors);
        ValidateExactProperties(objects, ["tables", "explicit_indexes", "constraint_backing_indexes"],
            "$.expected_objects", errors);

        var excludedExpected = new[]
        {
            "backfill", "data changes", "legacy schema changes", "user-defined triggers",
            "procedures/functions", "roles or privilege changes", "migration history table", "runtime integration"
        };
        if (!TryProperty(root, "excluded", out var excluded) || excluded.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Manifest excluded scope is missing or malformed.");
        }
        else
        {
            var excludedValues = excluded.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
            CompareNameList(excludedValues, excludedExpected, "excluded migration scope", errors);
        }
    }

    private static void CompareTables(List<ParsedTable> actual, ExpectedCatalog expected, List<string> errors)
    {
        var expectedByName = expected.Tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        foreach (var table in actual)
        {
            if (!expectedByName.TryGetValue(table.Name, out var specification))
            {
                errors.Add($"Unexpected table '{table.Name}'.");
                continue;
            }
            if (!string.Equals(table.DefinitionSha256, specification.DefinitionSha256, StringComparison.Ordinal))
                errors.Add($"Table '{table.Name}' definition signature differs: {table.DefinitionSha256}.");

            var actualColumns = table.Columns
                .GroupBy(column => column.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            CompareNameList(actualColumns.Keys, specification.Columns.Select(column => column.Name),
                $"columns in {table.Name}", errors);
            foreach (var column in specification.Columns)
            {
                if (!actualColumns.TryGetValue(column.Name, out var parsed))
                    continue;
                if (!string.Equals(parsed.Type, column.Type, StringComparison.Ordinal))
                    errors.Add($"Column '{table.Name}.{column.Name}' has an unexpected type.");
                if (parsed.Nullable != column.Nullable)
                    errors.Add($"Column '{table.Name}.{column.Name}' has unexpected nullability.");
            }

            AddDuplicateErrors(table.Columns.Select(column => column.Name), $"column in {table.Name}", errors);
            AddDuplicateErrors(table.Constraints.Select(constraint => constraint.Name), $"constraint in {table.Name}", errors);
            var actualConstraints = table.Constraints
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            CompareNameList(actualConstraints.Keys, specification.Constraints.Select(item => item.Name),
                $"constraints in {table.Name}", errors);
            foreach (var constraint in specification.Constraints)
            {
                if (actualConstraints.TryGetValue(constraint.Name, out var parsed) &&
                    !string.Equals(parsed.Kind, constraint.Kind, StringComparison.Ordinal))
                    errors.Add($"Constraint '{constraint.Name}' has an unexpected kind.");
            }
        }
        CompareNameList(actual.Select(table => table.Name), expected.Tables.Select(table => table.Name),
            "tables", errors);
    }

    private static void CompareIndexes(List<ParsedIndex> actual, ExpectedCatalog expected, List<string> errors)
    {
        var expectedByName = expected.Indexes.ToDictionary(index => index.Name, StringComparer.Ordinal);
        foreach (var index in actual)
        {
            if (!expectedByName.TryGetValue(index.Name, out var specification))
            {
                errors.Add($"Unexpected index '{index.Name}'.");
                continue;
            }
            if (index.Table != specification.Table ||
                index.Unique != specification.Unique ||
                index.Method != specification.Method ||
                index.PredicatePresent != specification.PredicateRequired ||
                !index.Columns.SequenceEqual(specification.Columns, StringComparer.Ordinal))
                errors.Add($"Index '{index.Name}' does not match its catalog expectation.");
            else if (!string.Equals(index.DefinitionSha256, specification.DefinitionSha256, StringComparison.Ordinal))
                errors.Add($"Index '{index.Name}' definition signature differs: {index.DefinitionSha256}.");
        }
        CompareNameList(actual.Select(index => index.Name), expected.Indexes.Select(index => index.Name),
            "indexes", errors);
    }

    private static void CompareForeignKeys(List<ParsedTable> tables, ExpectedCatalog expected, List<string> errors)
    {
        var actual = tables.SelectMany(table => table.ForeignKeys).ToArray();
        if (actual.Length != expected.ForeignKeys.Count)
        {
            errors.Add("Foreign-key count does not match the catalog expectation.");
            return;
        }

        foreach (var foreignKey in expected.ForeignKeys)
        {
            var matches = actual.Where(item => item.Constraint == foreignKey.Constraint).ToArray();
            if (matches.Length != 1)
            {
                errors.Add($"Foreign key '{foreignKey.Constraint}' is missing or duplicated.");
                continue;
            }
            var parsed = matches[0];
            if (parsed.Table != foreignKey.Table ||
                parsed.ReferencedTable != foreignKey.ReferencedTable ||
                !parsed.Columns.SequenceEqual(foreignKey.Columns, StringComparer.Ordinal) ||
                !parsed.ReferencedColumns.SequenceEqual(foreignKey.ReferencedColumns, StringComparer.Ordinal))
                errors.Add($"Foreign key '{foreignKey.Constraint}' does not match its catalog expectation.");
        }
    }

    private static bool TryParseTable(IReadOnlyList<SqlToken> tokens, out ParsedTable table)
    {
        table = new ParsedTable("", "", [], [], []);
        if (tokens.Count < 7 || !IsIdentifier(tokens, 2, "public") || !IsSymbol(tokens, 3, ".") ||
            tokens[2].Kind != SqlTokenKind.Word || tokens[4].Kind != SqlTokenKind.Word ||
            !IsSymbol(tokens, 5, "("))
            return false;

        var name = tokens[4].Text;
        var end = MatchingParenthesis(tokens, 5);
        if (end < 0 || end != tokens.Count - 1)
            return false;

        var clauses = SplitTopLevel(tokens.Skip(6).Take(end - 6).ToArray(), ",");
        var columns = new List<ParsedColumn>();
        var constraints = new List<ParsedConstraint>();
        var foreignKeys = new List<ParsedForeignKey>();
        foreach (var clause in clauses)
        {
            if (clause.Count == 0)
                return false;
            if (IsKeyword(clause, 0, "CONSTRAINT"))
            {
                if (clause.Count < 4 || clause[1].Kind != SqlTokenKind.Word)
                    return false;
                var constraintName = clause[1].Text;
                var kind = IsKeyword(clause, 2, "PRIMARY") ? "primary_key" :
                    IsKeyword(clause, 2, "UNIQUE") ? "unique" :
                    IsKeyword(clause, 2, "FOREIGN") ? "foreign_key" :
                    IsKeyword(clause, 2, "CHECK") ? "check" : "unknown";
                constraints.Add(new(constraintName, kind));
                if (kind == "foreign_key")
                {
                    if (!TryParseForeignKey(name, constraintName, clause, out var foreignKey))
                        return false;
                    foreignKeys.Add(foreignKey!);
                }
            }
            else if (clause[0].Kind == SqlTokenKind.Word && TryParseColumn(clause, out var column))
            {
                columns.Add(column);
            }
            else
            {
                return false;
            }
        }

        table = new(name, Signature(tokens), columns, constraints, foreignKeys);
        return true;
    }

    private static bool TryParseColumn(IReadOnlyList<SqlToken> tokens, out ParsedColumn column)
    {
        column = new ParsedColumn("", "", false);
        var modifierIndex = tokens.Count;
        for (var index = 1; index < tokens.Count; index++)
        {
            if (IsAnyKeyword(tokens, index, "COLLATE", "NOT", "NULL", "DEFAULT", "CHECK",
                    "REFERENCES", "PRIMARY", "UNIQUE", "GENERATED"))
            {
                modifierIndex = index;
                break;
            }
        }
        if (modifierIndex <= 1)
            return false;

        var type = NormalizeType(tokens.Skip(1).Take(modifierIndex - 1));
        var nullable = true;
        for (var index = modifierIndex; index < tokens.Count; index++)
        {
            if (IsKeyword(tokens, index, "NOT") && IsKeyword(tokens, index + 1, "NULL"))
            {
                nullable = false;
                break;
            }
            if (IsKeyword(tokens, index, "NULL"))
            {
                nullable = true;
                break;
            }
        }
        column = new(tokens[0].Text, type, nullable);
        return true;
    }

    private static bool TryParseForeignKey(
        string table,
        string constraintName,
        IReadOnlyList<SqlToken> clause,
        out ParsedForeignKey? foreignKey)
    {
        foreignKey = null;
        var foreign = FindKeyword(clause, "FOREIGN");
        var references = FindKeyword(clause, "REFERENCES");
        if (foreign < 0 || references < 0 || !IsKeyword(clause, foreign + 1, "KEY") ||
            !IsIdentifier(clause, references + 1, "public") ||
            !IsSymbol(clause, references + 2, ".") ||
            clause[references + 3].Kind != SqlTokenKind.Word ||
            !IsSymbol(clause, references + 4, "("))
            return false;

        var localOpen = FindSymbol(clause, foreign + 2, "(");
        var localClose = localOpen < 0 ? -1 : MatchingParenthesis(clause, localOpen);
        var referencedOpen = references + 4;
        var referencedClose = MatchingParenthesis(clause, referencedOpen);
        if (localOpen < 0 || localClose < 0 || referencedClose < 0)
            return false;

        var localColumns = SplitTopLevel(clause.Skip(localOpen + 1).Take(localClose - localOpen - 1).ToArray(), ",")
            .Select(part => part.Count == 1 ? part[0].Text : "")
            .ToArray();
        var referencedColumns = SplitTopLevel(clause.Skip(referencedOpen + 1).Take(referencedClose - referencedOpen - 1).ToArray(), ",")
            .Select(part => part.Count == 1 ? part[0].Text : "")
            .ToArray();
        foreignKey = new(table, constraintName, clause[references + 3].Text, localColumns, referencedColumns);
        return localColumns.Length > 0 && localColumns.All(value => value.Length > 0) &&
               referencedColumns.Length > 0 && referencedColumns.All(value => value.Length > 0);
    }

    private static bool TryParseIndex(IReadOnlyList<SqlToken> tokens, out ParsedIndex index)
    {
        index = new ParsedIndex("", "", "", false, "", [], false);
        var indexNamePosition = IsKeyword(tokens, 1, "UNIQUE") ? 3 : 2;
        var unique = indexNamePosition == 3;
        var on = FindKeyword(tokens, "ON");
        if (on != indexNamePosition + 1 || tokens.Count <= on + 4 ||
            tokens[indexNamePosition].Kind != SqlTokenKind.Word ||
            !IsIdentifier(tokens, on + 1, "public") || !IsSymbol(tokens, on + 2, ".") ||
            tokens[on + 3].Kind != SqlTokenKind.Word)
            return false;

        var open = FindSymbol(tokens, on + 4, "(");
        if (open < 0)
            return false;
        var close = MatchingParenthesis(tokens, open);
        if (close < 0)
            return false;
        var keyColumns = SplitTopLevel(tokens.Skip(open + 1).Take(close - open - 1).ToArray(), ",")
            .Select(part => part.Count == 1 ? part[0].Text : "")
            .ToArray();
        if (keyColumns.Length == 0 || keyColumns.Any(value => value.Length == 0))
            return false;

        var usingPosition = FindKeyword(tokens, "USING", on + 4);
        var method = usingPosition >= 0 && usingPosition + 1 < tokens.Count
            ? tokens[usingPosition + 1].Text.ToLowerInvariant()
            : "btree";
        var predicatePresent = FindKeyword(tokens, "WHERE", close + 1) >= 0;
        index = new(tokens[indexNamePosition].Text, Signature(tokens), tokens[on + 3].Text,
            unique, method, keyColumns, predicatePresent);
        return true;
    }

    private static string Signature(IEnumerable<SqlToken> tokens)
    {
        var canonical = string.Join("\n", tokens.Select(token => $"{token.Kind}:{token.Text}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static List<List<SqlToken>> SplitTopLevel(IReadOnlyList<SqlToken> tokens, string separator)
    {
        var result = new List<List<SqlToken>>();
        var current = new List<SqlToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SqlTokenKind.Symbol && token.Text == "(")
                depth++;
            else if (token.Kind == SqlTokenKind.Symbol && token.Text == ")")
                depth--;
            if (depth == 0 && token.Kind == SqlTokenKind.Symbol && token.Text == separator)
            {
                result.Add(current);
                current = [];
            }
            else
            {
                current.Add(token);
            }
        }
        result.Add(current);
        return result;
    }

    private static int MatchingParenthesis(IReadOnlyList<SqlToken> tokens, int open)
    {
        if (!IsSymbol(tokens, open, "("))
            return -1;
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (IsSymbol(tokens, index, "("))
                depth++;
            else if (IsSymbol(tokens, index, ")") && --depth == 0)
                return index;
        }
        return -1;
    }

    private static string NormalizeType(IEnumerable<SqlToken> tokens)
    {
        var value = string.Join(" ", tokens.Select(token => token.Text.ToLowerInvariant()));
        return value.Replace(" ( ", "(", StringComparison.Ordinal)
            .Replace(" )", ")", StringComparison.Ordinal)
            .Replace(" ,", ",", StringComparison.Ordinal);
    }

    private static void FindDuplicateProperties(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    errors.Add($"Manifest has duplicate property '{path}.{property.Name}'.");
                FindDuplicateProperties(property.Value, $"{path}.{property.Name}", errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                FindDuplicateProperties(item, $"{path}[{index++}]", errors);
        }
    }

    private static void RequireString(JsonElement parent, string name, string expected, List<string> errors)
    {
        if (!TryProperty(parent, name, out var value) || value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
            errors.Add($"Manifest property '{name}' does not match the reviewed value.");
    }

    private static void RequireBoolean(JsonElement parent, string name, bool expected, List<string> errors)
    {
        if (!TryProperty(parent, name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetBoolean() != expected)
            errors.Add($"Manifest property '{name}' does not match the reviewed value.");
    }

    private static string? GetString(JsonElement parent, string name) =>
        TryProperty(parent, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static void ValidateExactProperties(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string path,
        List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Manifest object '{path}' is missing or malformed.");
            return;
        }
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            errors.Add($"Manifest object '{path}' has missing or unexpected properties.");
    }

    private static void CompareNameList(
        IEnumerable<string> actual,
        IEnumerable<string> expected,
        string label,
        List<string> errors)
    {
        var actualNames = actual.ToArray();
        var expectedNames = expected.ToArray();
        if (actualNames.Length != expectedNames.Length ||
            !actualNames.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames))
            errors.Add($"The {label} inventory does not match the reviewed catalog model.");
    }

    private static void AddDuplicateErrors(IEnumerable<string> names, string label, List<string> errors)
    {
        foreach (var duplicate in names.GroupBy(name => name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1).Select(group => group.Key))
            errors.Add($"Duplicate {label} '{duplicate}'.");
    }

    private static bool IsKeyword(IReadOnlyList<SqlToken> tokens, int index, string keyword) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == SqlTokenKind.Word &&
        string.Equals(tokens[index].Text, keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsAnyKeyword(IReadOnlyList<SqlToken> tokens, int index, params string[] keywords) =>
        keywords.Any(keyword => IsKeyword(tokens, index, keyword));

    private static bool IsIdentifier(IReadOnlyList<SqlToken> tokens, int index, string value) =>
        index >= 0 && index < tokens.Count &&
        string.Equals(tokens[index].Text.Trim('"'), value, StringComparison.OrdinalIgnoreCase);

    private static bool IsSymbol(IReadOnlyList<SqlToken> tokens, int index, string value) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == SqlTokenKind.Symbol &&
        string.Equals(tokens[index].Text, value, StringComparison.Ordinal);

    private static int FindKeyword(IReadOnlyList<SqlToken> tokens, string keyword, int start = 0)
    {
        for (var index = start; index < tokens.Count; index++)
            if (IsKeyword(tokens, index, keyword))
                return index;
        return -1;
    }

    private static int FindSymbol(IReadOnlyList<SqlToken> tokens, int start, string symbol)
    {
        for (var index = start; index < tokens.Count; index++)
            if (IsSymbol(tokens, index, symbol))
                return index;
        return -1;
    }

    private sealed record ParsedTable(
        string Name,
        string DefinitionSha256,
        IReadOnlyList<ParsedColumn> Columns,
        IReadOnlyList<ParsedConstraint> Constraints,
        IReadOnlyList<ParsedForeignKey> ForeignKeys);
    private sealed record ParsedColumn(string Name, string Type, bool Nullable);
    private sealed record ParsedConstraint(string Name, string Kind);
    private sealed record ParsedForeignKey(
        string Table,
        string Constraint,
        string ReferencedTable,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> ReferencedColumns);
    private sealed record ParsedIndex(
        string Name,
        string DefinitionSha256,
        string Table,
        bool Unique,
        string Method,
        IReadOnlyList<string> Columns,
        bool PredicatePresent);
}

internal enum SqlTokenKind { Word, String, QuotedIdentifier, Symbol }
internal sealed record SqlToken(string Text, SqlTokenKind Kind);

internal static class SqlLexer
{
    internal static IReadOnlyList<IReadOnlyList<SqlToken>> SplitStatements(string sql)
    {
        var tokens = Tokenize(sql);
        var statements = new List<IReadOnlyList<SqlToken>>();
        var current = new List<SqlToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SqlTokenKind.Symbol && token.Text == "(")
                depth++;
            else if (token.Kind == SqlTokenKind.Symbol && token.Text == ")")
            {
                if (--depth < 0)
                    throw new FormatException("Unmatched closing parenthesis.");
            }

            if (token.Kind == SqlTokenKind.Symbol && token.Text == ";" && depth == 0)
            {
                if (current.Count > 0)
                    statements.Add(current);
                current = [];
            }
            else
            {
                current.Add(token);
            }
        }
        if (depth != 0)
            throw new FormatException("Unclosed parenthesis.");
        if (current.Count > 0)
            statements.Add(current);
        return statements;
    }

    private static List<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        for (var index = 0; index < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                index++;
                continue;
            }
            if (sql[index] == '-' && HasNext(sql, index, '-') )
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n')
                    index++;
                continue;
            }
            if (sql[index] == '/' && HasNext(sql, index, '*'))
            {
                index = SkipBlockComment(sql, index + 2);
                continue;
            }
            if (sql[index] == '\'')
            {
                var end = SkipQuoted(sql, index, '\'');
                tokens.Add(new(sql[index..end], SqlTokenKind.String));
                index = end;
                continue;
            }
            if (sql[index] == '"')
            {
                var end = SkipQuoted(sql, index, '"');
                tokens.Add(new(sql[index..end], SqlTokenKind.QuotedIdentifier));
                index = end;
                continue;
            }
            if (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$')
            {
                var start = index++;
                while (index < sql.Length &&
                       (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                    index++;
                tokens.Add(new(sql[start..index], SqlTokenKind.Word));
                continue;
            }

            if (index + 1 < sql.Length && sql.AsSpan(index, 2) is var pair &&
                (pair.SequenceEqual("::") || pair.SequenceEqual("<=") || pair.SequenceEqual(">=") ||
                 pair.SequenceEqual("<>") || pair.SequenceEqual("!=") || pair.SequenceEqual("||")))
            {
                tokens.Add(new(sql.Substring(index, 2), SqlTokenKind.Symbol));
                index += 2;
                continue;
            }
            tokens.Add(new(sql[index].ToString(), SqlTokenKind.Symbol));
            index++;
        }
        return tokens;
    }

    private static int SkipBlockComment(string sql, int index)
    {
        var depth = 1;
        while (index < sql.Length && depth > 0)
        {
            if (sql[index] == '/' && HasNext(sql, index, '*'))
            {
                depth++;
                index += 2;
            }
            else if (sql[index] == '*' && HasNext(sql, index, '/'))
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }
        if (depth != 0)
            throw new FormatException("Unterminated block comment.");
        return index;
    }

    private static int SkipQuoted(string sql, int start, char quote)
    {
        var index = start + 1;
        while (index < sql.Length)
        {
            if (sql[index] == quote)
            {
                if (HasNext(sql, index, quote))
                {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        throw new FormatException("Unterminated quoted SQL token.");
    }

    private static bool HasNext(string sql, int index, char expected) =>
        index + 1 < sql.Length && sql[index + 1] == expected;
}
