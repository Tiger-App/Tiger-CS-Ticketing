using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Diagnostics of the financial-source reconciliation: whether the source is reconciled and, if not, exactly why (sign-off missing, never run, failed with these units, could not complete).
/// Opening the page only READS the stored result; PACT is read only when someone presses Run.
/// </summary>
public sealed class ReconciliationModel(CollectionsApiClient api) : PageModel
{
    public ReconciliationOverviewDto? Overview { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    [TempData] public string? Notice { get; set; }
    public bool Running => Overview?.Latest?.Status == ReconciliationStates.Running;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var result = await api.GetReconciliationAsync(ct);
        Outcome = result.Outcome; Error = result.Detail;
        Overview = result.IsSuccess ? result.Value : null;
    }

    public async Task<IActionResult> OnPostRunAsync(int? companyId, CancellationToken ct)
    {
        var result = await api.StartReconciliationAsync(companyId, ct);
        Notice = result.IsSuccess ? "The snapshot-consistency check was started. This page refreshes while it runs."
            : result.Outcome == ApiOutcome.Forbidden ? "Your account does not have permission to run a reconciliation."
            : result.Detail ?? "The reconciliation could not be started.";
        return RedirectToPage();
    }

    public static string StateText(string state) => state switch
    {
        ReconciliationStates.Passed => "Passed: the snapshot is consistent with the PACT procedure",
        ReconciliationStates.Failed => "Failed: real differences found",
        ReconciliationStates.Error => "Could not be completed (no finding about the data)",
        ReconciliationStates.Running => "Running",
        ReconciliationStates.Expired => "Expired: older than allowed",
        ReconciliationStates.Superseded => "Superseded: the snapshot was refreshed since",
        _ => "Not run"
    };

    public static string TriggerText(string trigger) => trigger == ReconciliationTriggers.AfterRefresh ? "run by itself after the snapshot refresh" : "run on request";

    public static string GateCodeText(string code) => code switch
    {
        SourceGate.Reconciled => "Reconciled",
        SourceGate.ReconciledWithHeldUnits => "Reconciled; some units held back",
        _ => "Not reconciled"
    };

    public static string KindText(string kind) => kind switch
    {
        "MissingInSnapshot" => "In PACT, not in the snapshot",
        "MissingInSource" => "In the snapshot, not in PACT any more",
        "AmountMismatch" => "Different remaining amount",
        "DueDateMismatch" => "Different due date",
        _ => kind
    };
}
