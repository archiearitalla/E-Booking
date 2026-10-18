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

    public const string NotFoundTitle = "Page not found";
    public const string NotFoundBody = "We could not find what you were looking for. Check the link and try again.";
    public const string TooManyRequestsTitle = "Too many requests";
    public const string TooManyRequestsBody = "Please wait a minute and try again.";
    public const string BadRequestTitle = "That request could not be processed";
    public const string BadRequestBody = "Please go back, reload the page and try again.";
    public const string HomeLink = "Back to the home page";

    public const string BookTitle = "Book an appointment";
    public const string BookService = "Service";
    public const string BookStaff = "Barber";
    public const string BookStaffAny = "Any available";
    public const string BookDate = "Date";
    public const string BookShowTimes = "Show available times";
    public const string BookAvailableTimes = "Available times on";
    public const string BookNoTimes = "No times are available on this day. Try another date.";
    public const string BookPickService = "Choose a service to see available times.";
    public const string BookTaken = "That time was just taken. Please choose another time below.";
    public const string BookStaffMismatch = "That barber does not offer this service. Choose another barber or any available.";
    public const string BookNoServices = "This shop has not set up any services yet.";
    public const string BookYourDetails = "Your details";
    public const string BookSummary = "Your appointment";
    public const string BookLabelName = "Your name";
    public const string BookLabelPhone = "Mobile number";
    public const string BookHintPhone = "The shop uses this to reach you about your booking.";
    public const string BookLabelEmail = "Email (optional)";
    public const string BookConfirm = "Confirm booking";
    public const string BookPrivacy = "We keep your name and number only to manage your bookings with this shop. Ask the shop to delete them at any time.";
    public const string BookPayOnSite = "Pay at the shop.";
    public const string BookChangeTime = "Choose a different time";

    public const string ManageTitle = "Your booking";
    public const string ManageConfirmed = "Your booking is confirmed. Save this page: it is your link to view or cancel the booking.";
    public const string ManageCancelled = "Your booking was cancelled.";
    public const string ManageCancelButton = "Cancel booking";
    public const string ManageCancelHint = "You can cancel online until {0} hour(s) before your appointment.";
    public const string ManageStatus = "Status";
    public const string ManageWhen = "When";
    public const string ManageWho = "Name";
    public const string ManageWith = "With";
    public const string ManagePrice = "Price";
    public const string ManageShop = "Shop";
    public const string ManageService = "Service";
    public const string ManageClosed = "This booking can no longer be changed online.";

    public const string AdminTitle = "Dashboard";
    public const string AdminWelcome = "Welcome,";
    public const string AdminSoon = "Your bookings, services and staff will appear here.";
}
