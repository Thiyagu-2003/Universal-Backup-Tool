using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Security;

public static class WindowsImpersonator
{
    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out SafeAccessTokenHandle phToken);

    public static async Task<T> RunAsAsync<T>(
        WindowsRunAsCredentials? credentials,
        ICredentialProtector crypto,
        Func<Task<T>> action)
    {
        if (credentials == null || !credentials.Enabled || string.IsNullOrWhiteSpace(credentials.Username))
        {
            return await action();
        }

        var domain = string.IsNullOrWhiteSpace(credentials.Domain) ? "." : credentials.Domain;
        var password = string.IsNullOrWhiteSpace(credentials.PasswordSealed)
            ? ""
            : crypto.Unseal(credentials.PasswordSealed);

        SafeAccessTokenHandle tokenHandle;
        bool ok = LogonUser(
            credentials.Username,
            domain,
            password,
            LOGON32_LOGON_NEW_CREDENTIALS,
            LOGON32_PROVIDER_DEFAULT,
            out tokenHandle);

        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warning("[Impersonation] LogonUser failed with Win32 error {Code}. Proceeding with process token.", err);
            return await action();
        }

        using (tokenHandle)
        {
            return await WindowsIdentity.RunImpersonated(tokenHandle, async () => await action());
        }
    }
}
