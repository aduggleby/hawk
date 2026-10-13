using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Hawk.Web.Infrastructure;

public static class AuthCookieSetup
{
    public static IServiceCollection AddHawkApplicationCookie(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(1));

        services.ConfigureApplicationCookie(options =>
        {
            var httpsDisabled = configuration.GetValue("Hawk:DisableHttpsRedirection", false);
            options.Cookie.Name = "Hawk.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = httpsDisabled ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.LoginPath = "/Identity/Account/Login";
            options.AccessDeniedPath = "/Identity/Account/AccessDenied";

            options.Events.OnRedirectToLogin = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hawk.Auth");
                logger.LogWarning(
                    "Auth challenge for {Path}. Cookie header present: {HasCookieHeader}. Auth cookie present: {HasAuthCookie}. User authenticated: {IsAuthenticated}.",
                    context.Request.Path,
                    context.Request.Headers.Cookie.Count > 0,
                    context.Request.Cookies.ContainsKey("Hawk.Auth"),
                    context.HttpContext.User.Identity?.IsAuthenticated == true);
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };

            // Identity installs its security-stamp validator when registering the application cookie.
            // Keep that callback so password resets, deleted accounts and role changes take effect.
            var validatePrincipal = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = async context =>
            {
                await validatePrincipal(context);
                if (context.Principal?.Identity?.IsAuthenticated == true)
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hawk.Auth");
                    logger.LogDebug("Validated Hawk auth cookie for {Name}.", context.Principal.Identity.Name);
                }
            };
        });

        return services;
    }
}
