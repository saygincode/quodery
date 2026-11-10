using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Quodery.Web.Pages;

public sealed class AccessDeniedModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
