namespace BarberBook.Web;

/// <summary>All user-facing text lives here so Tagalog can be added later without touching logic.</summary>
public static class Text
{
    public const string ErrorTitle = "Something went wrong";
    public const string ErrorBody = "We could not complete that request. Please try again in a moment.";
    public const string ErrorRequestId = "Reference";

    public const string ValidationFailed = "Please check the highlighted fields.";
    public const string NameInvalid = "Enter a name of 1 to 80 characters.";
    public const string PhoneInvalid = "Enter a phone number with 7 to 15 digits, optionally starting with +.";
    public const string EmailInvalid = "Enter a valid email address of at most 120 characters.";
    public const string IdempotencyKeyInvalid = "The form is missing its one-time key. Please reload the page.";
    public const string SlotTaken = "That time is no longer available. Please choose another.";
    public const string NotFound = "We could not find that.";
    public const string BookingChanged = "This booking was changed by someone else. Please refresh and try again.";
    public const string NotAllowed = "That action is not allowed.";
}
