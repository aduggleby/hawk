// <file>
// <summary>
// Email service registration shared by Program.cs and tests.
// </summary>
// </file>

namespace Hawk.Web.Services.Email;

public static class EmailSetup
{
    /// <summary>
    /// Registers <see cref="IEmailSender"/> exactly once, as a typed HttpClient with a short timeout (a heartbeat
    /// recovery email is sent inside the ping request, whose clients typically time out at 10s).
    /// </summary>
    public static IServiceCollection AddHawkEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResendCompatibleEmailOptions>(configuration.GetSection("Hawk:Resend"));
        services.AddHttpClient<IEmailSender, ResendCompatibleEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, IdentityUiEmailSender>();
        return services;
    }
}
