using System.Data;
using System.Security.Cryptography;
using Npgsql;

namespace VirtuousGateway.CiMigrationRehearsal;

internal static class CiMigrationRehearsal
{
    private const string DatabaseName = "vg_v0001_rehearsal";
    private const string BootstrapRole = "vg_rehearsal_bootstrap";
    private const string MigratorRole = "vg_v0001_migrator";
    private const long SyntheticGiftId = 900001;

    internal static async Task RunAsync(string bootstrapPassword, MigrationArtifacts artifacts)
    {
        await using var bootstrap = new NpgsqlConnection(
            BuildConnectionString(BootstrapRole, bootstrapPassword));
        await bootstrap.OpenAsync();
        await VerifyConnectionAsync(bootstrap, BootstrapRole, requireSuperuser: true);
        await VerifyScramHostAuthenticationAsync(bootstrap);
        await VerifyEmptyPublicSchemaAsync(bootstrap);
        var migratorPassword = await ProvisionSyntheticBaselineAsync(bootstrap);

        await using var migrator = new NpgsqlConnection(
            BuildConnectionString(MigratorRole, migratorPassword));
        await migrator.OpenAsync();
        await VerifyConnectionAsync(migrator, MigratorRole, requireSuperuser: false);
        await VerifySyntheticBaselineAsync(migrator);

        await using var transaction = await migrator.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var migration = new NpgsqlCommand(artifacts.Sql, migrator, transaction))
        {
            await migration.ExecuteNonQueryAsync();
        }

        await VerifyMigratedCatalogAsync(migrator, transaction, artifacts);
        await VerifySyntheticBaselineAsync(migrator, transaction);
        await transaction.RollbackAsync();

