using System.Security.Cryptography;
using System.Text;

namespace Mercurius.Modules.Identity.Application.Services.Auth0;

internal static class Auth0SubjectHash
{
    public static string Compute(string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
}
