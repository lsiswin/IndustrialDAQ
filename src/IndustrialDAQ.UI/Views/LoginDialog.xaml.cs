using System.Windows.Controls;
using IndustrialDAQ.UI.ViewModels;

namespace IndustrialDAQ.UI.Views;

public partial class LoginDialog : UserControl
{
    public LoginDialog()
    {
        InitializeComponent();
    }

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        // PasswordBox 不支持普通双向绑定，仅将输入同步到当前对话框模型，避免密码明文显示。
        if (DataContext is LoginDialogViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.Password = passwordBox.Password;
    }
}
