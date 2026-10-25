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

    private static RegisterRequest NewRequest(string? slug = null, string? email = null, string password = "correct-horse-1") =>
        new("Ben's Barbershop", slug ?? Unique("shop"), "Ben Santos", email ?? $"{Unique("ben")}@example.com", password);

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

        var service = await db.Services.SingleAsync(s => s.BusinessId == owner.BusinessId);
        Assert.Equal(1, await db.StaffServices.CountAsync(x => x.StaffId == staff[0].Id && x.ServiceId == service.Id && x.BusinessId == owner.BusinessId));

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
    public async Task A_taken_slug_or_email_is_a_friendly_field_error_not_an_exception()
    {
        var (onboarding, _, _) = await CreateAsync();
        var first = NewRequest();
        Assert.True((await onboarding.RegisterAsync(first)).IsSuccess);

        var sameSlug = await onboarding.RegisterAsync(NewRequest(slug: first.Slug));
        Assert.Equal(FailureKind.Validation, sameSlug.Failure);
        Assert.Equal(Text.SlugTaken, sameSlug.FieldErrors["slug"]);

        var sameEmailOtherCase = await onboarding.RegisterAsync(NewRequest(email: first.Email!.ToUpperInvariant()));
        Assert.Equal(FailureKind.Validation, sameEmailOtherCase.Failure);
        Assert.Equal(Text.EmailTaken, sameEmailOtherCase.FieldErrors["email"]);
    }

    [Fact]
    public async Task Parallel_registrations_for_one_slug_produce_one_success_and_friendly_errors()
    {
        var (onboarding, _, _) = await CreateAsync();
        var slug = Unique("race");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => onboarding.RegisterAsync(NewRequest(slug: slug)))));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Contains("slug", r.FieldErrors.Keys));
    }

    [Fact]
    public async Task Invalid_registration_input_returns_field_errors()
    {
        var (onboarding, _, factory) = await CreateAsync();
        var result = await onboarding.RegisterAsync(new RegisterRequest("", "A_B", "", "nope", "short"));

        Assert.Equal(FailureKind.Validation, result.Failure);
        foreach (var field in new[] { "businessName", "slug", "ownerName", "email", "password" })
            Assert.Contains(field, result.FieldErrors.Keys);
        // Other tests create shops in parallel, so check only for rows this request could have created.
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Businesses.AnyAsync(b => b.Slug == "a_b" || b.Name == ""));
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
