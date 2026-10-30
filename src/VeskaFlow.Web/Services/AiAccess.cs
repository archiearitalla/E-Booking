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

    /// <summary>
    /// Counts one new conversation against this month's limit, in one statement so parallel starts cannot pass the limit
    /// together. False when the limit is used up.
    /// </summary>
    public async Task<bool> TryStartConversationAsync(Guid businessId, CancellationToken ct = default)
    {
        var limit = MonthlyConversations;
        if (limit == 0) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var month = ThisMonth;
        var changed = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai_usage (month, business_id, conversations) VALUES ({month}, {businessId}, 1)
            ON CONFLICT (month, business_id) DO UPDATE SET conversations = ai_usage.conversations + 1
            WHERE ai_usage.conversations < {limit}
            """, ct);
        return changed > 0;
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
        return new AiUsageSummary(row?.Conversations ?? 0, MonthlyConversations, row?.InputTokens ?? 0, row?.OutputTokens ?? 0);
    }
}

public sealed record AiUsageSummary(int Conversations, int Limit, long InputTokens, long OutputTokens);
