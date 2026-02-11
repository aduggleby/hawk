// <file>
// <summary>
// Helper for mapping custom monitor form validation results to Razor-friendly ModelState keys.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Hawk.Web.Pages.Monitors;

public static class MonitorFormValidation
{
    public static void AddResults(ModelStateDictionary modelState, IEnumerable<ValidationResult> results, string prefix = "Form")
    {
        foreach (var result in results)
        {
            var error = result.ErrorMessage ?? "Invalid value.";
            var members = result.MemberNames?.Where(m => !string.IsNullOrWhiteSpace(m)).ToArray() ?? [];

            if (members.Length == 0)
            {
                modelState.AddModelError(prefix, error);
                continue;
            }

            foreach (var member in members)
                modelState.AddModelError($"{prefix}.{member}", error);
        }
    }
}
