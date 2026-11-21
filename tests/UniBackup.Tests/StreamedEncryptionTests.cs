using System.Security.Cryptography;
using System.Text;
using UniBackup.Infrastructure.Security;
using Xunit;

namespace UniBackup.Tests;

public class StreamedEncryptionTests
{
    private readonly StreamedEncryptionService _crypto = new();

    [Fact]
    public async Task Encrypt_Then_Decrypt_RoundTrips_ExactBytes()
    {
        var rawData = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog. " + new string('A', 100_000));
        using var sourceStream = new MemoryStream(rawData);
        using var encryptedStream = new MemoryStream();

        const string passphrase = "SuperSecretAdminMasterPassphrase2026!";

        // Encrypt
        await _crypto.EncryptStreamAsync(sourceStream, encryptedStream, passphrase);
        Assert.True(encryptedStream.Length > rawData.Length);

        // Decrypt
        encryptedStream.Position = 0;
        using var decryptedStream = new MemoryStream();
        await _crypto.DecryptStreamAsync(encryptedStream, decryptedStream, passphrase);

        Assert.Equal(rawData, decryptedStream.ToArray());
    }

    [Fact]
    public async Task Decrypt_With_WrongPassphrase_FailsKeyCheck_ThrowsCryptographicException()
    {
        var rawData = Encoding.UTF8.GetBytes("Confidential SQL Server Data");
        using var sourceStream = new MemoryStream(rawData);
        using var encryptedStream = new MemoryStream();

        await _crypto.EncryptStreamAsync(sourceStream, encryptedStream, "CorrectPassphrase123");

        encryptedStream.Position = 0;
        using var decryptedStream = new MemoryStream();

        await Assert.ThrowsAsync<CryptographicException>(async () =>
        {
            await _crypto.DecryptStreamAsync(encryptedStream, decryptedStream, "WrongPassphrase999");
        });
    }

    [Fact]
    public async Task Decrypt_With_TamperedCiphertext_ThrowsCryptographicException()
    {
        var rawData = Encoding.UTF8.GetBytes("Critical Financial Database Dump");
        using var sourceStream = new MemoryStream(rawData);
        using var encryptedStream = new MemoryStream();

        const string passphrase = "CorrectPassphrase123";
        await _crypto.EncryptStreamAsync(sourceStream, encryptedStream, passphrase);

        // Tamper with one byte in the ciphertext payload
        var bytes = encryptedStream.ToArray();
        bytes[^10] ^= 0xFF; // flip bits

        using var tamperedStream = new MemoryStream(bytes);
        using var decryptedStream = new MemoryStream();

        await Assert.ThrowsAsync<CryptographicException>(async () =>
        {
            await _crypto.DecryptStreamAsync(tamperedStream, decryptedStream, passphrase);
        });
    }

    [Fact]
    public async Task Decrypt_With_TruncatedData_ThrowsCryptographicException()
    {
        var rawData = Encoding.UTF8.GetBytes("Another sample payload that is truncated");
        using var sourceStream = new MemoryStream(rawData);
        using var encryptedStream = new MemoryStream();

        await _crypto.EncryptStreamAsync(sourceStream, encryptedStream, "Password");

        var truncatedBytes = encryptedStream.ToArray()[..40]; // only header, missing chunks
        using var truncatedStream = new MemoryStream(truncatedBytes);
        using var decryptedStream = new MemoryStream();

        await Assert.ThrowsAsync<CryptographicException>(async () =>
        {
            await _crypto.DecryptStreamAsync(truncatedStream, decryptedStream, "Password");
        });
    }
}
