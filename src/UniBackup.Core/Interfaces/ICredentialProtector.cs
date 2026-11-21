using UniBackup.Core.Models;

namespace UniBackup.Core.Interfaces;

public interface ICredentialProtector
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
    string Seal(string plainText);
    string Unseal(string sealedValue);
    void SealConfig(AppConfig config);
    void UnsealConfig(AppConfig config);
    string ExpectedPassword(DateTime? when = null);
    bool CheckPassword(string? candidate, DateTime? when = null);
}
