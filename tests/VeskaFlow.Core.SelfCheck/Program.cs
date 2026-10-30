using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Pricing;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Core.Security;

// Dependency-free test runner (no NuGet needed). Exit code 1 if any check fails.
// Port to xunit whenever you like; the assertions are plain and map 1:1.
var failures = 0;
var total = 0;

void Check(string name, bool condition, string? detail = null)
{
    total++;
    if (condition) { Console.WriteLine($"  ok   {name}"); return; }
    failures++;
    Console.WriteLine($"  FAIL {name} {detail}");
}

const string Manila = "Asia/Manila";
var day = new DateOnly(2026, 10, 20);                 // a Tuesday
var nine = new WorkWindow(new TimeOnly(9, 0), new TimeOnly(18, 0));
var nowFar = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);   // well before `day`

AvailabilityRequest Req(
    DateOnly? date = null, IReadOnlyList<WorkWindow>? windows = null, int duration = 30, int buffer = 0,
    int slot = 30, IReadOnlyList<TimeRange>? occupied = null, IReadOnlyList<TimeRange>? blackouts = null,
    DateTime? now = null, int lead = 60, int horizon = 60, string tz = Manila) =>
    new(tz, date ?? day, windows ?? [nine], duration, buffer, slot, occupied ?? [], blackouts ?? [],
        now ?? nowFar, lead, horizon);

DateTime ManilaUtc(int h, int m = 0) => LocalTime.ToUtc(day.ToDateTime(new TimeOnly(h, m)), LocalTime.Zone(Manila));

Console.WriteLine("Promo pricing");
{
    Check("20% off 150.00 is 120.00", PromoPricing.Net(150m, 20) == 120.00m, PromoPricing.Net(150m, 20).ToString());
    Check("15% off 99.99 rounds to the centavo: 84.99", PromoPricing.Net(99.99m, 15) == 84.99m, PromoPricing.Net(99.99m, 15).ToString());
    Check("half a centavo rounds up: 50% off 10.05 is 5.03", PromoPricing.Net(10.05m, 50) == 5.03m, PromoPricing.Net(10.05m, 50).ToString());
    Check("90% off 100 is 10.00", PromoPricing.Net(100m, 90) == 10.00m);
    Check("a free service stays free", PromoPricing.Net(0m, 20) == 0m);
    Check("a percentage outside 1-90 changes nothing", PromoPricing.Net(150m, 0) == 150m && PromoPricing.Net(150m, 91) == 150m && PromoPricing.Net(150m, -5) == 150m);
    Check("percent validity", PromoPricing.IsValidPercent(1) && PromoPricing.IsValidPercent(90) && !PromoPricing.IsValidPercent(0) && !PromoPricing.IsValidPercent(91));
    var today = new DateOnly(2030, 3, 5);
    Check("running promo gives its percent", PromoPricing.ActivePercent(true, null, 20, today) == 20);
    Check("switched off gives nothing", PromoPricing.ActivePercent(false, null, 20, today) is null);
    Check("no percent gives nothing", PromoPricing.ActivePercent(true, null, null, today) is null);
    Check("last day is inclusive", PromoPricing.ActivePercent(true, today, 20, today) == 20);
    Check("the day after the last day gives nothing", PromoPricing.ActivePercent(true, today.AddDays(-1), 20, today) is null);
    Check("a bad stored percent gives nothing", PromoPricing.ActivePercent(true, null, 95, today) is null);
    Check("a service in the promo gets the percent", PromoPricing.PercentFor(false, true, 150m, 20) == 20);
    Check("a service left out, the Other service and a free service get none", PromoPricing.PercentFor(false, false, 150m, 20) is null && PromoPricing.PercentFor(true, true, 150m, 20) is null && PromoPricing.PercentFor(false, true, 0m, 20) is null);
    Check("no running promo means no percent for anyone", PromoPricing.PercentFor(false, true, 150m, null) is null);
    Check("net is never above regular and never negative", Enumerable.Range(1, 90).All(p => PromoPricing.Net(333.33m, p) is var n && n > 0 && n < 333.33m));
}

