// <file>
// <summary>
// Hangfire dashboard authorization filter. Restricts dashboard access to authenticated Admin users.
// </summary>
// </file>

using Hangfire.Dashboard;

namespace Hawk.Web.Infrastructure;

/// <summary>
/// Authorizes Hangfire dashboard requests.
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    /// <inheritdoc />
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        return http.User.Identity?.IsAuthenticated == true && http.User.IsInRole("Admin");
    }
}

