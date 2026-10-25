namespace VeskaFlow.Web.Pages;

/// <summary>
/// A ready-made message with every way to send it: copy, text, email, share to any app, and save the customer's contact.
/// Nothing is sent by the system; the staff member sends it from their own phone.
/// </summary>
public sealed record SendMessageModel(string Id, string Message, string Phone, string? Email, string Subject, string ContactUrl);