Console.WriteLine("Availability");
{
    var s = AvailabilityCalculator.FreeStarts(Req());
    Check("09:00-18:00, 30 min service, 30 min step -> 18 slots", s.Count == 18, $"got {s.Count}");
    Check("first slot is 09:00 Manila (01:00 UTC)", s[0] == ManilaUtc(9));
    Check("last slot is 17:30 Manila", s[^1] == ManilaUtc(17, 30));
    Check("slots are sorted and unique", s.Distinct().Count() == s.Count && s.SequenceEqual(s.OrderBy(x => x)));
}
{
    var s = AvailabilityCalculator.FreeStarts(Req(duration: 60, slot: 30));
    Check("60 min service: last start is 17:00", s[^1] == ManilaUtc(17));
}
{
    var booked = new TimeRange(ManilaUtc(10), ManilaUtc(10, 30));
    var s = AvailabilityCalculator.FreeStarts(Req(occupied: [booked]));
    Check("booked 10:00 slot is not offered", !s.Contains(ManilaUtc(10)));
    Check("09:30 and 10:30 remain free (no buffer)", s.Contains(ManilaUtc(9, 30)) && s.Contains(ManilaUtc(10, 30)));
    Check("one slot removed -> 17 remain", s.Count == 17, $"got {s.Count}");
}
{
    // Existing booking occupies 10:00-10:30 plus a 15 min buffer => occupied until 10:45.
    var booked = new TimeRange(ManilaUtc(10), ManilaUtc(10, 45));
    var s = AvailabilityCalculator.FreeStarts(Req(occupied: [booked]));
    Check("buffer after existing booking blocks 10:30", !s.Contains(ManilaUtc(10, 30)));
    Check("11:00 free again", s.Contains(ManilaUtc(11)));
}
{
    // New service with 15 min buffer: a start at 09:30 occupies until 10:15 and collides with the 10:00 booking.
    var booked = new TimeRange(ManilaUtc(10), ManilaUtc(10, 30));
    var s = AvailabilityCalculator.FreeStarts(Req(buffer: 15, occupied: [booked]));
    Check("new booking's own buffer blocks the slot before an existing booking", !s.Contains(ManilaUtc(9, 30)));
    Check("09:00 still fine (ends 09:30 + buffer 09:45)", s.Contains(ManilaUtc(9)));
}
{
    var lunch = new TimeRange(ManilaUtc(12), ManilaUtc(13));
    var s = AvailabilityCalculator.FreeStarts(Req(blackouts: [lunch]));
    Check("blackout 12:00-13:00 removes 12:00 and 12:30", !s.Contains(ManilaUtc(12)) && !s.Contains(ManilaUtc(12, 30)));
    Check("11:30 service ends exactly at blackout start -> allowed", s.Contains(ManilaUtc(11, 30)));
    Check("13:00 allowed", s.Contains(ManilaUtc(13)));
}
{
    var closed = AvailabilityCalculator.FreeStarts(Req(windows: []));
    Check("no working windows -> no slots", closed.Count == 0);
    var split = AvailabilityCalculator.FreeStarts(Req(windows:
        [new(new TimeOnly(9, 0), new TimeOnly(12, 0)), new(new TimeOnly(13, 0), new TimeOnly(18, 0))]));
    Check("split shift (lunch break) -> 6 + 10 slots", split.Count == 16, $"got {split.Count}");
}
{
    // now = 2026-10-20 09:00 Manila; lead 60 => earliest 10:00
    var now = ManilaUtc(9);
    var s = AvailabilityCalculator.FreeStarts(Req(now: now, lead: 60));
    Check("lead time hides slots inside the notice window", s[0] == ManilaUtc(10), $"first={s[0]:o}");
    var none = AvailabilityCalculator.FreeStarts(Req(now: ManilaUtc(17, 45), lead: 60));
    Check("late in the day with lead time -> nothing left", none.Count == 0);
}
{
    var s = AvailabilityCalculator.FreeStarts(Req(now: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), horizon: 30));
    Check("beyond booking horizon -> no slots", s.Count == 0);
}
{
    // Bar-style window crossing midnight: 18:00 -> 02:00
    var s = AvailabilityCalculator.FreeStarts(Req(windows: [new(new TimeOnly(18, 0), new TimeOnly(2, 0))]));
    Check("window crossing midnight: 16 half-hour slots", s.Count == 16, $"got {s.Count}");
    Check("last start is 01:30 next day", s[^1] == ManilaUtc(1, 30).AddDays(1));
}
{
    // DST: New York spring forward 2026-03-08 (23h day). 01:00-04:00 window loses the 02:00 hour.
    var ny = new DateOnly(2026, 3, 8);
    var win = new WorkWindow(new TimeOnly(1, 0), new TimeOnly(4, 0));
    var s = AvailabilityCalculator.FreeStarts(new("America/New_York", ny, [win], 60, 0, 60, [], [],
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0, 365));
    var tz = LocalTime.Zone("America/New_York");
    var locals = s.Select(u => TimeZoneInfo.ConvertTimeFromUtc(u, tz).ToString("HH:mm")).ToList();
    Check("spring-forward day has no duplicate or phantom 02:00 slot",
        s.Distinct().Count() == s.Count && !locals.Contains("02:00"), string.Join(",", locals));
    Check("spring-forward: 01:00 and 03:00 exist", locals.Contains("01:00") && locals.Contains("03:00"), string.Join(",", locals));
}
{
    // DST: fall back 2026-11-01 (25h day). Window 00:00-04:00 gives 5 hourly starts in real elapsed time? 00:00 EDT..03:00 EST
    var ny = new DateOnly(2026, 11, 1);
    var win = new WorkWindow(new TimeOnly(0, 0), new TimeOnly(4, 0));
    var s = AvailabilityCalculator.FreeStarts(new("America/New_York", ny, [win], 60, 0, 60, [], [],
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0, 365));
    Check("fall-back day yields 5 hourly starts (the repeated hour is real time)", s.Count == 5, $"got {s.Count}");
    Check("fall-back slots are unique instants", s.Distinct().Count() == s.Count);
}
{
    var (a, b) = LocalTime.DayRangeUtc(new DateOnly(2026, 3, 8), "America/New_York");
    Check("DST spring day is 23 hours", (b - a) == TimeSpan.FromHours(23), $"{b - a}");
    var (c, d) = LocalTime.DayRangeUtc(new DateOnly(2026, 11, 1), "America/New_York");
    Check("DST fall day is 25 hours", (d - c) == TimeSpan.FromHours(25), $"{d - c}");
    var (e, f) = LocalTime.DayRangeUtc(day, Manila);
    Check("Manila day is 24h and starts at 16:00 UTC the previous day",
        f - e == TimeSpan.FromHours(24) && e == new DateTime(2026, 10, 19, 16, 0, 0, DateTimeKind.Utc));
}
{
    var s = AvailabilityCalculator.FreeStarts(Req());
    Check("IsStartFree true for offered slot", AvailabilityCalculator.IsStartFree(Req(), s[3]));
    Check("IsStartFree false for off-grid time", !AvailabilityCalculator.IsStartFree(Req(), s[3].AddMinutes(7)));
    Check("IsStartFree false outside hours", !AvailabilityCalculator.IsStartFree(Req(), ManilaUtc(6)));
}

