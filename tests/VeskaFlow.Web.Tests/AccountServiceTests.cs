using System.Diagnostics;
using System.Security.Claims;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Tests;

public class AccountServiceTests
{
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<(OnboardingService Onboarding, AuthService Auth, TestDbFactory Factory)> CreateAsync()
    {
        var factory = new TestDbFactory(await TestDb.ConnectionStringAsync());
        return (new OnboardingService(factory), new AuthService(factory), factory);
    }

    private static RegisterRequest NewRequest(string? slug = null, string? email = null, string password = "correct-horse-1", string shopName = "Ben's Barbershop", string? ownerName = null) =>
        new(shopName, slug ?? Unique("shop"), ownerName ?? "Ben Santos", email ?? $"{Unique("ben")}@example.com", password);

    /// <summary>The short code added to an address that is already used: four letters or numbers, none that look alike.</summary>
    private const string Code = "[a-hj-km-np-z2-9]{4}";

    [Fact]
    public async Task Register_creates_business_owner_staff_service_and_hours_in_one_go()
    {
        var (onboarding, _, factory) = await CreateAsync();
        var result = await onboarding.RegisterAsync(NewRequest());

        Assert.True(result.IsSuccess);
        var owner = result.Value!;
        Assert.Equal("OWNER", owner.Role);

        await using var db = factory.CreateDbContext();
        var business = await db.Businesses.SingleAsync(b => b.Id == owner.BusinessId);
        Assert.Equal("FREE", business.Plan);
        Assert.Equal("Asia/Manila", business.TimeZone);

        var staff = await db.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).ToListAsync();
        Assert.Single(staff); // exactly the FREE plan limit
        Assert.True(staff.Count <= PlanLimits.FreeMaxStaff);

        // One starter service, plus "Other" for visits whose service is not in the list. Both can be booked with the owner.
        var services = await db.Services.Where(s => s.BusinessId == owner.BusinessId).OrderBy(s => s.SortOrder).ToListAsync();
        Assert.Equal(2, services.Count);
        Assert.False(services[0].IsOther);
        Assert.True(services[1].IsOther);
        Assert.Equal(Text.OtherServiceName, services[1].Name);
        Assert.All(services, service => Assert.Equal(1, db.StaffServices.Count(x => x.StaffId == staff[0].Id && x.ServiceId == service.Id && x.BusinessId == owner.BusinessId)));

