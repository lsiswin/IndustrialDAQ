using System.Security.Cryptography;
using IndustrialDAQ.Core.Authorization;

namespace IndustrialDAQ.UI.Services;

public class AuthManager : IAuthManager
{
    private readonly IUserRepository _userRepository;

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

    public AuthManager(IUserRepository userRepository)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        var user = await _userRepository.FindByUsernameAsync(username);

        if (user is not null && VerifyPassword(password, user.PasswordHash) && user.IsActive)
        {
            CurrentUser = user;
            CurrentUserChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // 用于演示/开发环境：如果是 admin / admin
        if (username.Equals("admin", StringComparison.OrdinalIgnoreCase) && password == "admin")
        {
            CurrentUser = new User
            {
                Id = "admin-sys",
                Username = "admin",
                RealName = "系统管理员",
                Roles = new List<string> { "Admin", "Operator" }
            };
            CurrentUserChanged?.Invoke(this, EventArgs.Empty);
            return true;
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
        if (password.Length < 6)
            return (false, "密码至少需要 6 个字符。");
        if (role is not ("Guest" or "Engineer"))
            return (false, "自助注册仅支持访客或工程师身份。");
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

    public void Logout()
    {
        CurrentUser = GuestUser;
        CurrentUserChanged?.Invoke(this, EventArgs.Empty);
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
