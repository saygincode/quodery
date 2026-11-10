namespace Quodery.Protocol;

public static class RuntimeKeyFormat
{
    public const int KeyLength = 43;

    public static bool IsWellFormed(string? key) =>
        key is { Length: KeyLength } && key.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}
