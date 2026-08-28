namespace IndustrialDAQ.Core.Authorization;

/// <summary>
/// 系统用户领域模型。
/// </summary>
public sealed class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Username { get; init; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string RealName { get; init; } = string.Empty;
    
    // 简化的角色列表，实际应用中可以映射到 RoleIds
    public List<string> Roles { get; init; } = new();

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    public PermissionSubject ToSubject()
    {
        return new PermissionSubject
        {
            UserId = Id,
            RoleIds = new HashSet<string>(Roles, StringComparer.OrdinalIgnoreCase),
            IsSystem = false
        };
    }
}
