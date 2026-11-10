namespace Quodery.Runtime;

internal static class RuntimeVersion
{
    public static string Current => typeof(RuntimeVersion).Assembly.GetName().Version!.ToString(3);
}
