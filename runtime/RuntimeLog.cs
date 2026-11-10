using System.Text;
using System.Text.Json;

namespace Quodery.Runtime;

internal sealed class RuntimeLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly object sync = new();
    private readonly StreamWriter writer;
    private bool failed;
    private bool disposed;

    private RuntimeLog(DateTimeOffset startedAt, string runtimeDirectory, string sessionDirectory,
        string filePath, StreamWriter writer)
    {
        StartedAt = startedAt;
        RuntimeDirectory = runtimeDirectory;
        SessionDirectory = sessionDirectory;
        FilePath = filePath;
        this.writer = writer;
    }

    public DateTimeOffset StartedAt { get; }
    public string RuntimeDirectory { get; }
    public string SessionDirectory { get; }
    public string FilePath { get; }

    public static RuntimeLog Create()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var timestamp = startedAt.ToString("yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new IOException("Cannot determine the user home directory for runtime data");
        var runtimeDirectory = Path.Combine(home, ".quodery", "runtime");
        var sessionDirectory = Path.Combine(runtimeDirectory, timestamp);
        Directory.CreateDirectory(sessionDirectory);
        if (!OperatingSystem.IsWindows())
        {
            var privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(Path.Combine(home, ".quodery"), privateDirectoryMode);
            File.SetUnixFileMode(runtimeDirectory, privateDirectoryMode);
            File.SetUnixFileMode(sessionDirectory, privateDirectoryMode);
        }
        var filePath = Path.Combine(sessionDirectory, "session.log");
        var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        return new RuntimeLog(startedAt, runtimeDirectory, sessionDirectory, filePath, writer);
    }

    public void Information(string eventName, object? data = null) => Write("information", eventName, data);
    public void Warning(string eventName, object? data = null) => Write("warning", eventName, data);
    public void Error(string eventName, object? data = null) => Write("error", eventName, data);

    private void Write(string level, string eventName, object? data)
    {
        lock (sync)
        {
            if (disposed || failed) return;
            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    level,
                    @event = eventName,
                    data
                }, JsonOptions);
                writer.WriteLine(line);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                failed = true;
                Console.Error.WriteLine($"runtime log write failed: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            writer.Dispose();
        }
    }
}
