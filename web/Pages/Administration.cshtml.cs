using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Quodery.Web.Pages;

public sealed record RevealedLink(string Username, string Url, DateTimeOffset ExpiresAt);

[Authorize]
public sealed class AdministrationModel(IServiceProvider services, AccountService accounts, TeamService team)
    : AppPageModel
{
    public DateTimeOffset? CertificateExpiresAt { get; } =
        services.GetService<ServerCertificate>() is { ExpiresSoon: true } tls ? tls.ExpiresAt : null;

    public bool IsAdmin => Access.Role == "admin";
    public TeamMembers? Members { get; private set; }
    public RevealedLink? Link { get; private set; }

    public string? ErrorFor { get; private set; }
    public string? TeamError => ErrorFor == "team" ? Error : FlashError;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadMembersAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(string? currentPassword, string? newPassword,
        string? confirmPassword, CancellationToken ct)
    {
        if (newPassword != confirmPassword) return await ShowErrorAsync("password", "The new passwords don't match.", ct);
        if (await accounts.ChangePasswordAsync(Access, currentPassword, newPassword, ct) is { } refusal)
            return await ShowErrorAsync("password", refusal.Message, ct);
        return await SignOutAsync();
    }

    public async Task<IActionResult> OnPostSignOutEverywhereAsync(CancellationToken ct)
    {
        if (await accounts.SignOutEverywhereAsync(Access, ct) is { } refusal)
            return await ShowErrorAsync("sessions", refusal.Message, ct);
        return await SignOutAsync();
    }

    public async Task<IActionResult> OnPostInviteAsync(string? username, CancellationToken ct)
    {
        var invited = await team.InviteAsync(Access, username, ct);
        if (invited.Refusal is { } refusal)
            return refusal.Kind == RefusalKind.Forbidden ? Forbid() : await ShowErrorAsync("team", refusal.Message, ct);
        var (invitation, token) = invited.Value!;
        return await RevealAsync(new RevealedLink(invitation.Username, LinkTo("/AcceptInvite", token),
            invitation.ExpiresAt), ct);
    }

    public async Task<IActionResult> OnPostDeleteMemberAsync(string userId, string? username, CancellationToken ct)
    {
        if (await team.DeleteMemberAsync(Access, userId, ct) is { } refusal) return Refused(refusal, Url.Page("/Administration")!);
        FlashNotice = $"The account for {username ?? "the team member"} was deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelInvitationAsync(string invitationId, string? username, CancellationToken ct)
    {
        if (await team.RevokeInvitationAsync(Access, invitationId, ct) is { } refusal) return Refused(refusal, Url.Page("/Administration")!);
        FlashNotice = $"The invitation for {username ?? "the team member"} was cancelled.";
        return RedirectToPage();
    }

    private async Task LoadMembersAsync(CancellationToken ct)
    {
        if (IsAdmin) Members = (await team.GetMembersAsync(Access, ct)).Value;
    }

    private async Task<IActionResult> ShowErrorAsync(string form, string message, CancellationToken ct)
    {
        ErrorFor = form;
        Error = message;
        await LoadMembersAsync(ct);
        return Page();
    }

    private async Task<IActionResult> RevealAsync(RevealedLink link, CancellationToken ct)
    {
        PreventCaching();
        Link = link;
        await LoadMembersAsync(ct);
        return Page();
    }

    private async Task<IActionResult> SignOutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }

    private string LinkTo(string page, string token) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}{page}?token={Uri.EscapeDataString(token)}";
}
