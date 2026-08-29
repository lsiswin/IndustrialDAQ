using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Tests;

public sealed class DatabaseProfileConnectionTests
{
    [Fact]
    public async Task Sqlite_profile_can_be_configured_and_connected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"industrialdaq-{Guid.NewGuid():N}.db");
        try
        {
            var profile = DatabaseProfile.CreateDefaultSqlite() with { SqlitePath = path };
            var builder = new DbContextOptionsBuilder<DaqDbContext>();

            DatabaseProfileConnection.Configure(builder, profile);
            await DatabaseProfileConnection.TestAsync(profile);

            await using var context = new DaqDbContext(builder.Options);
            Assert.True(context.Database.IsSqlite());
        }
        finally
        {
            // 测试结束主动清空连接池，避免 Windows 文件锁影响临时库删除。
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task PostgreSql_profile_requires_password_environment_variable()
    {
        var variable = $"INDUSTRIALDAQ_TEST_PASSWORD_{Guid.NewGuid():N}";
        var profile = new DatabaseProfile
        {
            Provider = "PostgreSQL",
            PasswordEnvironmentVariable = variable
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseProfileConnection.TestAsync(profile));

        Assert.Contains(variable, error.Message);
    }
}