Console.WriteLine("State machine");
{
    Check("CONFIRMED -> COMPLETED", BookingStateMachine.CanTransition(BookingStatus.Confirmed, BookingStatus.Completed));
    Check("CONFIRMED -> CANCELLED", BookingStateMachine.CanTransition(BookingStatus.Confirmed, BookingStatus.Cancelled));
    Check("CONFIRMED -> NO_SHOW", BookingStateMachine.CanTransition(BookingStatus.Confirmed, BookingStatus.NoShow));
    Check("COMPLETED -> PENDING is illegal", !BookingStateMachine.CanTransition(BookingStatus.Completed, BookingStatus.Pending));
    Check("CANCELLED is terminal", Enum.GetValues<BookingStatus>().All(t => !BookingStateMachine.CanTransition(BookingStatus.Cancelled, t)));
    Check("PENDING cannot be COMPLETED directly", !BookingStateMachine.CanTransition(BookingStatus.Pending, BookingStatus.Completed));
    var threw = false;
    try { BookingStateMachine.EnsureCanTransition(BookingStatus.NoShow, BookingStatus.Confirmed); }
    catch (InvalidBookingTransitionException) { threw = true; }
    Check("EnsureCanTransition throws on illegal move", threw);
    Check("only HELD/PENDING/CONFIRMED occupy a slot",
        Enum.GetValues<BookingStatus>().Where(s => s.OccupiesSlot()).Count() == 3);
    Check("DB string round-trip for every status",
        Enum.GetValues<BookingStatus>().All(s => BookingStatusExtensions.FromDb(s.ToDb()) == s));
}

