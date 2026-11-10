using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quodery.Protocol;

namespace Quodery.Web.Pages;

[Authorize(Roles = "admin")]
public sealed class SetupModel(WorkspaceContext workspace, ProjectService projects,
    ServingMode serving) : AppPageModel
{
    public static readonly IReadOnlyList<(string Name, string Label)> Steps =
    [
        ("welcome", "Welcome"), ("project", "Create workspace"), ("install", "Install"),
        ("connect", "Connect"), ("waiting", "Verify"), ("complete", "Ready")
    ];
    private static readonly HashSet<string> AgentProviders = new(StringComparer.Ordinal) { "codex", "claude" };

    public string RuntimeDownloadUrl { get; } = "https://github.com/saygincode/quodery/releases/latest/download/quodery";
    public string RuntimeServerUrl { get; private set; } = string.Empty;

    [BindProperty(SupportsGet = true, Name = "project_id")] public string? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Step { get; set; }
    [BindProperty] public string WorkspaceRoot { get; set; } = "/path/to/workspace";
    [BindProperty] public string AgentProvider { get; set; } = "codex";
    [BindProperty] public string AgentBinary { get; set; } = "codex";

    public string? CurrentStep { get; private set; }
    public ProjectWithRuntime? Project { get; private set; }
    public ProjectConnection? Connection { get; private set; }
    public bool ResumeKey { get; private set; }
    public bool ShowExit { get; private set; }
    public string? Command { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } redirect) return redirect;
        return Page();
    }

    public async Task<IActionResult> OnPostProjectAsync(string? name, CancellationToken ct)
    {
        var created = await projects.CreateAsync(Access, name, ct);
        if (created.Refusal is { } refusal)
        {
            if (refusal.Kind == RefusalKind.Forbidden) return Forbid();
            ProjectId = null;
            Step = "project";
            Error = refusal.Message;
            return await LoadAsync(ct) ?? Page();
        }
        return RedirectToPage(new { project_id = created.Value!.Id, step = "install" });
    }

    public async Task<IActionResult> OnPostCommandAsync(CancellationToken ct)
    {
        Step = "connect";
        if (await LoadAsync(ct) is { } redirect) return redirect;
        if (Project is null) return Page();
        WorkspaceRoot = WorkspaceRoot.Trim();
        AgentBinary = AgentBinary.Trim();
        if (WorkspaceRoot.Length == 0 || !AgentProviders.Contains(AgentProvider) || AgentBinary.Length == 0)
        {
            Error = "Enter the workspace path and choose a coding agent.";
            return Page();
        }
        var created = await projects.CreateRuntimeKeyAsync(Access, Project.Id, ct);
        if (created.Refusal is { } refusal)
        {
            Error = refusal.Message;
            return Page();
        }
        PreventCaching();
        ResumeKey = false;
        string[] options =
        [
            $"--server-url {ShellQuote(RuntimeServerUrl)}",
            $"--workspace-root {ShellQuote(WorkspaceRoot)}",
            $"--agent-provider {ShellQuote(AgentProvider)}",
            $"--agent-binary {ShellQuote(AgentBinary)}"
        ];
        string[] lines =
        [
            $"export QUODERY_RUNTIME_KEY={ShellQuote(created.Value!.Token)}",
            "quodery runtime \\",
            .. options.Select((option, index) => $"  {option}{(index < options.Length - 1 ? " \\" : "")}")
        ];
        Command = string.Join("\n", lines);
        return Page();
    }

    public async Task<IActionResult> OnGetConnectionAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Step = "complete";
        if (await LoadAsync(ct) is not null || Project is null) return NotFound();
        return Partial("_SetupConnection", this);
    }

    public string StepUrl(string step) => Url.Page("/Setup", new { project_id = Project?.Id, step })!;

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        if (serving.ServesRuntime) return RedirectToPage("/Index");
        RuntimeServerUrl = $"{(Request.IsHttps ? "https" : "http")}://{Request.Host}/runtime";
        await workspace.SelectAsync(ProjectId, ct);
        var all = workspace.Projects;
        Project = workspace.Current;
        if (ProjectId is null)
        {
            CurrentStep = Step is "project" ? "project" : "welcome";
            ShowExit = all.Count > 0;
            return null;
        }
        if (Project is null)
        {
            if (all.Count == 0) return RedirectToPage();
            ShowExit = true;
            Error = "This workspace is not available. Return to Workspaces and choose another workspace.";
            return null;
        }
        Connection = (await projects.GetConnectionAsync(Project.Id, ct)).Value;
        var activated = Connection?.IsActivated == true;
        if (activated && Step != "complete") return Redirect($"/?project_id={Uri.EscapeDataString(Project.Id)}");
        ResumeKey = !activated && await projects.HasPendingRuntimeKeyAsync(Access, Project.Id, ct);
        CurrentStep = Step switch
        {
            "complete" when !activated => "waiting",
            not null when Steps.Any(step => step.Name == Step) => Step,
            _ => ResumeKey ? "connect" : "install"
        };
        ShowExit = Project.Runtime.IsConfigured || activated || all.Any(project => project.Id != Project.Id);
        return null;
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'")}'";
}
