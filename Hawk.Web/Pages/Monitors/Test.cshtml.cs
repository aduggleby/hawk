// <file>
// <summary>
// Interactive diagnostics page: executes a monitor immediately and shows raw request/response details.
// </summary>
// </file>

using Hawk.Web.Services.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Monitors;

public sealed class TestModel(IMonitorExecutor executor) : PageModel
{
    public MonitorExecutionResult? Execution { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Execution = await executor.ExecuteAsync(id, "manual", cancellationToken);
        if (Execution is null)
            return NotFound();

        return Page();
    }
}

