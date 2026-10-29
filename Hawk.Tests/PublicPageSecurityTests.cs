using Hawk.Web.Areas.Identity.Pages.Account;
using Hawk.Web.Pages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Hawk.Tests;

public class PublicPageSecurityTests
{
    [Fact]
    public void Production_error_keeps_exception_out_of_page_model_and_logs_it()
    {
        var exception = new InvalidOperationException("private database detail");
        var logger = new RecordingLogger<ErrorModel>();
        var model = CreateErrorModel("Production", exception, logger);

        model.OnGet();

        Assert.False(string.IsNullOrEmpty(model.RequestId));
        Assert.True(model.ShowRequestId);
        Assert.True(model.IsProduction);
        Assert.Null(model.Path);
        Assert.Null(model.ExceptionType);
        Assert.Null(model.ErrorMessage);
        Assert.Null(model.ExceptionDetails);
        Assert.False(model.HasDiagnostics);
        Assert.Same(exception, logger.Exception);
        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Contains(model.RequestId!, logger.Message);
    }

    [Fact]
    public void Development_error_retains_diagnostics()
    {
        var exception = new InvalidOperationException("private database detail");
        var model = CreateErrorModel("Development", exception, new RecordingLogger<ErrorModel>());

        model.OnPost();

        Assert.False(model.IsProduction);
        Assert.Equal("/private-path", model.Path);
        Assert.Equal(typeof(InvalidOperationException).FullName, model.ExceptionType);
        Assert.Equal(exception.Message, model.ErrorMessage);
        Assert.Contains(exception.Message, model.ExceptionDetails);
    }

    [Fact]
    public void Malformed_reset_token_returns_validation_page()
    {
        var model = new ResetPasswordModel(null!, null!);

        var result = model.OnGet("invalid***token");

        Assert.IsType<PageResult>(result);
        Assert.Contains(model.ModelState[string.Empty]!.Errors,
            error => error.ErrorMessage == "The password reset link is invalid.");
    }

    [Fact]
    public void Valid_reset_token_still_decodes()
    {
        var model = new ResetPasswordModel(null!, null!);
        var token = "valid+token/value=";
        var code = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));

        var result = model.OnGet(code);

        Assert.IsType<PageResult>(result);
        Assert.Equal(token, model.Input.Code);
        Assert.True(model.ModelState.IsValid);
    }

    [Fact]
    public async Task Malformed_confirmation_token_returns_validation_page_before_user_lookup()
    {
        var model = new ConfirmEmailModel(null!);

        var result = await model.OnGetAsync("some-user", "invalid***token");

        Assert.IsType<PageResult>(result);
        Assert.Equal("Error confirming your email.", model.StatusMessage);
    }

    [Fact]
    public void Old_test_route_redirects_without_executor_dependency()
    {
        var id = Guid.NewGuid();
        var model = new Hawk.Web.Pages.Monitors.TestModel();

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet(id));

        Assert.Equal("/Monitors/Details", result.PageName);
        Assert.Equal(id, result.RouteValues!["id"]);
    }

    private static ErrorModel CreateErrorModel(
        string environmentName,
        Exception exception,
        RecordingLogger<ErrorModel> logger)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "request-123";
        context.Request.Path = "/private-path";
        context.Items["Hawk.Exception"] = exception;

        return new ErrorModel(new TestEnvironment(environmentName), logger)
        {
            PageContext = new PageContext { HttpContext = context }
        };
    }

    private sealed class TestEnvironment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = ".";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public Exception? Exception { get; private set; }
        public LogLevel? Level { get; private set; }
        public string Message { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
            Level = logLevel;
            Message = formatter(state, exception);
        }
    }
}
