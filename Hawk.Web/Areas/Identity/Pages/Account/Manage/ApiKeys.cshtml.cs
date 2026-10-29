using System.ComponentModel.DataAnnotations;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Services.ApiKeys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class ApiKeysModel(ApiKeyService keys, UserManager<IdentityUser> userManager) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(100)]
    [Display(Name = "Name")]
    public string? Name { get; set; }

    [BindProperty]
    [EnumDataType(typeof(ApiKeyScope))]
    public ApiKeyScope Scope { get; set; } = ApiKeyScope.Read;

    public IReadOnlyList<ApiKey> Keys { get; private set; } = [];

    /// <summary>The plaintext of the key created by this request. Never stored or put in TempData.</summary>
    public string? NewKey { get; private set; }

    public string? NewKeyName { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        Keys = await keys.ListAsync(userId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        // PostgreSQL text columns reject NUL characters (SQL Server accepts them); refuse them on every provider.
        if (Name is not null && Name.Contains('\0'))
            ModelState.AddModelError(nameof(Name), "Fields must not contain NUL characters.");

        if (ModelState.IsValid)
        {
            var created = await keys.CreateAsync(userId, Name!.Trim(), Scope, cancellationToken);
            NewKey = created.Plaintext;
            NewKeyName = created.Key.Name;
            Response.Headers.CacheControl = "no-store";

            // Do not refill the form after a successful create.
            ModelState.Clear();
            Name = null;
            Scope = ApiKeyScope.Read;
        }

        Keys = await keys.ListAsync(userId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid keyId, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        if (await keys.RevokeAsync(userId, keyId, cancellationToken))
            TempData["FlashInfo"] = "API key revoked.";
        else
            TempData["FlashError"] = "API key not found.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAllAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        await keys.RevokeAllAsync(userId, cancellationToken);
        TempData["FlashInfo"] = "All API keys revoked.";
        return RedirectToPage();
    }
}
