using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

public class LoginDialogViewModel : BindableBase, IDialogAware
{
    private readonly IAuthManager _authManager;
    private readonly IDialogService _dialogService;

    private string _username = "";
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    private string _password = "";
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            SetProperty(ref _errorMessage, value);
            RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string Title => "身份验证";

    public DialogCloseListener RequestClose { get; }

    public DelegateCommand LoginCommand { get; }
    public DelegateCommand ShowRegisterCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public LoginDialogViewModel(IAuthManager authManager, IDialogService dialogService)
    {
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        LoginCommand = new DelegateCommand(OnLoginExecute, CanLoginExecute)
            .ObservesProperty(() => Username)
            .ObservesProperty(() => Password);
        ShowRegisterCommand = new DelegateCommand(() => _dialogService.ShowDialog("RegisterDialog"));
        CancelCommand = new DelegateCommand(OnCancelExecute);
    }

    private bool CanLoginExecute()
    {
        return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
    }

    private async void OnLoginExecute()
    {
        ErrorMessage = string.Empty;
        var success = await _authManager.LoginAsync(Username, Password);
        if (success)
        {
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        else
        {
            ErrorMessage = "用户名或密码错误，或用户被禁用。";
        }
    }

    private void OnCancelExecute()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }

    public bool CanCloseDialog() => true;

    public void OnDialogClosed() { }

    public void OnDialogOpened(IDialogParameters parameters) { }
}
