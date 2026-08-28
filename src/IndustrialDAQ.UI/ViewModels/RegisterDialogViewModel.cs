using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>独立账号注册弹窗，仅允许创建访客和工程师账号。</summary>
public sealed class RegisterDialogViewModel : BindableBase, IDialogAware
{
    private readonly IAuthManager _authManager;
    private string _username = string.Empty;
    private string _realName = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _selectedRole = "Guest";
    private string _errorMessage = string.Empty;

    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string RealName { get => _realName; set => SetProperty(ref _realName, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }
    public string SelectedRole { get => _selectedRole; set => SetProperty(ref _selectedRole, value); }
    public string ErrorMessage { get => _errorMessage; set { SetProperty(ref _errorMessage, value); RaisePropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public IReadOnlyList<RoleOption> RegisterRoles { get; } =
    [new("Guest", "访客"), new("Engineer", "工程师")];

    public DelegateCommand RegisterCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand BackToLoginCommand { get; }
    public DialogCloseListener RequestClose { get; }

    public RegisterDialogViewModel(IAuthManager authManager)
    {
        _authManager = authManager;
        RegisterCommand = new DelegateCommand(RegisterAsync, CanRegister)
            .ObservesProperty(() => Username).ObservesProperty(() => Password).ObservesProperty(() => ConfirmPassword);
        CancelCommand = new DelegateCommand(() => RequestClose.Invoke(new DialogResult(ButtonResult.Cancel)));
        // 注册弹窗由登录弹窗通过 Prism 打开，关闭后立即返回原登录弹窗。
        BackToLoginCommand = new DelegateCommand(() => RequestClose.Invoke(new DialogResult(ButtonResult.Cancel)));
    }

    private bool CanRegister() => !string.IsNullOrWhiteSpace(Username) &&
                                  !string.IsNullOrWhiteSpace(Password) &&
                                  !string.IsNullOrWhiteSpace(ConfirmPassword);

    private async void RegisterAsync()
    {
        if (Password != ConfirmPassword) { ErrorMessage = "两次输入的密码不一致。"; return; }
        var result = await _authManager.RegisterAsync(Username, Password, RealName, SelectedRole);
        ErrorMessage = result.Message;
        if (result.Success) RequestClose.Invoke(new DialogResult(ButtonResult.OK));
    }

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }
    public void OnDialogOpened(IDialogParameters parameters) { }
}

public sealed record RoleOption(string Id, string Name);
