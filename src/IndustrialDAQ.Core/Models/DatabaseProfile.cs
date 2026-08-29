namespace IndustrialDAQ.Core.Models;

/// <summary>客户端可维护的数据库连接档案；敏感密码只保存环境变量名称。</summary>
public sealed record DatabaseProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新数据库";
    public string Provider { get; init; } = "SQLite";
    public string SqlitePath { get; init; } = string.Empty;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = "industrialdaq";
    public string Username { get; init; } = "postgres";
    public string PasswordEnvironmentVariable { get; init; } = string.Empty;

    public static DatabaseProfile CreateDefaultSqlite() => new()
    {
        Id = "sqlite-default",
        Name = "本机 SQLite",
        Provider = "SQLite"
    };
}
