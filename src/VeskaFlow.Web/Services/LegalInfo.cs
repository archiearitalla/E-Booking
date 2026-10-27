namespace VeskaFlow.Web.Services;

/// <summary>
/// The version of the Terms and the Privacy Notice shown on the site. Change it whenever either text changes in a way
/// owners should agree to again; it is stored with each shop's sign-up.
/// </summary>
public static class LegalInfo
{
    public const string Version = "2026-10-27";

    /// <summary>Who runs the service, for the legal pages. Set <c>Legal:OperatorName</c>; the default is the studio name.</summary>
    public static string OperatorName(IConfiguration config) => config["Legal:OperatorName"] is { Length: > 0 } n ? n : "Veskacode";

    /// <summary>The contact for privacy questions and requests: <c>Legal:ContactEmail</c>, else the contact page set in <c>Trial:ContactUrl</c>.</summary>
    public static string ContactText(IConfiguration config) =>
        config["Legal:ContactEmail"] is { Length: > 0 } email ? email
        : config["Trial:ContactUrl"] is { Length: > 0 } url ? url : "the contact page on veskacode.com";
}
