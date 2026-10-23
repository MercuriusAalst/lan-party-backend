using System.Security.Cryptography;
using System.Text;

namespace Mercurius.Modules.Identity.Application.Services;

internal static class IdentitySubjectHash
{
    public static string Compute(string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
}