Console.WriteLine("Policy");
{
    var start = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc);
    Check("customer cancels 5h before, cutoff 2h -> allowed",
        BookingPolicy.CanCancel(BookingStatus.Confirmed, start, start.AddHours(-5), ActorKind.Customer, 2).Allowed);
    Check("customer cancels exactly at cutoff -> allowed",
        BookingPolicy.CanCancel(BookingStatus.Confirmed, start, start.AddHours(-2), ActorKind.Customer, 2).Allowed);
    Check("customer cancels 1h before, cutoff 2h -> rejected",
        !BookingPolicy.CanCancel(BookingStatus.Confirmed, start, start.AddHours(-1), ActorKind.Customer, 2).Allowed);
    Check("staff can cancel after cutoff",
        BookingPolicy.CanCancel(BookingStatus.Confirmed, start, start.AddHours(-1), ActorKind.Staff, 2).Allowed);
    Check("nobody cancels a COMPLETED booking",
        !BookingPolicy.CanCancel(BookingStatus.Completed, start, start.AddHours(-9), ActorKind.Staff, 2).Allowed);
    Check("cannot complete before start",
        !BookingPolicy.CanMarkCompleted(BookingStatus.Confirmed, start, start.AddMinutes(-1)).Allowed);
    Check("can complete after start",
        BookingPolicy.CanMarkCompleted(BookingStatus.Confirmed, start, start.AddMinutes(10)).Allowed);
    Check("no-show needs the grace period",
        !BookingPolicy.CanMarkNoShow(BookingStatus.Confirmed, start, start.AddMinutes(10)).Allowed
        && BookingPolicy.CanMarkNoShow(BookingStatus.Confirmed, start, start.AddMinutes(15)).Allowed);
    // With the business time zone, completed and no-show are for the day of the appointment only (Manila is UTC+8).
    Check("complete and no-show work on the day of the appointment",
        BookingPolicy.CanMarkCompleted(BookingStatus.Confirmed, start, start.AddMinutes(10), "Asia/Manila").Allowed
        && BookingPolicy.CanMarkNoShow(BookingStatus.Confirmed, start, start.AddMinutes(20), "Asia/Manila").Allowed);
    Check("cannot complete or mark no-show the next day",
        !BookingPolicy.CanMarkCompleted(BookingStatus.Confirmed, start, start.AddDays(1), "Asia/Manila").Allowed
        && !BookingPolicy.CanMarkNoShow(BookingStatus.Confirmed, start, start.AddDays(1), "Asia/Manila").Allowed);
    Check("the day is the local day, not the UTC day",
        // 23:30 UTC is already the next day in Manila, so an appointment at 15:00 UTC (23:00 Manila) is still "today" at 15:30 UTC.
        BookingPolicy.CanMarkCompleted(BookingStatus.Confirmed, new DateTime(2030, 3, 4, 15, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 3, 4, 15, 30, 0, DateTimeKind.Utc), "Asia/Manila").Allowed
        && !BookingPolicy.CanMarkCompleted(BookingStatus.Confirmed, new DateTime(2030, 3, 4, 15, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 3, 4, 16, 30, 0, DateTimeKind.Utc), "Asia/Manila").Allowed);
}

Console.WriteLine("Security & slugs");
{
    var h = PasswordHasher.Hash("correct horse", iterations: 1000);
    Check("password verifies", PasswordHasher.Verify("correct horse", h));
    Check("wrong password rejected", !PasswordHasher.Verify("wrong", h));
    Check("hash is salted (two hashes differ)", PasswordHasher.Hash("x", 1000) != PasswordHasher.Hash("x", 1000));
    Check("garbage hash rejected, no exception", !PasswordHasher.Verify("x", "not-a-hash") && !PasswordHasher.Verify("x", "v1.1.@@.@@"));
    Check("slug from name", Slug.From("Maria's Salon & Barber!") == "marias-salon-barber", Slug.From("Maria's Salon & Barber!"));

    // phone numbers: one canonical form per person
    Check("phone: local format kept", PhoneNumber.Normalize("0917 123 4567") == "09171234567", PhoneNumber.Normalize("0917 123 4567"));
    Check("phone: +63 becomes local", PhoneNumber.Normalize("+63 917-123-4567") == "09171234567", PhoneNumber.Normalize("+63 917-123-4567"));
    Check("phone: 63 prefix becomes local", PhoneNumber.Normalize("639171234567") == "09171234567", PhoneNumber.Normalize("639171234567"));
    Check("phone: 10 digits starting 9 becomes local", PhoneNumber.Normalize("(917) 123.4567") == "09171234567", PhoneNumber.Normalize("(917) 123.4567"));
    Check("phone: foreign number unchanged", PhoneNumber.Normalize("+1 415 555 0172") == "+14155550172", PhoneNumber.Normalize("+1 415 555 0172"));
    Check("phone: landline unchanged", PhoneNumber.Normalize("02 8123 4567") == "0281234567", PhoneNumber.Normalize("02 8123 4567"));
    Check("phone: empty stays empty", PhoneNumber.Normalize("  ") == "", PhoneNumber.Normalize("  "));
    Check("slug from typed address", Slug.From("  San Juan 123  ") == "san-juan-123", Slug.From("  San Juan 123  "));
    Check("slug from symbols only is empty", Slug.From("!! ??") == "");
    Check("slug is cut at 40 and never ends in a hyphen", Slug.From(new string('a', 39) + " bbb").Length <= 40 && !Slug.From(new string('a', 39) + " bbb").EndsWith('-'));
    Check("slug validity", Slug.IsValid("salon-maria") && !Slug.IsValid("-bad") && !Slug.IsValid("ab") && !Slug.IsValid("Has Space"));
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"All {total} checks passed." : $"{failures} of {total} checks FAILED.");
return failures == 0 ? 0 : 1;
