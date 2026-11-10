using System.Security.Cryptography;
using System.Text;

namespace Quodery.Web.Runtime;

public static class RuntimeKeys
{
    public static (string Plaintext, byte[] Hash) Generate()
    {
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (plaintext, Hash(plaintext));
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static bool Matches(byte[]? keyHash, string key) =>
        keyHash is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(keyHash, Hash(key));
}
