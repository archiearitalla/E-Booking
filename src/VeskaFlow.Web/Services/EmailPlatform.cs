using Microsoft.Extensions.DependencyInjection;

namespace VeskaFlow.Web.Services;

/// <summary>
/// Whether the platform can send email at all (an email service is set up by the platform owner). The email features of a shop (emails to its
/// customers, "Require email confirmation to book") can only be switched on while it is, and a shop's saved switch is ignored while it is not,
/// so customers are never locked out by a switch that can no longer be served.
/// </summary>
public interface IEmailPlatform
{
    bool IsEnabled { get; }
}

public sealed class EmailPlatform([FromKeyedServices("raw")] IEmailSender raw) : IEmailPlatform
{
    public bool IsEnabled => raw.IsConfigured;
}
