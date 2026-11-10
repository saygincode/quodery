using Quodery.Cli;
using Quodery.Runtime;
using Quodery.Web;

const string usage = """
    Usage: quodery <mode> [options]

    Modes:
      server    The web UI, accounts and run history for your team.
      runtime   The agent connection on a machine with the code: a standalone runtime connects to a
                server elsewhere with --server-url, and a serving runtime (--serve) also serves the
                web UI and accounts itself, for installations on one machine.

    Run `quodery <mode> --help` for a mode's options, or `quodery --version`.
    """;

const string ServerNotes = """
    Without --tls-certificate, run the server behind a TLS-terminating reverse proxy, or on
    --address 127.0.0.1 for a trial. The certificate is read at startup: restart after replacing it.
    On first start, the server prints a one-time link for creating its administrator account.
    """;

const string serverUsage = """
    Usage: quodery server [listening options]

    """ + QuoderyServer.ListeningUsage + "\n\n" + ServerNotes;

var runtimeUsage = $"""
    {RuntimeCommand.Usage}

    With --serve, these listening options apply:
    {QuoderyServer.ListeningUsage}

    {ServerNotes}
    """;

return args switch
{
    ["server", "--help" or "-h"] => Print(serverUsage, 0),
    ["server", .. var rest] => await RunServerAsync(rest),
    ["runtime", "--help" or "-h"] => Print(runtimeUsage, 0),
    ["runtime", .. var rest] when rest.Contains("--serve") => await ServingRuntime.RunAsync(rest),
    ["runtime", .. var rest] => await RuntimeCommand.RunAsync(rest),
    ["--version"] => Print(RuntimeCommand.Version, 0),
    ["--help" or "-h"] => Print(usage, 0),
    _ => Print(usage, 2)
};

static async Task<int> RunServerAsync(string[] args)
{
    if (RuntimeCommand.SplitServingOptions(args).Runtime is [var option, ..])
    {
        Console.Error.WriteLine($"{option} is a runtime option: it belongs to `quodery runtime --serve`, a runtime that also serves");
        return 2;
    }
    return await QuoderyServer.RunAsync(args);
}

static int Print(string text, int exitCode)
{
    (exitCode == 0 ? Console.Out : Console.Error).WriteLine(text);
    return exitCode;
}
