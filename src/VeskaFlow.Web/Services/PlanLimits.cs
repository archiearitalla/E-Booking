namespace VeskaFlow.Web.Services;

/// <summary>Plan limits (BUSINESS_RULES section 12). During the pilot every feature is free; only the staff limit applies.</summary>
public static class PlanLimits
{
    public const int FreeMaxStaff = 1;

    /// <summary>Maximum active staff for a plan, or null when unlimited. A business on a trial has every feature, so no limit. PRO limits are not decided yet.</summary>
    public static int? MaxStaff(string plan, bool inTrial = false) => inTrial ? null : plan == "FREE" ? FreeMaxStaff : null;
}