        await VerifyRolledBackCatalogAsync(migrator, artifacts);
        await VerifySyntheticBaselineAsync(migrator);
    }

    private static string BuildConnectionString(string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "postgres",
            Port = 5432,
            Database = DatabaseName,
            Username = role,
            Password = password,
            SslMode = SslMode.Disable,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 30,
            IncludeErrorDetail = false,
            SearchPath = "pg_catalog,public"
        };
        return builder.ConnectionString;
    }

    private static async Task VerifyConnectionAsync(
        NpgsqlConnection connection,
        string expectedRole,
        bool requireSuperuser)
    {
        const string targetSql = """
            SELECT current_database(),
                   session_user,
                   current_setting('server_version_num')::integer / 10000,
                   pg_catalog.pg_encoding_to_char(encoding)
            FROM pg_catalog.pg_database
            WHERE datname = current_database();
            """;
        await using (var command = new NpgsqlCommand(targetSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()
                || reader.GetString(0) != DatabaseName
                || reader.GetString(1) != expectedRole
                || reader.GetInt32(2) != 17
                || reader.GetString(3) != "UTF8")
            {
                throw new InvalidOperationException("The fixed disposable PostgreSQL target failed identity checks.");
            }
        }

        const string roleSql = """
            SELECT rolcanlogin, rolinherit, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls
            FROM pg_catalog.pg_roles
            WHERE rolname = current_user;
            """;
        await using (var command = new NpgsqlCommand(roleSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("The expected rehearsal role is missing.");
            }

            var actualSuperuser = reader.GetBoolean(2);
            if (actualSuperuser != requireSuperuser
                || (!requireSuperuser
                    && (!reader.GetBoolean(0)
                        || reader.GetBoolean(1)
                        || reader.GetBoolean(3)
                        || reader.GetBoolean(4)
                        || reader.GetBoolean(5)
                        || reader.GetBoolean(6))))
            {
                throw new InvalidOperationException("The rehearsal role privilege boundary is invalid.");
            }
        }

        if (!requireSuperuser)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles role ON role.oid = membership.member
                WHERE role.rolname = current_user;
                """,
                connection);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            {
                throw new InvalidOperationException("The migration role has unexpected role memberships.");
            }

            await using var databasePrivileges = new NpgsqlCommand(
                """
                SELECT                 has_database_privilege(current_user, current_database(), 'CREATE'),
                has_database_privilege(current_user, current_database(), 'TEMP'),
                count(*) FILTER (
                    WHERE db.datname <> current_database()
                      AND has_database_privilege(current_user, db.datname, 'CREATE')
                )
                FROM pg_catalog.pg_database db;
                """,
                connection);
            await using var privilegeReader = await databasePrivileges.ExecuteReaderAsync();
            if (!await privilegeReader.ReadAsync()
                || privilegeReader.GetBoolean(0)
                || privilegeReader.GetBoolean(1)
                || privilegeReader.GetInt64(2) != 0)
            {
                throw new InvalidOperationException("The migration role has unexpected database-level privileges.");
            }
        }
    }

    private static async Task VerifyScramHostAuthenticationAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_catalog.pg_hba_file_rules
            WHERE type IN ('host', 'hostssl', 'hostnossl')
              AND auth_method <> 'scram-sha-256';
            """,
            connection);
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
        {
            throw new InvalidOperationException("The disposable PostgreSQL service does not enforce SCRAM host authentication.");
        }
    }

    private static async Task VerifyEmptyPublicSchemaAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'public'
              AND relation.relkind IN ('r', 'p', 'v', 'm', 'S', 'f');
            """,
            connection);
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
        {
            throw new InvalidOperationException("The disposable public schema is not empty.");
        }
    }

    private static async Task<string> ProvisionSyntheticBaselineAsync(NpgsqlConnection bootstrap)
    {
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await ExecuteAsync(bootstrap, "SET password_encryption = 'scram-sha-256';");
        await ExecuteAsync(
            bootstrap,
            $"CREATE ROLE {MigratorRole} LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;");
        await VerifyStoredScramPasswordAsync(bootstrap);
        await ExecuteAsync(bootstrap, $"REVOKE ALL PRIVILEGES ON DATABASE {DatabaseName} FROM PUBLIC;");
        await ExecuteAsync(bootstrap, $"GRANT CONNECT ON DATABASE {DatabaseName} TO {MigratorRole};");
        await ExecuteAsync(
            bootstrap,
            "CREATE TABLE public.virtuous_gift_processing (gift_id bigint PRIMARY KEY);");
        await using (var insert = new NpgsqlCommand(
                         "INSERT INTO public.virtuous_gift_processing (gift_id) VALUES (@giftId);",
                         bootstrap))
        {
            insert.Parameters.AddWithValue("giftId", SyntheticGiftId);
            await insert.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(bootstrap, $"GRANT USAGE, CREATE ON SCHEMA public TO {MigratorRole};");
        await ExecuteAsync(
            bootstrap,
            $"GRANT SELECT, REFERENCES (gift_id) ON TABLE public.virtuous_gift_processing TO {MigratorRole};");
        return migratorPassword;
    }

    private static async Task VerifyStoredScramPasswordAsync(NpgsqlConnection bootstrap)
    {
        await using var command = new NpgsqlCommand(
            "SELECT rolpassword LIKE 'SCRAM-SHA-256$%' FROM pg_catalog.pg_authid WHERE rolname = @role;",
            bootstrap);
        command.Parameters.AddWithValue("role", MigratorRole);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync()))
        {
            throw new InvalidOperationException("The migration role password was not stored as SCRAM-SHA-256.");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task VerifySyntheticBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            "SELECT gift_id FROM public.virtuous_gift_processing ORDER BY gift_id;",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetInt64(0) != SyntheticGiftId
            || await reader.ReadAsync())
        {
            throw new InvalidOperationException("Synthetic prerequisite data was changed or is incomplete.");
        }
    }

    private static async Task VerifyMigratedCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationArtifacts artifacts)
    {
        var expectedTables = artifacts.Tables.Append("virtuous_gift_processing").ToHashSet(StringComparer.Ordinal);
        var actualTables = await ReadSetAsync(
            connection,
            transaction,
            """
            SELECT relation.relname
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'public' AND relation.relkind = 'r';
            """);
        RequireSameSet(expectedTables, actualTables, "table");

        var expectedConstraints = artifacts.Constraints.Append("virtuous_gift_processing_pkey")
            .ToHashSet(StringComparer.Ordinal);
        var actualConstraints = await ReadSetAsync(
            connection,
            transaction,
            """
            SELECT constraint_row.conname
            FROM pg_catalog.pg_constraint constraint_row
            JOIN pg_catalog.pg_namespace schema ON schema.oid = constraint_row.connamespace
            WHERE schema.nspname = 'public';
            """);
        RequireSameSet(expectedConstraints, actualConstraints, "constraint");

        var expectedIndexes = artifacts.Constraints
            .Where(name => name.StartsWith("pk_", StringComparison.Ordinal)
                || name.StartsWith("uq_", StringComparison.Ordinal))
            .Append("virtuous_gift_processing_pkey")
            .Concat(artifacts.ExplicitIndexes)
            .ToHashSet(StringComparer.Ordinal);
        var actualIndexes = await ReadSetAsync(
            connection,
            transaction,
            """
            SELECT index_row.indexname
            FROM pg_catalog.pg_indexes index_row
            WHERE index_row.schemaname = 'public';
            """);
        RequireSameSet(expectedIndexes, actualIndexes, "index");
    }

    private static async Task VerifyRolledBackCatalogAsync(
        NpgsqlConnection connection,
        MigrationArtifacts artifacts)
    {
        var actualTables = await ReadSetAsync(
            connection,
            transaction: null,
            """
            SELECT relation.relname
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'public' AND relation.relkind = 'r';
            """);
        RequireSameSet(
            new HashSet<string>(["virtuous_gift_processing"], StringComparer.Ordinal),
            actualTables,
            "post-rollback table");

        foreach (var table in artifacts.Tables)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_catalog.to_regclass(@name) IS NULL;",
                connection);
            command.Parameters.AddWithValue("name", $"public.{table}");
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync()))
            {
                throw new InvalidOperationException("A migration-created table remained after rollback.");
            }
        }

        foreach (var index in artifacts.ExplicitIndexes)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_catalog.to_regclass(@name) IS NULL;",
                connection);
            command.Parameters.AddWithValue("name", $"public.{index}");
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync()))
            {
                throw new InvalidOperationException("A migration-created index remained after rollback.");
            }
        }
    }

    private static async Task<HashSet<string>> ReadSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void RequireSameSet(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual,
        string objectType)
    {
        if (!expected.SetEquals(actual))
        {
            throw new InvalidOperationException($"The V0001 {objectType} catalog does not match the reviewed inventory.");
        }
    }
}
