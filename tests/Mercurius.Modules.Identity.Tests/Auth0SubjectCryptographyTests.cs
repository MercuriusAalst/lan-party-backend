using Mercurius.Modules.Identity.Application.Services.Auth0;
using Mercurius.Modules.Identity.Options;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Identity.Tests;

public sealed class Auth0SubjectCryptographyTests
{
    [Fact]
    public void Encrypt_AuthenticatesJobAndKeepsOnlyStableHashAfterDecryption()
    {
        var crypto = new Auth0SubjectCryptography(Microsoft.Extensions.Options.Options.Create(new Auth0ManagementOptions
        {
            AccountCleanupEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray())
        }));
        const string subject = "google-oauth2|private-subject";
        var jobId = Guid.NewGuid();

        var encrypted = crypto.Encrypt(subject, jobId);

        Assert.DoesNotContain(subject, encrypted, StringComparison.Ordinal);
        Assert.Equal(subject, crypto.Decrypt(encrypted, jobId));
        Assert.Equal("5d62cb5c84b1abc7517695ee1dd304e4b68dd0c73f7b42e2724e9645debea32a", Auth0SubjectCryptography.Hash(subject));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => crypto.Decrypt(encrypted, Guid.NewGuid()));
    }

    [Fact]
    public void Encrypt_FailsClosedWithoutAValidSharedKey()
    {
        var crypto = new Auth0SubjectCryptography(Microsoft.Extensions.Options.Options.Create(new Auth0ManagementOptions
        {
            AccountCleanupEncryptionKey = "invalid"
        }));

        Assert.Throws<InvalidOperationException>(() => crypto.Encrypt("auth0|subject", Guid.NewGuid()));
    }
}
