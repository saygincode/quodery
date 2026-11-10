using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Quodery.Web.Storage;

namespace Quodery.Web.Pages;

public abstract class NewPasswordPageModel : PageModel
{
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    protected bool PasswordsMatch()
    {
        if (Password == ConfirmPassword) return true;
        ClearPasswords();
        ModelState.AddModelError(string.Empty, "The passwords don't match.");
        return false;
    }

    protected async Task<IActionResult> SignInOrShowErrorAsync(Result<ApplicationUser> result, string redirectTo)
    {
        ClearPasswords();
        if (result.Refusal is { } refusal)
        {
            ModelState.AddModelError(string.Empty, refusal.Message);
            return Page();
        }
        await AuthenticationSession.SignInAsync(HttpContext, result.Value!);
        return LocalRedirect(redirectTo);
    }

    private void ClearPasswords()
    {
        Password = ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(Password));
        ModelState.Remove(nameof(ConfirmPassword));
    }
}
