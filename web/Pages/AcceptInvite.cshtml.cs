using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Quodery.Web.Pages;

[AllowAnonymous]
public sealed class AcceptInviteModel(AccountService accounts) : NewPasswordPageModel
{
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = string.Empty;

    public string? Username { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        if (string.IsNullOrWhiteSpace(Token)) ModelState.AddModelError(string.Empty, "This invitation link is incomplete.");
        Username = await accounts.GetInvitedUsernameAsync(Token, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        Username = await accounts.GetInvitedUsernameAsync(Token, ct);
        if (!PasswordsMatch()) return Page();
        return await SignInOrShowErrorAsync(await accounts.AcceptInvitationAsync(Token, Password, ct), Url.Page("/Workspaces")!);
    }
}
