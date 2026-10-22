using System.Security.Cryptography;
using System.Text;
using Mercurius.Modules.Identity.Options;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal sealed class Auth0SubjectCryptography
{
    private readonly byte[]? _key;

    public Auth0SubjectCryptography(IOptions<Auth0ManagementOptions> options)
    {
        var encodedKey = options.Value.AccountCleanupEncryptionKey;
        if (!string.IsNullOrWhiteSpace(encodedKey))
        {
            try
            {
                var key = Convert.FromBase64String(encodedKey);
                if (key.Length == 32)
                    _key = key;
            }
            catch (FormatException)
            {
            }
        }
    }

    public static string Hash(string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();

    public bool IsConfigured => _key is { Length: 32 };

    public string Encrypt(string subject, Guid jobId)
    {
        var key = _key ?? throw new InvalidOperationException("Auth0 account cleanup encryption key is missing or invalid.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(subject);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, jobId.ToByteArray());
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public string Decrypt(string encryptedSubject, Guid jobId)
    {
        var key = _key ?? throw new InvalidOperationException("Auth0 account cleanup encryption key is missing or invalid.");
        var data = Convert.FromBase64String(encryptedSubject);
        if (data.Length < 28)
            throw new CryptographicException("Stored Auth0 cleanup payload is invalid.");
        var subject = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), subject, jobId.ToByteArray());
        return Encoding.UTF8.GetString(subject);
    }
}
