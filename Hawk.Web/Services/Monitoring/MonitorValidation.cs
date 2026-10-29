// <file>
// <summary>
// Monitor validation shared by bulk import and the agent API: field attributes of MonitorForm and the header / match-rule limits.
// Cross-field rules live in MonitorForm.Validate; the Create/Edit pages use that plus the model binder's attribute checks.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Hawk.Web.Pages.Monitors;

namespace Hawk.Web.Services.Monitoring;

public static class MonitorValidation
{
    /// <summary>Longest header name (matches the column).</summary>
    public const int MaxHeaderNameLength = 200;

    /// <summary>Longest header value (matches the column).</summary>
    public const int MaxHeaderValueLength = 2000;

    /// <summary>Longest match pattern (matches the column).</summary>
    public const int MaxPatternLength = 2000;

    /// <summary>
    /// Field-level attributes ([Required]/[MaxLength]/[Range]/[EmailAddress]) of <paramref name="form"/>.
    /// </summary>
    public static IEnumerable<ValidationResult> Attributes(MonitorForm form)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), results, validateAllProperties: true);
        return results;
    }

    /// <summary>
    /// Header and match-rule lengths and NUL characters, mirroring the entity column limits. Blank header names and blank or
    /// <see cref="ContentMatchMode.None"/> rules are ignored (the pages drop them). Member names are <c>headers[i].name</c>,
    /// <c>headers[i].value</c> and <c>matchRules[i].pattern</c> with the index in the given list.
    /// </summary>
    public static IEnumerable<ValidationResult> ChildLimits(
        IEnumerable<(string? Name, string? Value)> headers, IEnumerable<(ContentMatchMode Mode, string? Pattern)> rules)
    {
        var i = 0;
        foreach (var (rawName, rawValue) in headers)
        {
            var index = i++;
            var name = (rawName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (name.Length > MaxHeaderNameLength)
                yield return new ValidationResult($"Header name '{name[..20]}...' is longer than {MaxHeaderNameLength} characters.", [$"headers[{index}].name"]);
            else if ((rawValue ?? string.Empty).Trim().Length > MaxHeaderValueLength)
                yield return new ValidationResult($"Header '{name}' value is longer than {MaxHeaderValueLength} characters.", [$"headers[{index}].value"]);
            else if (name.Contains('\0') || (rawValue ?? string.Empty).Contains('\0'))
                yield return new ValidationResult($"Header '{name.Replace('\0', ' ')}' contains NUL characters.", [$"headers[{index}].name"]);
        }

        i = 0;
        foreach (var (mode, rawPattern) in rules)
        {
            var index = i++;
            var pattern = (rawPattern ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pattern) || mode == ContentMatchMode.None)
                continue;
            if (pattern.Length > MaxPatternLength)
                yield return new ValidationResult("Match rule pattern is longer than 2000 characters.", [$"matchRules[{index}].pattern"]);
            else if (pattern.Contains('\0'))
                yield return new ValidationResult("Match rule pattern contains NUL characters.", [$"matchRules[{index}].pattern"]);
        }
    }
}
