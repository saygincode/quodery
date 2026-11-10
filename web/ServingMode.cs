namespace Quodery.Web;

public sealed class ServingMode(bool servesRuntime)
{
    public bool ServesRuntime { get; } = servesRuntime;

    public Func<CancellationToken, Task>? SetupCompleted { get; set; }
}
