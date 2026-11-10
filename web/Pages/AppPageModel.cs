using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Quodery.Web.Pages;

public abstract class AppPageModel : PageModel
{
    protected BrowserAccess Access => HttpContext.Access();

    [TempData] public string? FlashError { get; set; }
    [TempData] public string? FlashNotice { get; set; }

    public string? Error { get; protected set; }
    public string? ErrorMessage => Error ?? FlashError;

    protected IActionResult Refused(Refusal refusal, string returnUrl) => refusal.Kind switch
    {
        RefusalKind.NotFound => NotFound(),
        RefusalKind.Forbidden => Forbid(),
        _ => RedirectWithError(refusal.Message, returnUrl)
    };

    protected IActionResult RedirectWithError(string message, string returnUrl)
    {
        FlashError = message;
        return LocalRedirect(returnUrl);
    }

    protected void PreventCaching()
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
