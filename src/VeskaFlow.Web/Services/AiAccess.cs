using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>
/// Who may use the AI receptionist and how much. It is a paid add-on: a shop has it only while
/// <c>business.ai_addon_until_utc</c> is in the future (the platform owner sets that after payment), and only while the
/// model service has a key. Each shop also has a monthly number of conversations, so a burst of messages cannot cost more
/// than the add-on earns. Every check is made on the server; the booking page merely hides the chat when it fails.
/// </summary>
public sealed class AiAccess(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, IConfiguration config, IAiModel model)
{
    /// <summary>Conversations one shop may start per UTC month (AiReceptionist:MonthlyConversations). 0 stops new ones.</summary>
    public int MonthlyConversations => Math.Max(0, config.GetValue("AiReceptionist:MonthlyConversations", 300));

    private DateTime Now => time.GetUtcNow().UtcDateTime;
    private DateOnly ThisMonth => new(Now.Year, Now.Month, 1);

    /// <summary>True when the shop has paid for the add-on and the model service is set up.</summary>
    public async Task<bool> IsActiveAsync(Guid businessId, CancellationToken ct = default)
    {
        if (!model.IsConfigured) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var until = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => b.AiAddonUntilUtc).FirstOrDefaultAsync(ct);
        return until is { } u && DateTime.SpecifyKind(u, DateTimeKind.Utc) > Now;
    }

    /// <summary>The limits in force for one shop: its own where the platform owner set one, otherwise the defaults from settings.</summary>
    public static AiLimits Effective(IConfiguration config, int? dailyOverride, int? monthlyOverride, long? tokenOverride)
    {
        var daily = dailyOverride ?? Math.Max(0, config.GetValue("AiReceptionist:DailyConversations", 0));
        var tokens = tokenOverride ?? Math.Max(0, config.GetValue("AiReceptionist:MonthlyTokens", 0L));
        // From settings, 0 means "no limit" for the day and for tokens; a limit the platform owner typed for one shop, 0 included, is the limit.
        return new AiLimits(
            dailyOverride is null && daily == 0 ? null : daily,
            monthlyOverride ?? Math.Max(0, config.GetValue("AiReceptionist:MonthlyConversations", 300)),
            tokenOverride is null && tokens == 0 ? null : tokens);
    }

    /// <summary>
    /// Counts one new conversation against this month's and today's limits, each in one statement and both in one transaction, so
    /// parallel starts cannot pass a limit together and a refusal by one limit counts nothing. False when a limit is used up, or when
    /// this month's tokens have reached the shop's token limit.
    /// </summary>
    public async Task<bool> TryStartConversationAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => new { b.AiDailyLimit, b.AiMonthlyLimit, b.AiTokenLimit }).FirstOrDefaultAsync(ct);
        if (shop is null) return false;
        var limits = Effective(config, shop.AiDailyLimit, shop.AiMonthlyLimit, shop.AiTokenLimit);
        if (limits.Monthly == 0 || limits.Daily == 0) return false;

        var month = ThisMonth;
        if (limits.Tokens is { } cap)
        {
            var used = await db.AiUsages.AsNoTracking().Where(u => u.Month == month && u.BusinessId == businessId)
                .Select(u => u.InputTokens + u.OutputTokens).FirstOrDefaultAsync(ct);
            if (used >= cap) return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monthly = limits.Monthly;
        var okMonth = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai_usage (month, business_id, conversations) VALUES ({month}, {businessId}, 1)
            ON CONFLICT (month, business_id) DO UPDATE SET conversations = ai_usage.conversations + 1
            WHERE ai_usage.conversations < {monthly}
            """, ct) > 0;
        if (!okMonth) return false;
        var day = DateOnly.FromDateTime(Now);
        var dailyCap = limits.Daily ?? int.MaxValue;
        var okDay = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai_usage_day (day, business_id, conversations) VALUES ({day}, {businessId}, 1)
            ON CONFLICT (day, business_id) DO UPDATE SET conversations = ai_usage_day.conversations + 1
            WHERE ai_usage_day.conversations < {dailyCap}
            """, ct) > 0;
        if (!okDay) { await tx.RollbackAsync(ct); return false; }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Adds the tokens one reply used to this month's total (for the owner's cost tracking).</summary>
    public async Task AddTokensAsync(Guid businessId, long inputTokens, long outputTokens, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var month = ThisMonth;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai_usage (month, business_id, input_tokens, output_tokens) VALUES ({month}, {businessId}, {inputTokens}, {outputTokens})
            ON CONFLICT (month, business_id) DO UPDATE
            SET input_tokens = ai_usage.input_tokens + {inputTokens}, output_tokens = ai_usage.output_tokens + {outputTokens}
            """, ct);
    }

    /// <summary>When the shop's add-on ends (null: never bought), for its Settings page.</summary>
    public async Task<DateTime?> AddonUntilAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var until = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.AiAddonUntilUtc).FirstOrDefaultAsync(ct);
        return until is { } u ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : null;
    }

    /// <summary>This month's numbers for one shop, for its Settings page.</summary>
    public async Task<AiUsageSummary> UsageAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var month = ThisMonth;
        var row = await db.AiUsages.AsNoTracking().FirstOrDefaultAsync(u => u.Month == month && u.BusinessId == businessId, ct);
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => new { b.AiDailyLimit, b.AiMonthlyLimit, b.AiTokenLimit }).FirstOrDefaultAsync(ct);
        var limit = Effective(config, shop?.AiDailyLimit, shop?.AiMonthlyLimit, shop?.AiTokenLimit).Monthly;
        return new AiUsageSummary(row?.Conversations ?? 0, limit, row?.InputTokens ?? 0, row?.OutputTokens ?? 0);
    }
}

/// <summary>Conversations per day (null: no daily limit), per month, and tokens per month (null: no token limit).</summary>
public sealed record AiLimits(int? Daily, int Monthly, long? Tokens);

public sealed record AiUsageSummary(int Conversations, int Limit, long InputTokens, long OutputTokens);
