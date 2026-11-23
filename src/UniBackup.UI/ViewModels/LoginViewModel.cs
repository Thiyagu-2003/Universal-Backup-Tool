using System.Windows;
using UniBackup.Engine.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniBackup.UI.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly ConfigurationService _configService;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _engineStatus = "Engine Active";

    public bool IsAuthenticated { get; private set; }
    public Action? CloseAction { get; set; }
    public event EventHandler? SignedIn;

    public LoginViewModel(ConfigurationService configService)
    {
        _configService = configService;
    }

    public static bool CheckSecurity(string input, UniBackup.Core.Models.SecuritySettings? security)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();
        if (security == null || !security.RequireLogin) return true;

        // 1. Dynamic date-based password check
        if (string.Equals(security.AuthMode, "DateBased", StringComparison.OrdinalIgnoreCase))
        {
            var expectedLocal = security.GenerateExpectedPassword(DateTime.Now);
            var expectedUtc = security.GenerateExpectedPassword(DateTime.UtcNow);
            var expectedYesterday = security.GenerateExpectedPassword(DateTime.Today.AddDays(-1));
            var expectedTomorrow = security.GenerateExpectedPassword(DateTime.Today.AddDays(1));

            return string.Equals(trimmed, expectedLocal, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, expectedUtc, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, expectedYesterday, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, expectedTomorrow, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Custom static password check
        if (!string.IsNullOrEmpty(security.AccessPassword))
        {
            return string.Equals(trimmed, security.AccessPassword, StringComparison.Ordinal);
        }

        return false;
    }

    public static bool CheckPassword(string input, string? configuredPassword)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();

        // 1. Literal format match
        if (string.Equals(trimmed, "adminupsddmmyyyy", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Dynamic daily date format: adminups + ddMMyyyy (e.g., adminups17112026 for 17-11-2026)
        var today = DateTime.Now.ToString("ddMMyyyy");
        var utcToday = DateTime.UtcNow.ToString("ddMMyyyy");
        var yesterday = DateTime.Today.AddDays(-1).ToString("ddMMyyyy");
        var tomorrow = DateTime.Today.AddDays(1).ToString("ddMMyyyy");

        if (string.Equals(trimmed, $"adminups{today}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, $"adminups{utcToday}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, $"adminups{yesterday}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, $"adminups{tomorrow}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Custom configured password if set
        if (!string.IsNullOrWhiteSpace(configuredPassword) &&
            !string.Equals(configuredPassword, "admin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, configuredPassword, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = "";

        try
        {
            var config = await _configService.LoadConfigAsync();
            if (CheckSecurity(Password, config.Security))
            {
                IsAuthenticated = true;
                SignedIn?.Invoke(this, EventArgs.Empty);
                CloseAction?.Invoke();
                return;
            }

            ErrorMessage = "Incorrect access password. Please try again.";
            Password = "";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Authentication error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
