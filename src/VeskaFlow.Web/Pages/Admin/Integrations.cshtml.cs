using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Connect the shop to n8n or a similar tool (BUSINESS_RULES 6.y24): the address events are posted to, the secret that signs them, a test button and the latest deliveries. Owner only.</summary>
[AdminAccess(AdminLevel.Owner)]
public class IntegrationsModel(WebhookService webhooks) : AdminPageModel
{
    [BindProperty] public IntegrationForm Input { get; set; } = new();

    public WebhookSettings Settings { get; private set; } = new(false, null, null);
    public IReadOnlyList<WebhookLogRow> Recent { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        Input = new IntegrationForm { Url = Settings.Url, Enabled = Settings.Enabled };
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var result = await webhooks.SaveAsync(BusinessId, Input.Url, Input.Enabled, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) { Success = Text.IntegrationsSaved; return RedirectToPage(); }
        Errors = result.FieldErrors;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRegenerateAsync(CancellationToken ct)
    {
        var result = await webhooks.RegenerateSecretAsync(BusinessId, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        Success = Text.IntegrationsSecretNew;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        var result = await webhooks.SendTestAsync(BusinessId, ct);
        if (result.Ok) Success = Text.IntegrationsTestOk(result.StatusCode ?? 200);
        else Failure = Text.IntegrationsTestFailed(result.Error ?? "?");
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var s = await webhooks.GetSettingsAsync(BusinessId, ct);
        if (s is null) return false;
        Settings = s;
        Recent = await webhooks.RecentAsync(BusinessId, 20, ct);
        return true;
    }

    public class IntegrationForm
    {
        public string? Url { get; set; }
        public bool Enabled { get; set; }
    }
}
