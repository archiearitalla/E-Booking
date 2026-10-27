namespace VeskaFlow.Web.Services;

/// <summary>
/// How a client renews, shown on the trial-ended page and in About. All of it comes from settings, so the price and the
/// payment details are never in the code: <c>Billing:Price</c> (for example "PHP 499 per month"), <c>Billing:PayTo</c>
/// (for example "GCash 09XX XXX XXXX, Juan Dela Cruz") and <c>Billing:Note</c> (anything else, such as where to send the proof).
/// </summary>
public sealed class BillingInfo(IConfiguration config)
{
    public string Price => Clean(config["Billing:Price"]);
    public string PayTo => Clean(config["Billing:PayTo"]);
    public string Note => Clean(config["Billing:Note"]);
    public string ContactUrl => Clean(config["Trial:ContactUrl"]);

    /// <summary>The price of the more-emails add-on, for example "PHP 199 per month" (Billing:EmailAddonPrice).</summary>
    public string EmailAddonPrice => Clean(config["Billing:EmailAddonPrice"]);

    /// <summary>True when there is anything to tell a client about paying.</summary>
    public bool HasDetails => Price.Length > 0 || PayTo.Length > 0 || Note.Length > 0;

    private static string Clean(string? value) => (value ?? "").Trim();
}
