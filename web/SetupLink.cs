using System.Security.Cryptography;
using System.Text;

namespace Quodery.Web;

public sealed class SetupLink
{
    private volatile string _token = NewToken();

    public string Token => _token;

    public bool Verify(string? token) => token is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_token));

    public void Consume() => _token = NewToken();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
