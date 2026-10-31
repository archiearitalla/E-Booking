using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>One shop's AI numbers for one month, for the platform owner's summary.</summary>
public sealed record AiShopUsage(
    Guid BusinessId, string Name, string Slug, string OwnerEmail, DateTime? AddonUntilUtc, bool AddonActive,
    int Conversations, int ConversationsToday, long InputTokens, long OutputTokens,
    AiLimits Limits, int? DailyOverride, int? MonthlyOverride, long? TokenOverride, decimal CostUsd, decimal CostPhp)
{
    public long Tokens => InputTokens + OutputTokens;
}

public sealed record AiUsageReport(DateOnly Month, IReadOnlyList<AiShopUsage> Shops, decimal TotalCostUsd, decimal TotalCostPhp, int TotalConversations, long TotalTokens, bool PricesSet);

/// <summary>
/// The platform owner's view of the AI receptionist: every shop that has it (or used it) with this month's conversations, tokens and an
/// estimated cost, and the limits per shop (BUSINESS_RULES 6.y17). The cost is an estimate from the prices in settings
/// (<c>AiReceptionist:InputUsdPerMillionTokens</c>, <c>OutputUsdPerMillionTokens</c>, <c>UsdToPhp</c>); the real bill is on the provider's console.
/// </summary>
public sealed class AiAdminService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, IConfiguration config)
{
    public const int MaxDaily = 100_000, MaxMonthly = 1_000_000;
    public const long MaxTokens = 10_000_000_000;

    public decimal InputUsdPerMillion => config.GetValue("AiReceptionist:InputUsdPerMillionTokens", 1.00m);
    public decimal OutputUsdPerMillion => config.GetValue("AiReceptionist:OutputUsdPerMillionTokens", 5.00m);
    public decimal UsdToPhp => config.GetValue("AiReceptionist:UsdToPhp", 58m);

    public DateOnly CurrentMonth { get { var n = time.GetUtcNow().UtcDateTime; return new DateOnly(n.Year, n.Month, 1); } }

    public async Task<AiUsageReport> ReportAsync(DateOnly month, string? query, CancellationToken ct = default)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var now = time.GetUtcNow().UtcDateTime;

        var usage = await db.AiUsages.AsNoTracking().Where(u => u.Month == month).ToDictionaryAsync(u => u.BusinessId, ct);
        var days = await db.AiUsageDays.AsNoTracking().Where(d => d.Day == today).ToDictionaryAsync(d => d.BusinessId, d => d.Conversations, ct);
        var owners = (await db.AdminUsers.AsNoTracking().Where(u => u.Role == "OWNER").OrderBy(u => u.CreatedAtUtc)
            .Select(u => new { u.BusinessId, u.Email }).ToListAsync(ct)).GroupBy(u => u.BusinessId).ToDictionary(g => g.Key, g => g.First().Email);

        var shops = await db.Businesses.AsNoTracking()
            .Where(b => b.AiAddonUntilUtc != null || b.AiDailyLimit != null || b.AiMonthlyLimit != null || b.AiTokenLimit != null || db.AiUsages.Any(u => u.BusinessId == b.Id))
            .Select(b => new { b.Id, b.Name, b.Slug, b.AiAddonUntilUtc, b.AiDailyLimit, b.AiMonthlyLimit, b.AiTokenLimit }).ToListAsync(ct);

        var q = (query ?? "").Trim();
        var rows = new List<AiShopUsage>();
        foreach (var s in shops)
        {
            var email = owners.GetValueOrDefault(s.Id, "");
            if (q.Length > 0 && !(s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Slug.Contains(q, StringComparison.OrdinalIgnoreCase) || email.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            usage.TryGetValue(s.Id, out var u);
            var input = u?.InputTokens ?? 0; var output = u?.OutputTokens ?? 0;
            var usd = Math.Round(input / 1_000_000m * InputUsdPerMillion + output / 1_000_000m * OutputUsdPerMillion, 4);
            var until = s.AiAddonUntilUtc is { } x ? DateTime.SpecifyKind(x, DateTimeKind.Utc) : (DateTime?)null;
            rows.Add(new AiShopUsage(s.Id, s.Name, s.Slug, email, until, until is { } e && e > now, u?.Conversations ?? 0, days.GetValueOrDefault(s.Id), input, output,
                AiAccess.Effective(config, s.AiDailyLimit, s.AiMonthlyLimit, s.AiTokenLimit), s.AiDailyLimit, s.AiMonthlyLimit, s.AiTokenLimit,
                usd, Math.Round(usd * UsdToPhp, 2)));
        }
        rows = rows.OrderByDescending(r => r.CostUsd).ThenByDescending(r => r.Conversations).ThenBy(r => r.Name).ToList();
        return new AiUsageReport(month, rows, rows.Sum(r => r.CostUsd), rows.Sum(r => r.CostPhp), rows.Sum(r => r.Conversations), rows.Sum(r => r.Tokens),
            config.GetSection("AiReceptionist:InputUsdPerMillionTokens").Exists());
    }

    /// <summary>Sets a shop's own limits. A null leaves that limit on the default from settings; 0 stops new conversations for that limit.</summary>
    public async Task<ServiceResult<bool>> SetLimitsAsync(Guid businessId, int? daily, int? monthly, long? tokens, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (daily is < 0 or > MaxDaily) errors["daily"] = Text.AiLimitInvalid;
        if (monthly is < 0 or > MaxMonthly) errors["monthly"] = Text.AiLimitInvalid;
        if (tokens is < 0 or > MaxTokens) errors["tokens"] = Text.AiLimitInvalid;
        if (errors.Count > 0) return ServiceResult<bool>.Invalid(errors);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.AiDailyLimit, daily).SetProperty(b => b.AiMonthlyLimit, monthly).SetProperty(b => b.AiTokenLimit, tokens), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }
}
