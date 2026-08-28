using System.Security.Cryptography;
using IndustrialDAQ.Core.Authorization;

namespace IndustrialDAQ.UI.Services;

public class AuthManager : IAuthManager
{
    private readonly IUserRepository _userRepository;
    private readonly SecurityAuditService _auditService;

    private static readonly User GuestUser = new User
    {
        Id = "guest",
        Username = "Guest",
        RealName = "访客",
        Roles = new List<string> { "Guest" }
    };

    public User CurrentUser { get; private set; } = GuestUser;

    public bool IsAuthenticated => CurrentUser.Id != GuestUser.Id;

    public bool CanModify => IsAuthenticated &&
        CurrentUser.Roles.Any(role => role is "Admin" or "Engineer");

    public bool IsAdministrator => IsAuthenticated && CurrentUser.Roles.Contains("Admin");

    /// <inheritdoc />
    public event EventHandler? CurrentUserChanged;

    public AuthManager(IUserRepository userRepository, SecurityAuditService auditService)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _auditService = auditService;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        // 工业终端连续失败 5 次后锁定 15 分钟，降低现场共享终端的暴力尝试风险。
        var user = await _userRepository.FindByUsernameAsync(username);
        if (user?.LockedUntilUtc > DateTime.UtcNow) return false;

        if (user is not null && VerifyPassword(password, user.PasswordHash) && user.IsActive)
        {
            CurrentUser = user;
            user.FailedLoginCount = 0;
            user.LockedUntilUtc = null;
            user.LastLoginAtUtc = DateTime.UtcNow;
            await _userRepository.UpsertAsync(user);
            CurrentUserChanged?.Invoke(this, EventArgs.Empty);
            await _auditService.RecordAsync(user.Id, user.Username, "Login", "/system/auth", "登录成功", true);
            return true;
        }

        if (user is not null)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= 5) { user.FailedLoginCount = 0; user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15); }
            await _userRepository.UpsertAsync(user);
            await _auditService.RecordAsync(user.Id, user.Username, "LoginFailed", "/system/auth", "登录失败", false);
        }
        return false;
    }

    public async Task<(bool Success, string Message)> RegisterAsync(
        string username, string password, string realName, string role)
    {
        username = username.Trim();
        realName = realName.Trim();
        role = role.Trim();

        if (username.Length < 3)
            return (false, "用户名至少需要 3 个字符。");
        if (password.Length < 8 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            return (false, "密码至少 8 位，并同时包含大写字母、小写字母和数字。");
        // 工业上位机禁止匿名用户自助提升权限，新账号统一以访客身份创建。
        role = "Guest";
        if (await _userRepository.FindByUsernameAsync(username) is not null)
            return (false, "该用户名已经存在。");

        // 使用 PBKDF2 加盐保存密码，数据库中不存储明文密码。
        await _userRepository.UpsertAsync(new User
        {
            Username = username,
            RealName = string.IsNullOrWhiteSpace(realName) ? username : realName,
            PasswordHash = HashPassword(password),
            Roles = [role],
            IsActive = true
        });
        return (true, "注册成功，请使用新账号登录。");
    }

    public async Task<(bool Success, string Message)> UpdateUserAccessAsync(string userId, string role, bool isActive)
    {
        if (!IsAdministrator) return (false, "仅管理员可以维护账号权限。");
        if (userId == CurrentUser.Id) return (false, "不能在当前会话中修改自己的角色或状态。");
        if (role is not ("Guest" or "Engineer" or "Admin")) return (false, "无效的账号角色。");

        var user = await _userRepository.FindByIdAsync(userId);
        if (user is null) return (false, "账号不存在。");
        await _userRepository.UpsertAsync(new User
        {
            Id = user.Id, Username = user.Username, RealName = user.RealName,
            PasswordHash = user.PasswordHash, Roles = [role], IsActive = isActive,
            CreatedAtUtc = user.CreatedAtUtc, FailedLoginCount = user.FailedLoginCount,
            LockedUntilUtc = user.LockedUntilUtc, MustChangePassword = user.MustChangePassword,
            LastLoginAtUtc = user.LastLoginAtUtc
        });
        await _auditService.RecordAsync(CurrentUser.Id, CurrentUser.Username, "PermissionChanged", "/system/users/" + user.Id, $"角色={role};启用={isActive}", true);
        return (true, "账号权限已更新。");
    }

    public void Logout()
    {
        var previous = CurrentUser;
        CurrentUser = GuestUser;
        CurrentUserChanged?.Invoke(this, EventArgs.Empty);
        if (previous.Id != GuestUser.Id)
            _ = _auditService.RecordAsync(previous.Id, previous.Username, "Logout", "/system/auth", "主动退出登录", true);
    }


    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        if (!storedHash.StartsWith("PBKDF2$", StringComparison.Ordinal))
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(password),
                System.Text.Encoding.UTF8.GetBytes(storedHash));

        var parts = storedHash.Split('$');
        if (parts.Length != 3) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

}
