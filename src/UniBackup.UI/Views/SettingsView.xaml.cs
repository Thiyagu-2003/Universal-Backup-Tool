using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += SettingsView_DataContextChanged;
    }

    private void SettingsView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        }
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel vm && e.PropertyName == nameof(MainViewModel.IsSecurityPasswordVisible))
        {
            if (!vm.IsSecurityPasswordVisible)
            {
                if (SecNewPasswordBox != null && SecNewPasswordBox.Password != vm.SecurityNewPassword)
                {
                    SecNewPasswordBox.Password = vm.SecurityNewPassword;
                }
                if (SecConfirmPasswordBox != null && SecConfirmPasswordBox.Password != vm.SecurityConfirmPassword)
                {
                    SecConfirmPasswordBox.Password = vm.SecurityConfirmPassword;
                }
            }
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string theme)
        {
            App.ApplyTheme(theme);
        }
    }

    private void SecurityMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string mode && DataContext is MainViewModel vm)
        {
            vm.SecurityAuthMode = mode;
        }
    }

    private void SecNewPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && DataContext is MainViewModel vm)
        {
            vm.SecurityNewPassword = pb.Password;
        }
    }

    private void SecConfirmPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && DataContext is MainViewModel vm)
        {
            vm.SecurityConfirmPassword = pb.Password;
        }
    }
}
