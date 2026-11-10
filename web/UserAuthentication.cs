using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Quodery.Web;

public static class UserAuthentication
{
    public const int MaximumUsernameLength = 64;
    public const string UsernameProblem = "Enter a username of up to 64 characters, without spaces.";

    public static bool TryNormaliseUsername(string? value, out string username)
    {
        username = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return username.Length is > 0 and <= MaximumUsernameLength &&
            !username.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
    }

    public static string CreateLinkToken(string id) =>
        $"{id}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    public static bool TryHashLinkToken(string? token, string idPrefix, out string id, out byte[] tokenHash)
    {
        id = string.Empty;
        tokenHash = [];
        if (token is null || token.Length > 160) return false;
        var separator = token.IndexOf('.');
        if (separator < 1 || separator != token.LastIndexOf('.') ||
            token[(separator + 1)..].Length != 64) return false;
        id = token[..separator];
        if (!id.StartsWith(idPrefix, StringComparison.Ordinal) ||
            !token[(separator + 1)..].All(Uri.IsHexDigit)) return false;
        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return true;
    }

    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    public static string? PasswordProblem(string? password) => password switch
    {
        null or { Length: < MinimumPasswordLength } => $"Use at least {MinimumPasswordLength} characters.",
        { Length: > MaximumPasswordLength } => $"Use at most {MaximumPasswordLength} characters.",
        _ => null
    };

    private static readonly PasswordHasher<string> Hasher = new();

    public static string HashPassword(string password) => Hasher.HashPassword(string.Empty, password);

    public static bool VerifyPassword(string? hash, string? password) =>
        hash is not null && password is { Length: > 0 and <= MaximumPasswordLength } &&
        Hasher.VerifyHashedPassword(string.Empty, hash, password) != PasswordVerificationResult.Failed;
}
