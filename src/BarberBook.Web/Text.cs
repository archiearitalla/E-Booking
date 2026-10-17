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

    public const string AppName = "BarberBook";
    public const string BusinessNameInvalid = "Enter your shop name (1 to 80 characters).";
    public const string SlugInvalid = "Use 3 to 40 lowercase letters, numbers and single hyphens.";
    public const string SlugTaken = "That booking address is already taken. Please choose another.";
    public const string OwnerNameInvalid = "Enter your name (1 to 80 characters).";
    public const string EmailTaken = "An account with that email already exists. Try signing in.";
    public const string PasswordInvalid = "Use a password of 8 to 128 characters.";
    public const string LoginFailed = "Email or password is incorrect.";
    public const string DefaultServiceName = "Haircut";
    public const string RegisterTitle = "Create your shop";
    public const string LoginTitle = "Sign in";

    public const string NavDashboard = "Dashboard";
    public const string NavSignIn = "Sign in";
    public const string NavSignOut = "Sign out";
    public const string NavRegister = "Create your shop";
    public const string HomeTitle = "Online booking for barbershops and salons";
    public const string HomeBody = "Let customers pick a service and a time. No double bookings, no phone tag.";
    public const string HomeHaveShop = "Already have a shop?";
    public const string LoginNew = "New here?";

    public const string LabelShopName = "Shop name";
    public const string LabelSlug = "Booking address";
    public const string HintSlug = "Optional. Letters, numbers and hyphens. Leave blank to use your shop name. Your page will be at /b/your-address.";
    public const string LabelOwnerName = "Your name";
    public const string LabelEmail = "Email";
    public const string LabelPassword = "Password";
    public const string HintPassword = "At least 8 characters.";
    public const string ButtonRegister = "Create shop";
    public const string ButtonSignIn = "Sign in";

    public const string AdminTitle = "Dashboard";
    public const string AdminWelcome = "Welcome,";
    public const string AdminSoon = "Your bookings, services and staff will appear here.";
}
