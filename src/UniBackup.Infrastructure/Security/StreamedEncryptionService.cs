using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UniBackup.Infrastructure.Security;

public class StreamedEncryptionService
{
    private static readonly byte[] MagicBytes = "UBENC"u8.ToArray();
    private const byte CurrentVersion = 1;
    private const int SaltSizeBytes = 32;
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeyCheckSizeBytes = 16;
    private const int ChunkSizeBytes = 64 * 1024; // 64 KB
    private const int MaxAllowedChunkSizeBytes = 10 * 1024 * 1024; // 10 MB safety cap
    private const int Pbkdf2Iterations = 50_000;
    private static readonly byte[] KeyCheckMessage = "UBKEYCHECK"u8.ToArray();

    public async Task EncryptStreamAsync(Stream source, Stream destination, string passphrase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] key = DeriveKey(passphrase, salt);
        byte[] keyCheck = ComputeKeyCheck(key);
        byte[] baseNonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        // 1. Write Header
        await destination.WriteAsync(MagicBytes, ct);
        destination.WriteByte(CurrentVersion);
        await destination.WriteAsync(salt, ct);
        await destination.WriteAsync(keyCheck, ct);
        await destination.WriteAsync(baseNonce, ct);

        // 2. Stream and Encrypt Chunks
        using var aesGcm = new AesGcm(key, TagSizeBytes);
        byte[] buffer = new byte[ChunkSizeBytes];
        byte[] ciphertext = new byte[ChunkSizeBytes];
        byte[] tag = new byte[TagSizeBytes];
        byte[] lengthBytes = new byte[4];
        ulong chunkIndex = 0;

        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            byte[] chunkNonce = BuildChunkNonce(baseNonce, chunkIndex);
            aesGcm.Encrypt(chunkNonce, buffer.AsSpan(0, bytesRead), ciphertext.AsSpan(0, bytesRead), tag);

            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, bytesRead);
            await destination.WriteAsync(lengthBytes, ct);
            await destination.WriteAsync(tag, ct);
            await destination.WriteAsync(ciphertext.AsMemory(0, bytesRead), ct);

            chunkIndex++;
        }

        // 3. Write EOF terminator chunk (length = 0)
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, 0);
        await destination.WriteAsync(lengthBytes, ct);
        await destination.FlushAsync(ct);
    }

    public async Task DecryptStreamAsync(Stream source, Stream destination, string passphrase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        try
        {
            // 1. Read and Validate Header
            byte[] magic = new byte[MagicBytes.Length];
            await source.ReadExactlyAsync(magic, ct);
            if (!magic.AsSpan().SequenceEqual(MagicBytes))
            {
                throw new CryptographicException("Invalid stream header: unrecognized magic bytes.");
            }

            int version = source.ReadByte();
            if (version != CurrentVersion)
            {
                throw new CryptographicException($"Unsupported encryption version: {version}.");
            }

            byte[] salt = new byte[SaltSizeBytes];
            await source.ReadExactlyAsync(salt, ct);

            byte[] expectedKeyCheck = new byte[KeyCheckSizeBytes];
            await source.ReadExactlyAsync(expectedKeyCheck, ct);

            byte[] key = DeriveKey(passphrase, salt);
            byte[] actualKeyCheck = ComputeKeyCheck(key);

            if (!CryptographicOperations.FixedTimeEquals(expectedKeyCheck, actualKeyCheck))
            {
                throw new CryptographicException("Incorrect passphrase or corrupted encryption header.");
            }

            byte[] baseNonce = new byte[NonceSizeBytes];
            await source.ReadExactlyAsync(baseNonce, ct);

            // 2. Read and Decrypt Chunks
            using var aesGcm = new AesGcm(key, TagSizeBytes);
            byte[] lengthBytes = new byte[4];
            byte[] tag = new byte[TagSizeBytes];
            ulong chunkIndex = 0;
            bool reachedEof = false;

            while (true)
            {
                var read = await source.ReadAsync(lengthBytes, 0, 4, ct);
                if (read == 0)
                {
                    // Unexpected EOF before reaching 0-length terminator
                    break;
                }
                if (read < 4)
                {
                    throw new CryptographicException("Truncated ciphertext: incomplete chunk header.");
                }

                int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (chunkLen == 0)
                {
                    reachedEof = true;
                    break;
                }

                if (chunkLen < 0 || chunkLen > MaxAllowedChunkSizeBytes)
                {
                    throw new CryptographicException($"Invalid chunk size: {chunkLen} bytes.");
                }

                await source.ReadExactlyAsync(tag, ct);

                byte[] ciphertext = new byte[chunkLen];
                await source.ReadExactlyAsync(ciphertext, ct);

                byte[] plaintext = new byte[chunkLen];
                byte[] chunkNonce = BuildChunkNonce(baseNonce, chunkIndex);

                try
                {
                    aesGcm.Decrypt(chunkNonce, ciphertext, tag, plaintext);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new CryptographicException("Authentication tag mismatch. Ciphertext was tampered with or corrupted.", ex);
                }

                await destination.WriteAsync(plaintext, ct);
                chunkIndex++;
            }

            if (!reachedEof)
            {
                throw new CryptographicException("Ciphertext truncated: termination chunk was not reached.");
            }

            await destination.FlushAsync(ct);
        }
        catch (EndOfStreamException ex)
        {
            throw new CryptographicException("Ciphertext truncated unexpectedly.", ex);
        }
    }

    public async Task EncryptFileAsync(string sourcePath, string destinationPath, string passphrase, CancellationToken ct = default)
    {
        await using var source = File.OpenRead(sourcePath);
        await using var dest = File.Create(destinationPath);
        await EncryptStreamAsync(source, dest, passphrase, ct);
    }

    public async Task DecryptFileAsync(string sourcePath, string destinationPath, string passphrase, CancellationToken ct = default)
    {
        await using var source = File.OpenRead(sourcePath);
        await using var dest = File.Create(destinationPath);
        await DecryptStreamAsync(source, dest, passphrase, ct);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
    }

    private static byte[] ComputeKeyCheck(byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        byte[] hash = hmac.ComputeHash(KeyCheckMessage);
        return hash.AsSpan(0, KeyCheckSizeBytes).ToArray();
    }

    private static byte[] BuildChunkNonce(byte[] baseNonce, ulong chunkIndex)
    {
        byte[] nonce = new byte[NonceSizeBytes];
        Array.Copy(baseNonce, nonce, 4);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), chunkIndex);
        return nonce;
    }
}
