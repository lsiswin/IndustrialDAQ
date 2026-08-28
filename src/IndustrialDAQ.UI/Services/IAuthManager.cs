using IndustrialDAQ.Core.Authorization;

namespace IndustrialDAQ.UI.Services;

/// <summary>
/// 客户端身份验证管理器接口
/// </summary>
public interface IAuthManager
{
    /// <summary>当前登录用户发生变化。</summary>
    event EventHandler? CurrentUserChanged;

    /// <summary>
    /// 当前登录的用户（如果未登录则为默认访客或系统用户）
    /// </summary>
    User CurrentUser { get; }

    /// <summary>当前是否为已登录账号。</summary>
    bool IsAuthenticated { get; }

    /// <summary>当前账号是否允许修改运行数据或工程配置。</summary>
    bool CanModify { get; }

    /// <summary>当前账号是否为管理员。</summary>
    bool IsAdministrator { get; }

    /// <summary>
    /// 尝试登录
    /// </summary>
    Task<bool> LoginAsync(string username, string password);

    /// <summary>注册访客或工程师账号，管理员账号只能由管理员维护。</summary>
    Task<(bool Success, string Message)> RegisterAsync(
        string username, string password, string realName, string role);

    /// <summary>
    /// 登出
    /// </summary>
    void Logout();
}