        var hours = await db.WorkingHours.Where(h => h.StaffId == staff[0].Id).OrderBy(h => h.DayOfWeek).ToListAsync();
        Assert.Equal(new short[] { 1, 2, 3, 4, 5, 6 }, hours.Select(h => h.DayOfWeek)); // Mon-Sat, closed Sunday
        Assert.All(hours, h => { Assert.Equal(new TimeOnly(9, 0), h.OpenTime); Assert.Equal(new TimeOnly(18, 0), h.CloseTime); });
    }

    [Fact]
    public async Task Password_is_stored_hashed_never_in_plain_text()
    {
        var (onboarding, _, factory) = await CreateAsync();
        var owner = (await onboarding.RegisterAsync(NewRequest(password: "s3cret-passw0rd"))).Value!;

        await using var db = factory.CreateDbContext();
        var hash = (await db.AdminUsers.SingleAsync(u => u.Id == owner.AdminUserId)).PasswordHash;
        Assert.DoesNotContain("s3cret-passw0rd", hash);
        Assert.StartsWith("v1.600000.", hash);
    }

    [Fact]
    public async Task Only_the_email_must_be_unique_the_shop_name_and_the_owner_name_may_be_shared()
    {
        var (onboarding, _, _) = await CreateAsync();
        var first = NewRequest();
        Assert.True((await onboarding.RegisterAsync(first)).IsSuccess);

        // The same shop name and the same owner name, a different email: fine.
        var twin = await onboarding.RegisterAsync(NewRequest(slug: first.Slug, shopName: first.BusinessName!, ownerName: first.OwnerName));
        Assert.True(twin.IsSuccess, twin.Message);

        // The same email, in any case, is a friendly field error and not an exception.
        var sameEmailOtherCase = await onboarding.RegisterAsync(NewRequest(email: first.Email!.ToUpperInvariant()));
        Assert.Equal(FailureKind.Validation, sameEmailOtherCase.Failure);
        Assert.Equal(Text.EmailTaken, sameEmailOtherCase.FieldErrors["email"]);
        Assert.DoesNotContain("slug", sameEmailOtherCase.FieldErrors.Keys);
    }

    [Fact]
    public async Task The_booking_address_comes_from_the_shop_name_and_gets_a_short_code_when_it_is_taken()
    {
        var (onboarding, _, _) = await CreateAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        string Name() => $"Kuya Jun Salon {tag}";

        // Blank address: made from the shop name.
        var first = await onboarding.RegisterAsync(NewRequest(slug: "  ", shopName: Name()));
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal($"kuya-jun-salon-{tag}", first.Value!.Slug);

        // The same shop name again: the same address with a short code at the end, then another code for the next one.
        var second = await onboarding.RegisterAsync(NewRequest(slug: "", shopName: Name()));
        var third = await onboarding.RegisterAsync(NewRequest(slug: null, shopName: Name()) with { Slug = null });
        Assert.True(second.IsSuccess, "second: " + second.Message);
        Assert.True(third.IsSuccess, "third: " + third.Message);
        Assert.Matches($"^kuya-jun-salon-{tag}-{Code}$", second.Value!.Slug);
        Assert.Matches($"^kuya-jun-salon-{tag}-{Code}$", third.Value!.Slug);
        Assert.NotEqual(second.Value.Slug, third.Value.Slug);

        // An address that was typed and is taken gets the code too: no error.
        var typed = await onboarding.RegisterAsync(NewRequest(slug: $"Kuya Jun Salon {tag}", shopName: "Another name"));
        Assert.True(typed.IsSuccess, typed.Message);
        Assert.Matches($"^kuya-jun-salon-{tag}-{Code}$", typed.Value!.Slug);

        // A name too short to be an address, and one with no letters at all, still get a valid address.
        var tiny = await onboarding.RegisterAsync(NewRequest(slug: "", shopName: "Ab"));
        Assert.Matches($"^ab-{Code}$", tiny.Value!.Slug);
        var symbols = await onboarding.RegisterAsync(NewRequest(slug: "", shopName: "!!! ???"));
        Assert.Matches($"^shop-{Code}$", symbols.Value!.Slug);

        // A very long name is cut, and the code still fits inside the 40 characters.
        var longName = new string('x', 70) + tag; // 76 characters: the longest a shop name may be is 80
        var longFirst = await onboarding.RegisterAsync(NewRequest(slug: "", shopName: longName));
        var longSecond = await onboarding.RegisterAsync(NewRequest(slug: "", shopName: longName));
        Assert.True(longFirst.IsSuccess, "first: " + longFirst.Message + " " + string.Join(",", longFirst.FieldErrors.Values));
        Assert.True(longSecond.IsSuccess, "second: " + longSecond.Message + " " + string.Join(",", longSecond.FieldErrors.Values));
        Assert.True(longFirst.Value!.Slug.Length <= 40);
        Assert.True(longSecond.Value!.Slug.Length <= 40);
        Assert.Matches("^x+-[a-hj-km-np-z2-9]{4,6}$", longSecond.Value.Slug);
        Assert.NotEqual(longFirst.Value.Slug, longSecond.Value.Slug);

        // Many shops with the very same long name: the code grows when needed and the address never passes 40 characters.
        var many = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => onboarding.RegisterAsync(NewRequest(slug: "", shopName: longName)))));
        Assert.All(many, r => { Assert.True(r.IsSuccess, r.Message); Assert.True(r.Value!.Slug.Length <= 40, r.Value.Slug); });
        Assert.Equal(12, many.Select(r => r.Value!.Slug).Distinct().Count());
        Assert.All(new[] { first, second, third, typed, tiny, symbols, longFirst, longSecond }, r => Assert.True(VeskaFlow.Core.Domain.Slug.IsValid(r.Value!.Slug), r.Value!.Slug));
    }

    [Fact]
    public async Task The_booking_address_is_accepted_however_it_is_typed_and_is_never_refused()
    {
        var (onboarding, _, _) = await CreateAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];

        // Capitals, spaces and symbols are turned into a clean address instead of being refused.
        var typed = await onboarding.RegisterAsync(NewRequest(slug: $"  San Juan {tag}  "));
        Assert.True(typed.IsSuccess, typed.Message);
        Assert.Equal($"san-juan-{tag}", typed.Value!.Slug);

        var symbols = await onboarding.RegisterAsync(NewRequest(slug: $"Maria's Salon & Spa!! {tag}"));
        Assert.True(symbols.IsSuccess, symbols.Message);
        Assert.Equal($"marias-salon-spa-{tag}", symbols.Value!.Slug);

        // Taken is judged on the cleaned address, so "SAN JUAN x" and "san-juan-x" are the same address: the second gets a code.
        var same = await onboarding.RegisterAsync(NewRequest(slug: $"SAN JUAN {tag}"));
        Assert.True(same.IsSuccess, same.Message);
        Assert.Matches($"^san-juan-{tag}-{Code}$", same.Value!.Slug);

        // Too short to be an address: the shop name is used instead, never an error. Blank uses the shop name too.
        var tooShort = await onboarding.RegisterAsync(NewRequest(slug: "a !"));
        Assert.True(tooShort.IsSuccess, tooShort.Message);
        Assert.StartsWith("bens-barbershop", tooShort.Value!.Slug);
        var blank = await onboarding.RegisterAsync(NewRequest(slug: "   "));
        Assert.True(blank.IsSuccess, blank.Message);
        Assert.StartsWith("bens-barbershop", blank.Value!.Slug);

        // Long input is cut to 40 characters rather than refused.
        var longOne = await onboarding.RegisterAsync(NewRequest(slug: new string('x', 80) + tag));
        Assert.True(longOne.IsSuccess, longOne.Message);
        Assert.True(longOne.Value!.Slug.Length <= 40);
    }

    [Theory]
    [InlineData("san-juan-salon", "k7x2", "san-juan-salon-k7x2")]
    [InlineData("", "k7x2", "shop-k7x2")]
    [InlineData("ab", "k7x2", "ab-k7x2")]
    public void An_address_with_a_code_is_the_address_a_hyphen_and_the_code(string wanted, string code, string expected) =>
        Assert.Equal(expected, OnboardingService.WithCode(wanted, code));

    [Fact]
    public void An_address_with_a_code_always_fits_in_40_characters_never_ends_a_cut_in_a_hyphen_and_is_a_valid_address()
    {
        foreach (var code in new[] { "k7x2", "k7x2m", "k7x2mp" })
        {
            foreach (var wanted in new[] { new string('x', 40), new string('x', 39) + "-", "abc-" + new string('y', 33) + "-zzz", "a-b-c-d-e-f-g-h-i-j-k-l-m-n-o-p-q-r-s-t" })
            {
                var result = OnboardingService.WithCode(VeskaFlow.Core.Domain.Slug.From(wanted), code);
                Assert.True(result.Length <= 40, $"{result} ({result.Length})");
                Assert.EndsWith("-" + code, result);
                Assert.DoesNotContain("--", result);
                Assert.True(VeskaFlow.Core.Domain.Slug.IsValid(result), result);
            }
        }
    }

    [Fact]
    public async Task Parallel_registrations_for_one_address_all_succeed_with_different_addresses()
    {
        var (onboarding, _, factory) = await CreateAsync();
        var slug = Unique("race");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => onboarding.RegisterAsync(NewRequest(slug: slug)))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));                                                       // nobody is refused
        Assert.Equal(8, results.Select(r => r.Value!.Slug).Distinct().Count());                                              // and no two share an address
        Assert.Contains(slug, results.Select(r => r.Value!.Slug));                                                           // one of them has the plain address
        Assert.All(results.Where(r => r.Value!.Slug != slug), r => Assert.Matches($"^{slug}-{Code}$", r.Value!.Slug));
        await using var db = factory.CreateDbContext();
        Assert.Equal(8, await db.Businesses.CountAsync(b => b.Slug == slug || b.Slug.StartsWith(slug + "-")));
    }

    [Fact]
    public async Task Parallel_registrations_for_one_email_produce_one_success_and_friendly_errors()
    {
        var (onboarding, _, factory) = await CreateAsync();
        var email = $"{Unique("same")}@example.com";

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => onboarding.RegisterAsync(NewRequest(email: email, shopName: "Shared Name")))));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(Text.EmailTaken, r.FieldErrors["email"]));
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.AdminUsers.CountAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Invalid_registration_input_returns_field_errors()
    {
        var (onboarding, _, factory) = await CreateAsync();
        var result = await onboarding.RegisterAsync(new RegisterRequest("", "A!", "", "nope", "short"));

        Assert.Equal(FailureKind.Validation, result.Failure);
        foreach (var field in new[] { "businessName", "ownerName", "email", "password" })
            Assert.Contains(field, result.FieldErrors.Keys);
        Assert.DoesNotContain("slug", result.FieldErrors.Keys);                                                              // the address is never the problem
        // Other tests create shops in parallel, so check only for rows this request could have created.
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Businesses.AnyAsync(b => b.Slug == "a" || b.Name == ""));
        Assert.False(await db.AdminUsers.AnyAsync(u => u.Email == "nope"));
    }

    [Fact]
    public async Task Slug_is_derived_from_the_shop_name_when_left_blank()
    {
        var (onboarding, _, _) = await CreateAsync();
        var name = "Maria's Salon & Barber " + Unique("x");
        var result = await onboarding.RegisterAsync(new RegisterRequest(name, "", "Maria", $"{Unique("m")}@example.com", "longenough1"));

        Assert.True(result.IsSuccess);
        Assert.StartsWith("marias-salon-barber-x", result.Value!.Slug);
    }

    [Fact]
    public async Task Sign_in_gives_business_role_and_name_claims()
    {
        var (onboarding, auth, _) = await CreateAsync();
        var request = NewRequest();
        var owner = (await onboarding.RegisterAsync(request)).Value!;

        var signedIn = await auth.SignInAsync(request.Email!.ToUpperInvariant(), request.Password); // email is case-insensitive
        Assert.True(signedIn.IsSuccess);

        var principal = AuthService.CreatePrincipal(signedIn.Value!);
        Assert.Equal(owner.BusinessId, principal.BusinessId());
        Assert.Equal(owner.AdminUserId, principal.UserId());
        Assert.Equal("OWNER", principal.Role());
        Assert.Equal("Ben Santos", principal.DisplayName());
        Assert.True(principal.IsInRole("OWNER"));
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(owner.BusinessId.ToString(), principal.FindFirstValue(UserClaims.BusinessIdType));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_give_the_same_message()
    {
        var (onboarding, auth, _) = await CreateAsync();
        var request = NewRequest();
        await onboarding.RegisterAsync(request);

        var wrong = await auth.SignInAsync(request.Email, "not-the-password");
        var unknown = await auth.SignInAsync($"{Unique("nobody")}@example.com", "not-the-password");
        var empty = await auth.SignInAsync(request.Email, "");

        Assert.False(wrong.IsSuccess);
        Assert.False(unknown.IsSuccess);
        Assert.False(empty.IsSuccess);
        Assert.Equal(Text.LoginFailed, wrong.Message);
        Assert.Equal(wrong.Message, unknown.Message);
        Assert.Equal(wrong.Message, empty.Message);
        Assert.Equal(wrong.Failure, unknown.Failure);
    }

    [Fact]
    public async Task Unknown_email_takes_about_as_long_as_a_wrong_password()
    {
        var (onboarding, auth, _) = await CreateAsync();
        var request = NewRequest();
        await onboarding.RegisterAsync(request);
        await auth.SignInAsync("warmup@example.com", "x"); // pay the one-time dummy-hash cost first

        static async Task<double> MedianMsAsync(Func<Task> action)
        {
            var samples = new List<double>();
            for (var i = 0; i < 7; i++)
            {
                var sw = Stopwatch.StartNew();
                await action();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return samples[samples.Count / 2];
        }

        var wrong = await MedianMsAsync(() => auth.SignInAsync(request.Email, "not-the-password"));
        var unknown = await MedianMsAsync(() => auth.SignInAsync("nobody@example.com", "not-the-password"));

        // An unknown email must not short-circuit: it should cost the same PBKDF2 work (allow a generous 2x either way).
        Assert.InRange(unknown / wrong, 0.5, 2.0);
    }

    [Fact]
    public async Task A_deactivated_user_cannot_sign_in()
    {
        var (onboarding, auth, factory) = await CreateAsync();
        var request = NewRequest();
        var owner = (await onboarding.RegisterAsync(request)).Value!;
        await using (var db = factory.CreateDbContext())
            await db.AdminUsers.Where(u => u.Id == owner.AdminUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        var result = await auth.SignInAsync(request.Email, request.Password);

        Assert.False(result.IsSuccess);
        Assert.Equal(Text.LoginFailed, result.Message);
    }

    [Fact]
    public void Free_plan_allows_one_staff_member()
    {
        Assert.Equal(1, PlanLimits.MaxStaff("FREE"));
        Assert.Null(PlanLimits.MaxStaff("PRO"));
    }
}
