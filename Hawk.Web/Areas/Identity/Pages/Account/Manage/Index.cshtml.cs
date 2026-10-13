using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Hawk.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class IndexModel(
    UserManager<IdentityUser> userManager,
    IEmailSender emailSender,
    ILogger<IndexModel> logger) : PageModel
{
    public string Username { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public bool EmailConfirmed { get; private set; }
    public bool HasPassword { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound($"Unable to load user with ID '{userManager.GetUserId(User)}'.");

        Username = user.UserName ?? string.Empty;
        Email = user.Email ?? string.Empty;
        EmailConfirmed = user.EmailConfirmed;
        HasPassword = await userManager.HasPasswordAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostSendConfirmationAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound($"Unable to load user with ID '{userManager.GetUserId(User)}'.");

        if (user.EmailConfirmed || string.IsNullOrEmpty(user.Email))
            return RedirectToPage();

        var code = await userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        var callbackUrl = Url.Page(
            "/Account/ConfirmEmail",
            pageHandler: null,
            values: new { area = "Identity", userId = user.Id, code },
            protocol: Request.Scheme)!;

        try
        {
            await emailSender.SendEmailAsync(
                user.Email,
                "Confirm your email",
                $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");
            TempData["FlashInfo"] = $"Confirmation email sent to {user.Email}.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send confirmation email (userId={UserId})", user.Id);
            TempData["FlashError"] = $"Email failed to send: {ex.Message}";
        }

        return RedirectToPage();
    }
}
