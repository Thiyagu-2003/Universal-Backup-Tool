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
        if (e.OldValue is SettingsViewModel oldVm)
        {
            oldVm.PropertyChanged -= SettingsVm_PropertyChanged;
        }
        else if (e.OldValue is MainViewModel oldMainVm)
        {
            oldMainVm.PropertyChanged -= MainVm_PropertyChanged;
        }

        if (e.NewValue is SettingsViewModel newVm)
        {
            newVm.PropertyChanged += SettingsVm_PropertyChanged;
        }
        else if (e.NewValue is MainViewModel newMainVm)
        {
            newMainVm.PropertyChanged += MainVm_PropertyChanged;
        }
    }

    private void SettingsVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is SettingsViewModel vm)
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsSecurityPasswordVisible))
            {
                if (!vm.IsSecurityPasswordVisible)
                {
                    if (SecNewPasswordBox != null && SecNewPasswordBox.Password != vm.SecurityNewPassword)
                    {
                        SecNewPasswordBox.Password = vm.SecurityNewPassword ?? "";
                    }
                    if (SecConfirmPasswordBox != null && SecConfirmPasswordBox.Password != vm.SecurityConfirmPassword)
                    {
                        SecConfirmPasswordBox.Password = vm.SecurityConfirmPassword ?? "";
                    }
                }
            }
            else if (e.PropertyName == nameof(SettingsViewModel.SecurityNewPassword))
            {
                if (SecNewPasswordBox != null && SecNewPasswordBox.Password != vm.SecurityNewPassword)
                {
                    SecNewPasswordBox.Password = vm.SecurityNewPassword ?? "";
                }
            }
            else if (e.PropertyName == nameof(SettingsViewModel.SecurityConfirmPassword))
            {
                if (SecConfirmPasswordBox != null && SecConfirmPasswordBox.Password != vm.SecurityConfirmPassword)
                {
                    SecConfirmPasswordBox.Password = vm.SecurityConfirmPassword ?? "";
                }
            }
        }
    }

    private void MainVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
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

    private void SecNewPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            if (DataContext is SettingsViewModel svm)
            {
                svm.SecurityNewPassword = pb.Password;
            }
            else if (DataContext is MainViewModel mvm)
            {
                mvm.SecurityNewPassword = pb.Password;
            }
        }
    }

    private void SecConfirmPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            if (DataContext is SettingsViewModel svm)
            {
                svm.SecurityConfirmPassword = pb.Password;
            }
            else if (DataContext is MainViewModel mvm)
            {
                mvm.SecurityConfirmPassword = pb.Password;
            }
        }
    }
}
