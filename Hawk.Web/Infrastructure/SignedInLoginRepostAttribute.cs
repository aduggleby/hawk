// <file>
// <summary>
// Turns a repeated login POST from an already signed-in browser into a redirect instead of a 400.
// </summary>
// </file>

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Hawk.Web.Infrastructure;

/// <summary>
/// The first login POST signs the user in; a second POST of the same form (double Enter, password manager
/// auto-submit while the next page loads) carries an antiforgery token issued to the anonymous user, so the
/// antiforgery filter rejects it with a bare 400 and the browser abandons the page it was loading.
/// This filter runs before antiforgery validation (lower order) and, only for a signed-in user whose token
/// no longer validates, redirects to the local return URL. A valid token (fresh form while signed in) is untouched.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SignedInLoginRepostAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    /// <summary>Before the antiforgery filter (order 1000).</summary>
    public int Order => 0;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsPost(http.Request.Method) || http.User.Identity?.IsAuthenticated != true)
            return;

        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        if (await antiforgery.IsRequestValidAsync(http))
            return;

        var returnUrl = http.Request.Query["returnUrl"].ToString();
        var urlHelper = context.HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory>()
            .GetUrlHelper(context);
        context.Result = new LocalRedirectResult(urlHelper.IsLocalUrl(returnUrl) ? returnUrl : "~/");
    }
}
