using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// Administration → Review Tablets: register a tablet for an existing Customer Service employee, change who it is assigned to, switch it
/// on or off, issue a new activation code or revoke its credential. An 8-character activation code (valid 24 hours, one use) is shown exactly
/// once, in the response to the post that created it (never redirected, stored in TempData or logged); the Api keeps only a salted hash. The
/// tablet's own long device token is created when the tablet redeems the code and is never shown here. Reassigning affects future reviews only.
/// </summary>
public sealed class EmployeeReviewDevicesModel(EmployeeReviewsApiClient api) : AdminPageModel
{
    public IReadOnlyList<EmployeeReviewDeviceDto> Devices { get; private set; } = [];

    public IReadOnlyList<EligibleReviewEmployeeDto> Employees { get; private set; } = [];

    public string? LoadError { get; private set; }

    /// <summary>The activation code just issued and the tablet it belongs to; <c>null</c> on every other response.</summary>
    public EmployeeReviewActivationCodeDto? Issued { get; private set; }

    [BindProperty] public RegisterInput Register { get; set; } = new();

    [BindProperty] public EditInput Edit { get; set; } = new();

    /// <summary>The link carries the code in the <c>#fragment</c>, which browsers never send to a server and which web logs therefore never see.</summary>
    public string ActivationUrl(string code) => $"{Request.Scheme}://{Request.Host}/EmployeeReviews/Activate#code={code}";

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostRegisterAsync(CancellationToken cancellationToken)
    {
        var result = await api.RegisterAsync(new RegisterEmployeeReviewDeviceRequestDto(Register.DeviceName, Register.EmployeeId), cancellationToken);
        if (result.IsSuccess)
        {
            Issued = result.Value;
            StatusMessage = $"Tablet '{result.Value!.Device.DeviceName}' registered. Copy its 8-character activation code now — it is shown only once and expires in 24 hours.";
            return await ShowAsync(cancellationToken);
        }

        ErrorMessage = DescribeFailure(result, "The tablet could not be registered.");
        return await ShowAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken cancellationToken)
    {
        var result = await api.UpdateAsync(Edit.DeviceId, new UpdateEmployeeReviewDeviceRequestDto(Edit.DeviceName, Edit.EmployeeId), cancellationToken);
        return await FinishAsync(result.IsSuccess, $"Tablet '{result.Value?.DeviceName}' updated. New reviews go to {result.Value?.EmployeeName}.",
            DescribeFailure(result, "The tablet could not be updated."), cancellationToken);
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid deviceId, bool isActive, CancellationToken cancellationToken)
    {
        var result = await api.SetActiveAsync(deviceId, isActive, cancellationToken);
        return await FinishAsync(result.IsSuccess, $"Tablet '{result.Value?.DeviceName}' {(isActive ? "activated" : "deactivated")}.",
            DescribeFailure(result, "The tablet could not be changed."), cancellationToken);
    }

    /// <summary><paramref name="confirmReplace"/> is the administrator's tick on an already-activated tablet; the Api refuses without it, whatever this page does.</summary>
    public async Task<IActionResult> OnPostRegenerateAsync(Guid deviceId, bool confirmReplace, CancellationToken cancellationToken)
    {
        var result = await api.RegenerateAsync(deviceId, confirmReplace, cancellationToken);
        if (result.IsSuccess)
        {
            Issued = result.Value;
            StatusMessage = $"A new activation code was issued for '{result.Value!.Device.DeviceName}'. The tablet's previous credential no longer works. Copy the new code now — it is shown only once and expires in 24 hours.";
            return await ShowAsync(cancellationToken);
        }

        ErrorMessage = DescribeFailure(result, "The activation code could not be issued.");
        return await ShowAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var result = await api.RevokeAsync(deviceId, cancellationToken);
        return await FinishAsync(result.IsSuccess, $"Tablet '{result.Value?.DeviceName}' revoked. It can no longer collect reviews until a new activation code is issued and used.",
            DescribeFailure(result, "The credential could not be revoked."), cancellationToken);
    }

    /// <summary>A change with no secret to show: report it and go back to the list (post/redirect/get).</summary>
    private async Task<IActionResult> FinishAsync(bool success, string successMessage, string errorMessage, CancellationToken cancellationToken)
    {
        if (success)
        {
            StatusMessage = successMessage;
            return RedirectToPage();
        }

        ErrorMessage = errorMessage;
        return await ShowAsync(cancellationToken);
    }

    /// <summary>Renders this response directly: the status/error shown here belongs to this request, not the next one.</summary>
    private async Task<IActionResult> ShowAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var devices = await api.GetDevicesAsync(cancellationToken);
        var employees = await api.GetEligibleEmployeesAsync(cancellationToken);
        if (devices.IsSuccess)
        {
            Devices = devices.Value ?? [];
        }
        else
        {
            LoadError = DescribeFailure(devices, "The review tablets could not be loaded.");
        }

        Employees = employees.IsSuccess ? employees.Value ?? [] : [];
    }

    public sealed class RegisterInput
    {
        [Required, StringLength(100)] public string? DeviceName { get; set; }

        public Guid EmployeeId { get; set; }
    }

    public sealed class EditInput
    {
        public Guid DeviceId { get; set; }

        [Required, StringLength(100)] public string? DeviceName { get; set; }

        public Guid EmployeeId { get; set; }
    }
}
