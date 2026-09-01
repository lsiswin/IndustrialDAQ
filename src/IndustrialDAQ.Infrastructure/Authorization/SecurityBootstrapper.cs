using System.Security.Cryptography;
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Infrastructure.Authorization;

/// <summary>为 SQLite 和 PostgreSQL 统一初始化内置角色、管理员账号和基础权限。</summary>
public static class SecurityBootstrapper
{
    public const string InitialAdminUsername = "admin";
    public const string InitialAdminPassword = "Admin#2026";

    public static void Ensure(DaqDbContext db)
    {
        var roles = new[]
        {
            new RoleEntity { Id = "role-guest", Name = "Guest", Description = "只读访客", IsBuiltIn = true },
            new RoleEntity { Id = "role-operator", Name = "Operator", Description = "生产操作员", IsBuiltIn = true },
            new RoleEntity { Id = "role-engineer", Name = "Engineer", Description = "工程维护人员", IsBuiltIn = true },
            new RoleEntity { Id = "role-admin", Name = "Admin", Description = "系统安全管理员", IsBuiltIn = true }
        };
        foreach (var role in roles)
            if (!db.Roles.Any(item => item.Id == role.Id)) db.Roles.Add(role);

        var admin = db.Users.FirstOrDefault(item => item.Username == InitialAdminUsername);
        if (admin is null)
        {
            admin = new UserEntity
            {
                Id = "admin-sys", Username = InitialAdminUsername,
                PasswordHash = HashPassword(InitialAdminPassword), RealName = "系统管理员",
                CreatedAtUtc = DateTime.UtcNow, IsActive = true, MustChangePassword = true
            };
            db.Users.Add(admin);
        }
        if (!db.UserRoles.Any(item => item.UserId == admin.Id && item.RoleId == "role-admin"))
            db.UserRoles.Add(new UserRoleEntity { UserId = admin.Id, RoleId = "role-admin" });

        AddPolicyIfMissing(db, "builtin-engineer-device-write", "Engineer", "Devices", "Write");
        AddPolicyIfMissing(db, "builtin-admin-device-all", "Admin", "Devices", "*");
        AddPolicyIfMissing(db, "builtin-admin-system-all", "Admin", "System", "*");
        AddPolicyIfMissing(db, "builtin-admin-vision-all", "Admin", "Vision", "*");
        db.SaveChanges();
    }

    private static void AddPolicyIfMissing(
        DaqDbContext db, string id, string role, string resourcePath, string action)
    {
        if (db.PermissionPolicies.Local.Any(item => item.Id == id) || db.PermissionPolicies.Any(item => item.Id == id)) return;
        db.PermissionPolicies.Add(new PermissionPolicyEntity
        {
            Id = id, SubjectType = "Role", SubjectId = role, ResourcePath = resourcePath,
            Action = action, Effect = "Allow", Inherit = true, Priority = 100,
            IsEnabled = true, Version = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        });
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}
