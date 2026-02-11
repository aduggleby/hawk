using System.ComponentModel.DataAnnotations;
using Hawk.Web.Pages.Monitors;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Hawk.Tests;

public class MonitorFormValidationTests
{
    [Fact]
    public void AddResults_PrefixesFieldMemberNames_WithForm()
    {
        var modelState = new ModelStateDictionary();
        var results = new[]
        {
            new ValidationResult("Bad status codes", [nameof(MonitorForm.AllowedStatusCodes)])
        };

        MonitorFormValidation.AddResults(modelState, results);

        Assert.True(modelState.ContainsKey("Form.AllowedStatusCodes"));
        Assert.Contains(modelState["Form.AllowedStatusCodes"]!.Errors, e => e.ErrorMessage == "Bad status codes");
    }

    [Fact]
    public void AddResults_UsesFormKey_WhenNoMembers()
    {
        var modelState = new ModelStateDictionary();
        var results = new[]
        {
            new ValidationResult("General monitor validation failed")
        };

        MonitorFormValidation.AddResults(modelState, results);

        Assert.True(modelState.ContainsKey("Form"));
        Assert.Contains(modelState["Form"]!.Errors, e => e.ErrorMessage == "General monitor validation failed");
    }
}
