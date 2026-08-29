using IndustrialDAQ.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndustrialDAQ.Infrastructure;

/// <summary>将数据库档案转换为 EF Core 配置，并提供不写入业务数据的连接测试。</summary>
public static class DatabaseProfileConnection
{
    public static void Configure(DbContextOptionsBuilder options, DatabaseProfile profile)
    {
        if (IsPostgreSql(profile.Provider))
        {
            options.UseNpgsql(BuildPostgreSqlConnectionString(profile), npgsql =>
                npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
            return;
        }

        options.UseSqlite(BuildSqliteConnectionString(profile));
    }

    public static async Task TestAsync(DatabaseProfile profile, CancellationToken cancellationToken = default)
    {
        if (IsPostgreSql(profile.Provider))
        {
            await using var connection = new NpgsqlConnection(BuildPostgreSqlConnectionString(profile));
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return;
        }

        await using var sqlite = new SqliteConnection(BuildSqliteConnectionString(profile));
        await sqlite.OpenAsync(cancellationToken);
        await using var sqliteCommand = sqlite.CreateCommand();
        sqliteCommand.CommandText = "SELECT 1";
        await sqliteCommand.ExecuteScalarAsync(cancellationToken);
    }

    public static string Describe(DatabaseProfile profile) => IsPostgreSql(profile.Provider)
        ? $"PostgreSQL · {profile.Host}:{profile.Port}/{profile.Database}"
        : $"SQLite · {ResolveSqlitePath(profile.SqlitePath)}";

    public static bool IsPostgreSql(string? provider) =>
        string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, "Npgsql", StringComparison.OrdinalIgnoreCase);

    private static string BuildPostgreSqlConnectionString(DatabaseProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Database) || string.IsNullOrWhiteSpace(profile.Username))
            throw new InvalidOperationException("PostgreSQL 主机、数据库和用户名不能为空。");
        if (profile.Port is < 1 or > 65535) throw new InvalidOperationException("PostgreSQL 端口无效。");
        if (string.IsNullOrWhiteSpace(profile.PasswordEnvironmentVariable))
            throw new InvalidOperationException("PostgreSQL 必须配置密码环境变量名称。");

        var password = Environment.GetEnvironmentVariable(profile.PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"未设置数据库密码环境变量 '{profile.PasswordEnvironmentVariable}'。");

        return new NpgsqlConnectionStringBuilder
        {
            Host = profile.Host,
            Port = profile.Port,
            Database = profile.Database,
            Username = profile.Username,
            Password = password,
            Pooling = true,
            Timeout = 10,
            CommandTimeout = 30
        }.ConnectionString;
    }

    private static string BuildSqliteConnectionString(DatabaseProfile profile) =>
        new SqliteConnectionStringBuilder { DataSource = ResolveSqlitePath(profile.SqlitePath) }.ConnectionString;

    private static string ResolveSqlitePath(string? configuredPath) => string.IsNullOrWhiteSpace(configuredPath)
        ? Path.Combine(AppContext.BaseDirectory, "industrialdaq.db")
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
}
