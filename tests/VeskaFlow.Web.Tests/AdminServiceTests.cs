using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Back-office services against real PostgreSQL: tenant scoping, plan limit and the "never silently cancel" warnings.</summary>
public class AdminServiceTests
{
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Tenant(Guid BusinessId, Guid StaffId, Guid ServiceId);

    private static TestClock Clock() => new(BaseNow);

    private static async Task<(TestDbFactory Db, CatalogAdminService Catalog, ShopSettingsService Settings, AdminDayService Days, BookingService Bookings)> StackAsync(TestClock? clock = null)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        clock ??= Clock();
        return (db, new CatalogAdminService(db, clock), new ShopSettingsService(db, clock), new AdminDayService(db, clock), new BookingService(db, clock));
    }

    /// <summary>A shop created through onboarding (FREE plan, one staff member, one service, Mon-Sat 09:00-18:00).</summary>
    private static async Task<Tenant> NewTenantAsync(TestDbFactory db)
    {
        var owner = (await new OnboardingService(db).RegisterAsync(
            new RegisterRequest("Shop", Unique("adm"), "Owner", $"{Unique("o")}@example.com", "correct-horse-1"))).Value!;
        await using var ctx = db.CreateDbContext();
        return new Tenant(owner.BusinessId,
            await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync(),
            await ctx.Services.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync());
    }

    private static async Task<BookingSummary> BookAsync(BookingService svc, Tenant t, DateTime start, string phone = "09170000001")
    {
        var r = await svc.CreateAsync(new CreateBookingRequest(t.BusinessId, t.ServiceId, t.StaffId, start, "Juan", phone, null,
            Unique("k"), BookingActor.Customer("Juan")));
        Assert.True(r.IsSuccess, r.Message);
        return r.Value!.Booking;
    }

    // ------------------------------------------------------------ services

    [Fact]
    public async Task Services_can_be_added_edited_and_deactivated_and_invalid_input_is_refused()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var t = await NewTenantAsync(db);

        var created = await catalog.SaveServiceAsync(t.BusinessId, new ServiceInput(null, "Beard trim", 20, 5, 80.5m, true, 3));
        Assert.True(created.IsSuccess);
        var row = (await catalog.GetServiceAsync(t.BusinessId, created.Value))!;
        Assert.Equal((20, 5, 80.5m, 3), (row.DurationMinutes, row.BufferMinutes, row.Price, row.SortOrder));

        var edited = await catalog.SaveServiceAsync(t.BusinessId, new ServiceInput(created.Value, "Beard trim+", 25, 0, 90m, false, 3));
        Assert.True(edited.IsSuccess);
        Assert.False((await catalog.GetServiceAsync(t.BusinessId, created.Value))!.IsActive);

        var bad = await catalog.SaveServiceAsync(t.BusinessId, new ServiceInput(null, "", 1, 999, -5, true, -1));
        Assert.Equal(FailureKind.Validation, bad.Failure);
        foreach (var f in new[] { "name", "durationMinutes", "bufferMinutes", "price", "sortOrder" })
            Assert.Contains(f, bad.FieldErrors.Keys);
    }

    [Fact]
    public async Task Another_business_cannot_read_or_change_a_service()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);

        Assert.Null(await catalog.GetServiceAsync(b.BusinessId, a.ServiceId));
        var forged = await catalog.SaveServiceAsync(b.BusinessId, new ServiceInput(a.ServiceId, "Hacked", 30, 0, 1m, true, 0));

        Assert.Equal(FailureKind.NotFound, forged.Failure);
        Assert.Equal("Haircut", (await catalog.GetServiceAsync(a.BusinessId, a.ServiceId))!.Name);
        Assert.DoesNotContain(await catalog.ListServicesAsync(b.BusinessId), s => s.Id == a.ServiceId);
    }

    // ------------------------------------------------------------ staff and plan limit

    [Fact]
    public async Task The_free_plan_allows_only_one_active_staff_member()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var t = await NewTenantAsync(db);

        var second = await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(null, "Carl", true, 1, [t.ServiceId]));
        Assert.Equal(FailureKind.Validation, second.Failure);
        Assert.Contains("isActive", second.FieldErrors.Keys);
        Assert.Single(await catalog.ListStaffAsync(t.BusinessId));

        // An inactive second person is fine, but cannot be switched on while the first is active.
        var inactive = await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(null, "Carl", false, 1, [t.ServiceId]));
        Assert.True(inactive.IsSuccess);
        var reactivate = await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(inactive.Value!.Id, "Carl", true, 1, [t.ServiceId]));
        Assert.Equal(FailureKind.Validation, reactivate.Failure);

        // Swap: deactivate the first, then the second can be activated.
        Assert.True((await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(t.StaffId, "Owner", false, 0, [t.ServiceId]))).IsSuccess);
        Assert.True((await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(inactive.Value.Id, "Carl", true, 1, [t.ServiceId]))).IsSuccess);
    }


    [Fact]
    public async Task A_business_on_a_trial_has_no_staff_limit_and_one_without_a_trial_keeps_the_free_limit()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var trial = (await new OnboardingService(db).RegisterAsync(
            new RegisterRequest("Clinic", Unique("tr"), "Dr Reyes", $"{Unique("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        var plain = await NewTenantAsync(db);
        var trialService = await db.CreateDbContext().Services.Where(s => s.BusinessId == trial.BusinessId).Select(s => s.Id).SingleAsync();

        foreach (var name in new[] { "Dr Cruz", "Dr Lim", "Dr Tan" })
            Assert.True((await catalog.SaveStaffAsync(trial.BusinessId, new StaffInput(null, name, true, 1, [trialService]))).IsSuccess);
        await using var ctx = db.CreateDbContext();
        Assert.Equal(4, await ctx.StaffMembers.CountAsync(s => s.BusinessId == trial.BusinessId && s.IsActive));

        var refused = await catalog.SaveStaffAsync(plain.BusinessId, new StaffInput(null, "Carl", true, 1, [plain.ServiceId]));
        Assert.Equal(FailureKind.Validation, refused.Failure);
    }
    [Fact]
    public async Task Parallel_attempts_to_add_staff_cannot_exceed_the_plan_limit()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var t = await NewTenantAsync(db);
        // free the one seat first
        await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(t.StaffId, "Owner", false, 0, [t.ServiceId]));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => catalog.SaveStaffAsync(t.BusinessId, new StaffInput(null, $"New {i}", true, i, [t.ServiceId])))));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        await using var ctx = db.CreateDbContext();
        Assert.Equal(1, await ctx.StaffMembers.CountAsync(s => s.BusinessId == t.BusinessId && s.IsActive));
    }

    [Fact]
    public async Task Staff_service_links_are_replaced_and_a_foreign_service_id_rejects_the_whole_save()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);
        var second = (await catalog.SaveServiceAsync(a.BusinessId, new ServiceInput(null, "Shave", 30, 0, 100m, true, 1))).Value;

        Assert.True((await catalog.SaveStaffAsync(a.BusinessId, new StaffInput(a.StaffId, "Owner", true, 0, [second]))).IsSuccess);
        var view = (await catalog.GetStaffAsync(a.BusinessId, a.StaffId))!;
        Assert.Equal(new[] { second }, view.ServiceIds.ToArray());

        var forged = await catalog.SaveStaffAsync(a.BusinessId, new StaffInput(a.StaffId, "Renamed", true, 0, [a.ServiceId, b.ServiceId]));
        Assert.Equal(FailureKind.NotFound, forged.Failure);
        var after = (await catalog.GetStaffAsync(a.BusinessId, a.StaffId))!;
        Assert.Equal("Owner", after.Name);
        Assert.Equal(new[] { second }, after.ServiceIds.ToArray());
    }

    [Fact]
    public async Task Another_business_cannot_read_or_change_a_staff_member_or_their_hours()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);
        var before = (await catalog.GetStaffAsync(a.BusinessId, a.StaffId))!.Hours.ToList();

        Assert.Null(await catalog.GetStaffAsync(b.BusinessId, a.StaffId));
        Assert.Equal(FailureKind.NotFound, (await catalog.SaveStaffAsync(b.BusinessId, new StaffInput(a.StaffId, "Hacked", true, 0, []))).Failure);
        var hours = await catalog.SaveHoursAsync(b.BusinessId, a.StaffId, [new HourWindow(1, new TimeOnly(0, 0), new TimeOnly(23, 0))]);
        Assert.Equal(FailureKind.NotFound, hours.Failure);

        var after = (await catalog.GetStaffAsync(a.BusinessId, a.StaffId))!;
        Assert.Equal("Owner", after.Name);
        Assert.Equal(before, after.Hours.ToList());
    }

    [Fact]
    public async Task Deactivating_staff_reports_the_upcoming_bookings_and_cancels_none()
    {
        var (db, catalog, _, _, bookings) = await StackAsync();
        var t = await NewTenantAsync(db);
        var booking = await BookAsync(bookings, t, Manila(5, 10));

        var result = await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(t.StaffId, "Owner", false, 0, [t.ServiceId]));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.AffectedBookings);
        Assert.Equal(BookingStatus.Confirmed, (await bookings.LoadSummaryAsync(t.BusinessId, booking.Id))!.Status);
    }

    // ------------------------------------------------------------ weekly hours

    [Fact]
    public async Task Changing_hours_counts_bookings_left_outside_them_and_never_cancels()
    {
        var (db, catalog, _, _, bookings) = await StackAsync();
        var t = await NewTenantAsync(db);
        var morning = await BookAsync(bookings, t, Manila(5, 10), "09170000001"); // Tuesday 10:00
        var evening = await BookAsync(bookings, t, Manila(5, 16), "09170000002"); // Tuesday 16:00

        // Tuesday now runs 09:00-12:00 only, plus Monday and Wednesday as before.
        var windows = new List<HourWindow>
        {
            new(1, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            new(2, new TimeOnly(9, 0), new TimeOnly(12, 0)),
            new(3, new TimeOnly(9, 0), new TimeOnly(18, 0)),
        };
        var result = await catalog.SaveHoursAsync(t.BusinessId, t.StaffId, windows);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.AffectedBookings); // the 16:00 booking
        Assert.Equal(BookingStatus.Confirmed, (await bookings.LoadSummaryAsync(t.BusinessId, morning.Id))!.Status);
        Assert.Equal(BookingStatus.Confirmed, (await bookings.LoadSummaryAsync(t.BusinessId, evening.Id))!.Status);
        Assert.Equal(3, (await catalog.GetStaffAsync(t.BusinessId, t.StaffId))!.Hours.Count);
    }

    [Fact]
    public async Task Overlapping_or_empty_windows_are_refused_and_nothing_changes()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var t = await NewTenantAsync(db);
        var before = (await catalog.GetStaffAsync(t.BusinessId, t.StaffId))!.Hours.Count;

        var overlap = await catalog.SaveHoursAsync(t.BusinessId, t.StaffId,
            [new HourWindow(1, new TimeOnly(9, 0), new TimeOnly(13, 0)), new HourWindow(1, new TimeOnly(12, 0), new TimeOnly(18, 0))]);
        var empty = await catalog.SaveHoursAsync(t.BusinessId, t.StaffId, [new HourWindow(1, new TimeOnly(9, 0), new TimeOnly(9, 0))]);

        Assert.Equal(FailureKind.Validation, overlap.Failure);
        Assert.Equal(FailureKind.Validation, empty.Failure);
        Assert.Equal(before, (await catalog.GetStaffAsync(t.BusinessId, t.StaffId))!.Hours.Count);
    }

    [Fact]
    public async Task A_split_shift_and_a_window_past_midnight_are_accepted()
    {
        var (db, catalog, _, _, _) = await StackAsync();
        var t = await NewTenantAsync(db);

        var result = await catalog.SaveHoursAsync(t.BusinessId, t.StaffId,
        [
            new HourWindow(5, new TimeOnly(9, 0), new TimeOnly(12, 0)),
            new HourWindow(5, new TimeOnly(13, 0), new TimeOnly(18, 0)),
            new HourWindow(6, new TimeOnly(18, 0), new TimeOnly(2, 0)),
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, (await catalog.GetStaffAsync(t.BusinessId, t.StaffId))!.Hours.Count);
    }

    // ------------------------------------------------------------ closures

    [Fact]
    public async Task Closing_a_day_cancels_nothing_reports_the_affected_bookings_and_blocks_new_ones()
    {
        var (db, _, settings, _, bookings) = await StackAsync();
        var t = await NewTenantAsync(db);
        var existing = await BookAsync(bookings, t, Manila(5, 10)); // Tuesday 2030-03-05

        var result = await settings.AddClosureAsync(t.BusinessId, new ClosureInput(null, new DateOnly(2030, 3, 5), new DateOnly(2030, 3, 5), "Holiday"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.AffectedBookings);
        Assert.Equal(BookingStatus.Confirmed, (await bookings.LoadSummaryAsync(t.BusinessId, existing.Id))!.Status);

        var free = await bookings.GetFreeStartsAsync(t.BusinessId, t.ServiceId, null, new DateOnly(2030, 3, 5));
        Assert.Empty(free.Value!);
        var other = await bookings.GetFreeStartsAsync(t.BusinessId, t.ServiceId, null, new DateOnly(2030, 3, 6));
        Assert.NotEmpty(other.Value!);

        var listed = (await settings.ListClosuresAsync(t.BusinessId)).Single();
        Assert.Equal(1, listed.AffectedBookings);
        Assert.Equal(new DateOnly(2030, 3, 5), listed.FromDate);
        Assert.Equal(new DateOnly(2030, 3, 5), listed.ToDate);
    }

    [Fact]
    public async Task A_closure_for_one_staff_member_only_affects_that_person()
    {
        var (db, catalog, settings, _, bookings) = await StackAsync();
        var t = await NewTenantAsync(db);
        // two seats: free the plan limit by switching the owner's plan to PRO for this test shop
        await using (var ctx = db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == t.BusinessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.Plan, "PRO"));
        var carl = (await catalog.SaveStaffAsync(t.BusinessId, new StaffInput(null, "Carl", true, 1, [t.ServiceId]))).Value!.Id;
        var ownerBooking = await BookAsync(bookings, t, Manila(5, 10));
        Assert.True((await bookings.CreateAsync(new CreateBookingRequest(t.BusinessId, t.ServiceId, carl, Manila(5, 10), "Maria", "09170000009", null, Unique("k"), BookingActor.Customer("Maria")))).IsSuccess);

        var result = await settings.AddClosureAsync(t.BusinessId, new ClosureInput(carl, new DateOnly(2030, 3, 5), new DateOnly(2030, 3, 5), null));

        Assert.Equal(1, result.Value!.AffectedBookings); // only Carl's booking
        Assert.Equal(BookingStatus.Confirmed, (await bookings.LoadSummaryAsync(t.BusinessId, ownerBooking.Id))!.Status);
    }

    [Fact]
    public async Task Another_business_cannot_remove_a_closure_or_close_for_foreign_staff()
    {
        var (db, _, settings, _, _) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);
        var closure = (await settings.AddClosureAsync(a.BusinessId, new ClosureInput(null, new DateOnly(2030, 3, 5), new DateOnly(2030, 3, 6), null))).Value!;

        var removed = await settings.DeleteClosureAsync(b.BusinessId, closure.Id);
        var foreignStaff = await settings.AddClosureAsync(b.BusinessId, new ClosureInput(a.StaffId, new DateOnly(2030, 3, 5), new DateOnly(2030, 3, 5), null));

        Assert.Equal(FailureKind.NotFound, removed.Failure);
        Assert.Equal(FailureKind.NotFound, foreignStaff.Failure);
        Assert.Single(await settings.ListClosuresAsync(a.BusinessId));
        Assert.Empty(await settings.ListClosuresAsync(b.BusinessId));
        Assert.True((await settings.DeleteClosureAsync(a.BusinessId, closure.Id)).IsSuccess);
    }

    [Fact]
    public async Task Invalid_closures_are_refused()
    {
        var (db, _, settings, _, _) = await StackAsync();
        var t = await NewTenantAsync(db);

        var backwards = await settings.AddClosureAsync(t.BusinessId, new ClosureInput(null, new DateOnly(2030, 3, 6), new DateOnly(2030, 3, 5), null));
        var tooLong = await settings.AddClosureAsync(t.BusinessId, new ClosureInput(null, new DateOnly(2030, 3, 5), new DateOnly(2032, 3, 5), null));
        var reason = await settings.AddClosureAsync(t.BusinessId, new ClosureInput(null, new DateOnly(2030, 3, 5), new DateOnly(2030, 3, 5), new string('x', 121)));

        Assert.Equal(FailureKind.Validation, backwards.Failure);
        Assert.Equal(FailureKind.Validation, tooLong.Failure);
        Assert.Equal(FailureKind.Validation, reason.Failure);
        Assert.Empty(await settings.ListClosuresAsync(t.BusinessId));
    }

    // ------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_are_validated_saved_per_business_and_report_a_time_zone_change()
    {
        var (db, _, settings, _, _) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);

        var bad = await settings.SaveSettingsAsync(a.BusinessId, new SettingsInput("", "Mars/Olympus", 7, -1, 0, -2));
        Assert.Equal(FailureKind.Validation, bad.Failure);
        foreach (var f in new[] { "name", "timeZone", "slotMinutes", "leadTimeMinutes", "horizonDays", "cancelCutoffHours" })
            Assert.Contains(f, bad.FieldErrors.Keys);

        var sameZone = await settings.SaveSettingsAsync(a.BusinessId, new SettingsInput("Ben's", "Asia/Manila", 15, 30, 60, 4));
        Assert.True(sameZone.IsSuccess);
        Assert.False(sameZone.Value);
        var changedZone = await settings.SaveSettingsAsync(a.BusinessId, new SettingsInput("Ben's", "Asia/Tokyo", 15, 30, 60, 4));
        Assert.True(changedZone.Value);

        var savedA = (await settings.GetSettingsAsync(a.BusinessId))!;
        Assert.Equal((15, 30, 60, 4, "Asia/Tokyo"), (savedA.SlotMinutes, savedA.LeadTimeMinutes, savedA.HorizonDays, savedA.CancelCutoffHours, savedA.TimeZone));
        var untouchedB = (await settings.GetSettingsAsync(b.BusinessId))!;
        Assert.Equal((30, 60, 30, 2, "Asia/Manila"), (untouchedB.SlotMinutes, untouchedB.LeadTimeMinutes, untouchedB.HorizonDays, untouchedB.CancelCutoffHours, untouchedB.TimeZone));
    }

    [Fact]
    public async Task Brand_color_defaults_to_teal_accepts_only_presets_and_is_kept_when_not_sent()
    {
        var (db, _, settings, _, _) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);
        Assert.Equal("teal", (await settings.GetSettingsAsync(a.BusinessId))!.BrandColor);

        var bad = await settings.SaveSettingsAsync(a.BusinessId, new SettingsInput("Shop", "Asia/Manila", 30, 60, 30, 2, "<script>"));
        Assert.Equal(FailureKind.Validation, bad.Failure);
        Assert.Contains("brandColor", bad.FieldErrors.Keys);

        Assert.True((await settings.SaveSettingsAsync(a.BusinessId, new SettingsInput("Shop", "Asia/Manila", 30, 60, 30, 2, "orange"))).IsSuccess);
        Assert.Equal("orange", (await settings.GetSettingsAsync(a.BusinessId))!.BrandColor);

        // A save that does not mention the color leaves it alone, and another business is never touched.
        Assert.True((await settings.SaveSettingsAsync(a.BusinessId, new SettingsInput("Shop 2", "Asia/Manila", 30, 60, 30, 2))).IsSuccess);
        Assert.Equal("orange", (await settings.GetSettingsAsync(a.BusinessId))!.BrandColor);
        Assert.Equal("teal", (await settings.GetSettingsAsync(b.BusinessId))!.BrandColor);

        // The database refuses anything that is not a preset, even if the service were bypassed.
        await using var ctx = db.CreateDbContext();
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync("UPDATE business SET brand_color = 'hotpink' WHERE id = {0}", a.BusinessId));
    }

    // ------------------------------------------------------------ day view and audit

    [Fact]
    public async Task The_day_view_and_audit_log_show_only_the_signed_in_businesss_data()
    {
        var (db, _, _, days, bookings) = await StackAsync();
        var a = await NewTenantAsync(db);
        var b = await NewTenantAsync(db);
        var mine = await BookAsync(bookings, a, Manila(5, 10), "09170000001");
        await BookAsync(bookings, b, Manila(5, 10), "09170000002");

        var dayA = (await days.GetDayAsync(a.BusinessId, new DateOnly(2030, 3, 5)))!;
        Assert.Equal(new[] { mine.Id }, dayA.Bookings.Select(x => x.Id).ToArray());
        Assert.DoesNotContain(dayA.Bookings, x => x.CustomerPhone == "09170000002");

        var auditA = (await days.GetAuditAsync(a.BusinessId))!;
        Assert.Single(auditA.Rows);
        Assert.Equal(mine.Id, auditA.Rows[0].BookingId);
    }

    [Fact]
    public async Task The_day_view_offers_only_the_actions_the_policy_allows()
    {
        var clock = Clock();
        var (db, _, _, days, bookings) = await StackAsync(clock);
        var t = await NewTenantAsync(db);
        await BookAsync(bookings, t, Manila(5, 10));
        var date = new DateOnly(2030, 3, 5);

        var before = (await days.GetDayAsync(t.BusinessId, date))!.Bookings.Single();
        Assert.True(before.CanCancel);
        Assert.False(before.CanComplete); // not started
        Assert.False(before.CanNoShow);

        clock.Now = new DateTimeOffset(Manila(5, 10, 5), TimeSpan.Zero);
        var started = (await days.GetDayAsync(t.BusinessId, date))!.Bookings.Single();
        Assert.True(started.CanComplete);
        Assert.False(started.CanNoShow); // inside the 15-minute grace

        clock.Now = new DateTimeOffset(Manila(5, 10, 20), TimeSpan.Zero);
        Assert.True((await days.GetDayAsync(t.BusinessId, date))!.Bookings.Single().CanNoShow);
    }
}
