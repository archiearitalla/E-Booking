namespace VeskaFlow.Web.Services;

public static class PlanLimits
{
    /// <summary>A shop with no access end date and no paid plan (not a client): one staff member.</summary>
    public const int FreeMaxStaff = 1;

    /// <summary>Staff included in the price (trial and paid). More staff are paid for, one by one, and set by the platform owner.</summary>
    public const int IncludedStaff = 5;

    /// <summary>Most extra staff the platform owner can give one shop.</summary>
    public const int MaxExtraStaff = 100;

    /// <summary>
    /// Maximum active staff: the included 5 plus the extra staff the shop paid for, for a trial or a paid plan; 1 for a shop that is neither.
    /// A shop already over the number is not cut down; it just cannot activate more.
    /// </summary>
    public static int MaxStaff(string plan, bool inTrial = false, int extraStaff = 0) =>
        inTrial || plan == "PRO" ? IncludedStaff + Math.Clamp(extraStaff, 0, MaxExtraStaff) : FreeMaxStaff;

    /// <summary>
    /// Most staff records a shop can have, active and deactivated together (deactivated staff are kept for history, so without this
    /// someone could add and deactivate staff without end and fill the database).
    /// </summary>
    public static int MaxStaffRecords(int extraStaff = 0) => 20 + Math.Clamp(extraStaff, 0, MaxExtraStaff);
}
