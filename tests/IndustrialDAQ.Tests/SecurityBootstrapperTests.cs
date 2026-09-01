using System.Security.Cryptography;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Authorization;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Tests;

/// <summary>验证跨数据库安全初始化不会遗漏管理员，也不会覆盖已有密码。</summary>
public sealed class SecurityBootstrapperTests
{
    [Fact]
    public async Task Bootstrapper_CreatesAdminRoleAndPermissions_WithoutResettingPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), "industrialdaq-security-bootstrap-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<DaqDbContext>().UseSqlite("Data Source=" + path).Options;
        await using var db = new DaqDbContext(options);
        await db.Database.EnsureCreatedAsync();

        SecurityBootstrapper.Ensure(db);
        var admin = await db.Users.SingleAsync(item => item.Username == SecurityBootstrapper.InitialAdminUsername);
        var originalHash = admin.PasswordHash;
        Assert.True(VerifyPassword(SecurityBootstrapper.InitialAdminPassword, originalHash));
        Assert.Contains(await db.UserRoles.ToListAsync(), item => item.UserId == admin.Id && item.RoleId == "role-admin");
        Assert.Contains(await db.PermissionPolicies.ToListAsync(), item => item.SubjectId == "Admin" && item.ResourcePath == "Vision");

        SecurityBootstrapper.Ensure(db);
        Assert.Equal(originalHash, (await db.Users.SingleAsync(item => item.Id == admin.Id)).PasswordHash);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
