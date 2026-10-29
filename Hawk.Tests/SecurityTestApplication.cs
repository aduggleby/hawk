using System.Net;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hawk.Web.Data;
using Hawk.Web.Infrastructure;
using Hawk.Web.Pages;
using Hawk.Web.Services.Email;
using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hawk.Tests;

internal sealed class SecurityTestApplication(WebApplication app, HttpClient client) : IAsyncDisposable
{
    internal const string UserEmail = "owner@example.com";
    internal const string Password = "test-password-123";
    internal const string PrivateException = "private-exception-secret";
    internal WebApplication App { get; } = app;
    internal HttpClient Client { get; } = client;
    internal CookieTestClock Clock => App.Services.GetRequiredService<CookieTestClock>();
    internal TestJobs Jobs => App.Services.GetRequiredService<TestJobs>();
    internal TestEmailSender Emails => App.Services.GetRequiredService<TestEmailSender>();
    internal TestExecutor Executor => App.Services.GetRequiredService<TestExecutor>();

    internal static async Task<SecurityTestApplication> CreateAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hawk.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Hawk solution root not found.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(ErrorModel).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(root, "Hawk.Web")
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hawk:DisableHttpsRedirection"] = "true",
            ["Hawk:Security:LoginRequestsPerMinute"] = "100",
            ["Hawk:Security:EmailRequestsPer15Minutes"] = "100"
        });
        var databaseName = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddHawkIdentity(builder.Configuration);
        builder.Services.AddScoped<Hawk.Web.Services.ApiKeys.ApiKeyService>();
        builder.Services.AddHawkApplicationCookie(builder.Configuration);
        builder.Services.AddHawkAuthAbuseProtection(builder.Configuration);
        builder.Services.AddSingleton<CookieTestClock>();
        builder.Services.AddSingleton<TimeProvider>(services => services.GetRequiredService<CookieTestClock>());
        builder.Services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
        builder.Services.AddOptions<SecurityStampValidatorOptions>()
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
        builder.Services.AddSingleton<TestJobs>();
        builder.Services.AddSingleton<IBackgroundJobClient>(services => services.GetRequiredService<TestJobs>());
        builder.Services.AddSingleton<TestEmailSender>();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>(services => services.GetRequiredService<TestEmailSender>());
        builder.Services.AddSingleton<TestExecutor>();
        builder.Services.AddSingleton<IMonitorExecutor>(services => services.GetRequiredService<TestExecutor>());
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/");
            options.Conventions.AllowAnonymousToPage("/Index");
            options.Conventions.AllowAnonymousToPage("/Error");
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin")));

        var application = builder.Build();
        application.UseExceptionHandler("/Error");
        application.UseHawkAuthAbuseProtection();
        application.UseRouting();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapRazorPages();
        application.MapGet("/test/secure", () => Results.Ok("authenticated")).RequireAuthorization();
        application.MapGet("/test/admin", () => Results.Ok("admin")).RequireAuthorization("AdminOnly");
        application.MapGet("/test/error", () => ThrowPrivateException());
        await application.StartAsync();

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            Assert.True((await roles.CreateAsync(new IdentityRole("Admin"))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = new IdentityUser { UserName = UserEmail, Email = UserEmail, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        }

        var cookieHandler = new CookieContainerHandler(application.GetTestServer().CreateHandler());
        var httpClient = new HttpClient(cookieHandler) { BaseAddress = new Uri("http://localhost") };
        return new SecurityTestApplication(application, httpClient);
    }

    internal async Task LoginAsync(string password = Password)
    {
        using var page = await Client.GetAsync("/Identity/Account/Login");
        page.EnsureSuccessStatusCode();
        var token = AntiforgeryToken(await page.Content.ReadAsStringAsync());
        using var response = await Client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = UserEmail,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    internal async Task WithUserAsync(Func<UserManager<IdentityUser>, IdentityUser, Task> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByEmailAsync(UserEmail) ?? throw new InvalidOperationException("Test owner missing.");
        await action(users, user);
    }

    internal static string AntiforgeryToken(string html)
    {
        var input = System.Text.RegularExpressions.Regex.Match(html,
            "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(input.Success, "Rendered form has no antiforgery token.");
        return WebUtility.HtmlDecode(input.Groups[1].Value);
    }

    private static IResult ThrowPrivateException() => throw new InvalidOperationException(PrivateException);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    internal sealed class CookieTestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    internal sealed class TestJobs : IBackgroundJobClient
    {
        internal List<Job> Created { get; } = [];
        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Created.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    internal sealed class TestEmailSender : Microsoft.AspNetCore.Identity.UI.Services.IEmailSender
    {
        internal int Sent { get; private set; }
        internal bool Fail { get; set; }
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            Sent++;
            return Fail ? Task.FromException(new InvalidOperationException(PrivateException)) : Task.CompletedTask;
        }
    }

    internal sealed class TestExecutor : IMonitorExecutor
    {
        internal int Executions { get; private set; }
        public Task<MonitorExecutionResult?> ExecuteAsync(Guid monitorId, string? reason, CancellationToken cancellationToken, Guid? runId = null)
        {
            Executions++;
            throw new InvalidOperationException("Test execution must not run on navigation.");
        }

        public Task<PingResult> RecordPingAsync(string token, PingRequestInfo info, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Test ping must not run.");
    }

    private sealed class CookieContainerHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var cookie = _cookies.GetCookieHeader(uri);
            if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) _cookies.SetCookies(uri, value);
            return response;
        }
    }
}
