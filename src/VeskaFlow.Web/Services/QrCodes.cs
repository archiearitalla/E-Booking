using QRCoder;

namespace VeskaFlow.Web.Services;

/// <summary>QR codes for a shop's booking link (BUSINESS_RULES 8.7). Pure and deterministic: the same URL gives the same image.</summary>
public static class QrCodes
{
    /// <summary>The booking page address for a shop. <paramref name="publicBaseUrl"/> (configuration) wins over the request's own host.</summary>
    public static string BookingUrl(string? publicBaseUrl, string requestScheme, string requestHost, string slug)
    {
        var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? $"{requestScheme}://{requestHost}" : publicBaseUrl.Trim().TrimEnd('/');
        return $"{baseUrl}/b/{slug}";
    }

    /// <summary>An SVG that scales to any size (viewBox, no fixed width), black on white with a quiet zone so phones can scan it from paper.</summary>
    public static string Svg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(10, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
