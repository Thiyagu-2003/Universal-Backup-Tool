using System.Security.Cryptography;
using System.Text;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Security;

public class CredentialProtector : ICredentialProtector
{
    public const string EncPrefix = "enc:";
    private static readonly byte[] LegacyKey = MD5.HashData(Encoding.UTF8.GetBytes("ERP")); // 16 bytes

    // 16-byte 3DES key needs to be 24 bytes in .NET (Key1 + Key2 + Key1)
    private static byte[] GetTripleDesKey()
    {
        var key24 = new byte[24];
        Buffer.BlockCopy(LegacyKey, 0, key24, 0, 16);
        Buffer.BlockCopy(LegacyKey, 0, key24, 16, 8); // duplicate first 8 bytes
        return key24;
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";

        using var des = TripleDES.Create();
        des.Key = GetTripleDesKey();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;

        using var encryptor = des.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(cipherBytes);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";

        try
        {
            using var des = TripleDES.Create();
            des.Key = GetTripleDesKey();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.PKCS7;

            using var decryptor = des.CreateDecryptor();
            var cipherBytes = Convert.FromBase64String(cipherText);
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return "";
        }
    }

    public string Seal(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || plainText.StartsWith(EncPrefix))
            return plainText ?? "";

        return EncPrefix + Encrypt(plainText);
    }

    public string Unseal(string sealedValue)
    {
        if (string.IsNullOrEmpty(sealedValue) || !sealedValue.StartsWith(EncPrefix))
            return sealedValue ?? "";

        var ciphertext = sealedValue[EncPrefix.Length..];
        return Decrypt(ciphertext);
    }

    public void SealConfig(AppConfig config)
    {
        if (!string.IsNullOrEmpty(config.Aws.AccessKeyId))
            config.Aws.AccessKeyId = Seal(config.Aws.AccessKeyId);

        if (!string.IsNullOrEmpty(config.Aws.SecretAccessKey))
            config.Aws.SecretAccessKey = Seal(config.Aws.SecretAccessKey);

        if (!string.IsNullOrEmpty(config.Email.Password))
            config.Email.Password = Seal(config.Email.Password);

        if (!string.IsNullOrEmpty(config.Security?.AccessPassword))
            config.Security.AccessPassword = Seal(config.Security.AccessPassword);

        if (!string.IsNullOrEmpty(config.Azure?.ConnectionString))
            config.Azure.ConnectionString = Seal(config.Azure.ConnectionString);

        if (!string.IsNullOrEmpty(config.Sftp?.Password))
            config.Sftp.Password = Seal(config.Sftp.Password);

        if (!string.IsNullOrEmpty(config.Ftp?.Password))
            config.Ftp.Password = Seal(config.Ftp.Password);

        if (!string.IsNullOrEmpty(config.WebDav?.Password))
            config.WebDav.Password = Seal(config.WebDav.Password);

        if (!string.IsNullOrEmpty(config.Dropbox?.AccessToken))
            config.Dropbox.AccessToken = Seal(config.Dropbox.AccessToken);

        if (!string.IsNullOrEmpty(config.OneDrive?.AccessToken))
            config.OneDrive.AccessToken = Seal(config.OneDrive.AccessToken);

        if (!string.IsNullOrEmpty(config.Box?.AccessToken))
            config.Box.AccessToken = Seal(config.Box.AccessToken);
    }

    public void UnsealConfig(AppConfig config)
    {
        if (!string.IsNullOrEmpty(config.Aws.AccessKeyId))
            config.Aws.AccessKeyId = Unseal(config.Aws.AccessKeyId);

        if (!string.IsNullOrEmpty(config.Aws.SecretAccessKey))
            config.Aws.SecretAccessKey = Unseal(config.Aws.SecretAccessKey);

        if (!string.IsNullOrEmpty(config.Email.Password))
            config.Email.Password = Unseal(config.Email.Password);

        if (!string.IsNullOrEmpty(config.Security?.AccessPassword))
            config.Security.AccessPassword = Unseal(config.Security.AccessPassword);

        if (!string.IsNullOrEmpty(config.Azure?.ConnectionString))
            config.Azure.ConnectionString = Unseal(config.Azure.ConnectionString);

        if (!string.IsNullOrEmpty(config.Sftp?.Password))
            config.Sftp.Password = Unseal(config.Sftp.Password);

        if (!string.IsNullOrEmpty(config.Ftp?.Password))
            config.Ftp.Password = Unseal(config.Ftp.Password);

        if (!string.IsNullOrEmpty(config.WebDav?.Password))
            config.WebDav.Password = Unseal(config.WebDav.Password);

        if (!string.IsNullOrEmpty(config.Dropbox?.AccessToken))
            config.Dropbox.AccessToken = Unseal(config.Dropbox.AccessToken);

        if (!string.IsNullOrEmpty(config.OneDrive?.AccessToken))
            config.OneDrive.AccessToken = Unseal(config.OneDrive.AccessToken);

        if (!string.IsNullOrEmpty(config.Box?.AccessToken))
            config.Box.AccessToken = Unseal(config.Box.AccessToken);
    }

    public string ExpectedPassword(DateTime? when = null)
    {
        var targetDate = when ?? DateTime.Now;
        // adminups + ddMMyyyy (zero padded)
        return $"adminups{targetDate:ddMMyyyy}";
    }

    public bool CheckPassword(string? candidate, DateTime? when = null)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var clean = candidate.Trim();
        var expected = ExpectedPassword(when);
        return string.Equals(clean, expected, StringComparison.Ordinal);
    }
}
