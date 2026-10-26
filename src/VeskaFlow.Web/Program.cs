using System.Net;
using System.Threading.RateLimiting;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "Connection string missing. Set the ConnectionStrings__Default environment variable (never commit it).");

// A wrong format must not echo the value: it holds the database password, and startup errors end up in host logs.
try
{
    _ = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
{
    throw new InvalidOperationException(
        "ConnectionStrings__Default is not a valid Npgsql connection string. Use the form " +
        "Host=...;Database=...;Username=...;Password=...;SSL Mode=Require, not a postgresql:// URL.");
}

// Data: one factory, plus a scoped context built from it for ordinary page/service use.
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new VeskaFlow.Web.Services.BookingLimits(builder.Configuration.GetValue("Booking:MaxActivePerCustomer", 2)));
builder.Services.AddScoped<VeskaFlow.Web.Services.BookingService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.OnboardingService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.AuthService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.ShopCatalogService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.AdminDayService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.FollowUpService>();
builder.Services.AddHttpClient<VeskaFlow.Web.Services.BrevoEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<VeskaFlow.Web.Services.ResendEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<VeskaFlow.Web.Services.IEmailSender>(sp =>
{
    // Brevo first (no domain needed), then Resend. With neither the platform owner confirms by hand.
    var brevo = sp.GetRequiredService<VeskaFlow.Web.Services.BrevoEmailSender>();
    if (brevo.IsConfigured) return brevo;
    var resend = sp.GetRequiredService<VeskaFlow.Web.Services.ResendEmailSender>();
    return resend.IsConfigured ? resend : new VeskaFlow.Web.Services.NoEmailSender();
});
builder.Services.AddScoped<VeskaFlow.Web.Services.AccountControlService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.VisitRecordService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.CustomerDataService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.CatalogAdminService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.ShopSettingsService>();

// Authentication: cookie for the web back office. Authorization is always enforced server-side.
builder.Services
    .AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "bb.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // secure whenever the request is HTTPS
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "bb.csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin");
    o.Conventions.AuthorizeFolder("/Platform"); // signed in; the page itself then checks the platform owner's email
});

// Rate limiting: per client IP. Policies are attached to endpoints with RequireRateLimiting("auth" | "booking").
var authPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 10);
var bookingPerMinute = builder.Configuration.GetValue("RateLimits:BookingPerMinute", 6);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Sign-in and registration count attempts, i.e. form posts; merely viewing the form is not limited.
    o.AddPolicy(RateLimitPolicies.Auth, ctx => ByClientIp(ctx, authPerMinute, postOnly: true));
    // Booking and cancel posts are limited too. Browsing times is not (revisit if availability queries get abused).
    o.AddPolicy(RateLimitPolicies.Booking, ctx => ByClientIp(ctx, bookingPerMinute, postOnly: true));
});

// Behind a reverse proxy (Render, Cloud Run, Cloudflare) the real client IP is in X-Forwarded-For.
// Only trusted when explicitly enabled, because anyone can forge that header when there is no proxy.
var trustProxy = builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false);
if (trustProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (trustProxy) app.UseForwardedHeaders();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
    await SqlMigrator.ApplyAsync(connectionString, app.Logger);

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

// Empty 4xx/5xx responses (unknown slug, unknown token, 429, bad request) render one friendly page per status.
// Unknown slug, unknown token and unknown URL therefore return the identical not-found page.
app.UseStatusCodePagesWithReExecute("/Status/{0}");

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// A plain "I am awake" answer that never touches the database. A free uptime monitor can call it every few minutes
// so the hosting does not put the app to sleep, without keeping the database awake.
app.MapGet("/ping", () => Results.Text("ok"));

app.MapGet("/healthz", async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        await using var db = await factory.CreateDbContextAsync(timeout.Token);
        await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
        return Results.Json(new { status = "ok", version = AppInfo.Version });
    }
    catch (Exception) when (!ct.IsCancellationRequested)
    {
        return Results.Json(new { status = "database_unavailable", version = AppInfo.Version },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

static RateLimitPartition<string> ByClientIp(HttpContext ctx, int permitsPerMinute, bool postOnly) =>
    postOnly && !HttpMethods.IsPost(ctx.Request.Method)
        ? RateLimitPartition.GetNoLimiter("not-limited")
        : RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? IPAddress.None.ToString(),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Booking = "booking";
}

public partial class Program;
