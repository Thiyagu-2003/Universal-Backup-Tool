using System.Windows;
using System.Windows.Input;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;
    private bool _isClosing;
    private bool _isPasswordVisible;

    public LoginWindow() : this(new LoginViewModel(new ConfigurationService(new CredentialProtector())))
    {
    }

    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.SignedIn += (_, _) =>
        {
            if (_isClosing) return;
            _isClosing = true;
            try
            {
                DialogResult = true;
            }
            catch
            {
                Close();
            }
        };

        Loaded += (_, _) => PasswordInput.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseWindow(this, new RoutedEventArgs());
            }
        };
    }

    private void ToggleEye_Click(object sender, RoutedEventArgs e)
    {
        _isPasswordVisible = !_isPasswordVisible;

        if (_isPasswordVisible)
        {
            PasswordVisibleInput.Text = PasswordInput.Password;
            PasswordInput.Visibility = Visibility.Collapsed;
            PasswordVisibleInput.Visibility = Visibility.Visible;
            EyeIconPath.Data = (System.Windows.Media.Geometry)FindResource("IconEyeOff");
            EyeIconPath.Fill = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
            PasswordVisibleInput.Focus();
            PasswordVisibleInput.CaretIndex = PasswordVisibleInput.Text.Length;
        }
        else
        {
            PasswordInput.Password = PasswordVisibleInput.Text;
            PasswordVisibleInput.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
            EyeIconPath.Data = (System.Windows.Media.Geometry)FindResource("IconEye");
            EyeIconPath.Fill = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            PasswordInput.Focus();
        }
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isPasswordVisible && PasswordVisibleInput.Text != PasswordInput.Password)
        {
            PasswordVisibleInput.Text = PasswordInput.Password;
        }
    }

    private void PasswordVisibleInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isPasswordVisible && PasswordInput.Password != PasswordVisibleInput.Text)
        {
            PasswordInput.Password = PasswordVisibleInput.Text;
        }
    }

    private void PasswordInputContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isPasswordVisible)
        {
            PasswordVisibleInput.Focus();
        }
        else
        {
            PasswordInput.Focus();
        }
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        try
        {
            DialogResult = false;
        }
        catch
        {
            Close();
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        try
        {
            _viewModel.Password = _isPasswordVisible ? PasswordVisibleInput.Text : PasswordInput.Password;
            await _viewModel.SignInCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Sign-in failed");
        }
    }

    private void PasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SignIn_Click(sender, e);
        }
    }
}
