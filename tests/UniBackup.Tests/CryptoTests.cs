using UniBackup.Infrastructure.Security;
using Xunit;

namespace UniBackup.Tests;

public class CryptoTests
{
    private readonly CredentialProtector _crypto = new();

    [Fact]
    public void Test_RoundTrip_Encryption()
    {
        var secret = "AKIA3RVFDAZDBNU3G53G";
        var encrypted = _crypto.Encrypt(secret);
        var decrypted = _crypto.Decrypt(encrypted);

        Assert.NotEmpty(encrypted);
        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void Test_Seal_And_Unseal()
    {
        var raw = "super_secret_password_123";
        var sealedVal = _crypto.Seal(raw);

        Assert.StartsWith("enc:", sealedVal);

        var unsealed = _crypto.Unseal(sealedVal);
        Assert.Equal(raw, unsealed);

        // Sealing already-sealed value should be idempotent
        var resealed = _crypto.Seal(sealedVal);
        Assert.Equal(sealedVal, resealed);
    }

    [Fact]
    public void Test_Legacy_Python_Ciphertext_Compatibility()
    {
        // Python crypto_util.py has: _PW_PREFIX_ENC = "r/bx3da6fyY8lzDvytGM1A=="
        // which decrypts to "adminups"
        const string legacyEnc = "r/bx3da6fyY8lzDvytGM1A==";
        var decrypted = _crypto.Decrypt(legacyEnc);

        Assert.Equal("adminups", decrypted);
    }

    [Fact]
    public void Test_Daily_Rolling_Password()
    {
        // Matches Python test_login_crypto.py:
        // expected_password(datetime(2026, 6, 15)) == "adminups15062026"
        var date1 = new DateTime(2026, 6, 15);
        Assert.Equal("adminups15062026", _crypto.ExpectedPassword(date1));

        var date2 = new DateTime(2026, 8, 20);
        Assert.Equal("adminups20082026", _crypto.ExpectedPassword(date2));

        var date3 = new DateTime(2026, 1, 5);
        Assert.Equal("adminups05012026", _crypto.ExpectedPassword(date3));
    }

    [Fact]
    public void Test_CheckPassword_Validation()
    {
        var today = _crypto.ExpectedPassword();

        Assert.True(_crypto.CheckPassword(today));
        Assert.True(_crypto.CheckPassword($"  {today}  ")); // Leading/trailing whitespace
        Assert.False(_crypto.CheckPassword("adminups"));     // Prefix alone fails
        Assert.False(_crypto.CheckPassword(""));
        Assert.False(_crypto.CheckPassword(null));
        Assert.False(_crypto.CheckPassword("wrong_password"));
    }
}
