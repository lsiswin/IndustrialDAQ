using System.Collections.ObjectModel;
using IndustrialDAQ.Core.Authorization;
using IndustrialDAQ.Infrastructure.Entities;
using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>工业上位机账号与角色管理，所有变更只允许管理员执行。</summary>
public sealed class UserAccessManagementViewModel : BindableBase
{
    private readonly IUserRepository _repository;
    private readonly IAuthManager _authManager;
    private readonly SecurityAuditService _auditService;
    private UserAccessItem? _selectedUser;
    private string _statusText = "选择账号后可调整角色和启用状态";

    public ObservableCollection<UserAccessItem> Users { get; } = [];
    public ObservableCollection<SecurityAuditEntity> Audits { get; } = [];
    public IReadOnlyList<string> Roles { get; } = ["Guest", "Engineer", "Admin"];
    public UserAccessItem? SelectedUser { get => _selectedUser; set { SetProperty(ref _selectedUser, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool IsAdministrator => _authManager.IsAdministrator;
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand SaveCommand { get; }

    public UserAccessManagementViewModel(IUserRepository repository, IAuthManager authManager, SecurityAuditService auditService)
    {
        _repository = repository;
        _authManager = authManager;
        _auditService = auditService;
        RefreshCommand = new DelegateCommand(LoadAsync);
        SaveCommand = new DelegateCommand(SaveAsync, () => IsAdministrator && SelectedUser is not null);
        _authManager.CurrentUserChanged += (_, _) => { RaisePropertyChanged(nameof(IsAdministrator)); SaveCommand.RaiseCanExecuteChanged(); _ = LoadUsersAsync(); };
        _ = LoadAllAsync();
    }

    private async void LoadAsync() => await LoadAllAsync();
    private async Task LoadAllAsync()
    {
        await LoadUsersAsync();
        await LoadAuditsAsync();
    }

    private async Task LoadAuditsAsync()
    {
        var audits = await _auditService.GetRecentAsync();
        Audits.Clear();
        foreach (var audit in audits) Audits.Add(audit);
    }
    private async Task LoadUsersAsync()
    {
        var users = await _repository.GetAllAsync();
        Users.Clear();
        foreach (var user in users.OrderBy(item => item.Username))
            Users.Add(new UserAccessItem(user.Id, user.Username, user.RealName, user.Roles.FirstOrDefault() ?? "Guest", user.IsActive, user.CreatedAtUtc));
        StatusText = $"共 {Users.Count} 个实名账号";
    }

    private async void SaveAsync()
    {
        if (SelectedUser is null) return;
        var result = await _authManager.UpdateUserAccessAsync(SelectedUser.Id, SelectedUser.Role, SelectedUser.IsActive);
        StatusText = result.Message;
        if (result.Success) await LoadAllAsync();
    }
}

public sealed class UserAccessItem : BindableBase
{
    private string _role;
    private bool _isActive;
    public string Id { get; }
    public string Username { get; }
    public string RealName { get; }
    public DateTime CreatedAtUtc { get; }
    public string Role { get => _role; set => SetProperty(ref _role, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public UserAccessItem(string id, string username, string realName, string role, bool isActive, DateTime createdAtUtc) =>
        (Id, Username, RealName, _role, _isActive, CreatedAtUtc) = (id, username, realName, role, isActive, createdAtUtc);
}
