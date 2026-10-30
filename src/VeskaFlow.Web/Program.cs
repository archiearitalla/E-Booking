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
builder.Services.AddHttpClient<VeskaFlow.Web.Services.ICaptcha, VeskaFlow.Web.Services.TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));
// The email service that is set up: Brevo first (no domain needed), then Resend. With neither the platform owner confirms by hand.
// Emails to shop owners go through CountedEmailSender (counted against the daily limit); customer emails use this "raw" one and
// count themselves per shop (see CustomerMailer).
builder.Services.AddKeyedScoped<VeskaFlow.Web.Services.IEmailSender>("raw", (sp, _) =>
{
    var brevo = sp.GetRequiredService<VeskaFlow.Web.Services.BrevoEmailSender>();
    if (brevo.IsConfigured) return brevo;
    var resend = sp.GetRequiredService<VeskaFlow.Web.Services.ResendEmailSender>();
    return resend.IsConfigured ? resend : new VeskaFlow.Web.Services.NoEmailSender();
});
builder.Services.AddScoped<VeskaFlow.Web.Services.EmailQuota>();
builder.Services.AddScoped<VeskaFlow.Web.Services.IEmailSender>(sp =>
{
    var raw = sp.GetRequiredKeyedService<VeskaFlow.Web.Services.IEmailSender>("raw");
    return raw.IsConfigured ? new VeskaFlow.Web.Services.CountedEmailSender(raw, sp.GetRequiredService<VeskaFlow.Web.Services.EmailQuota>()) : raw;
});
builder.Services.AddScoped<VeskaFlow.Web.Services.CustomerMailer>();
builder.Services.AddScoped<VeskaFlow.Web.Services.AutoReminderService>();
builder.Services.AddHostedService<VeskaFlow.Web.Services.ReminderEmailWorker>();
builder.Services.AddScoped<VeskaFlow.Web.Services.AccountControlService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.VisitRecordService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.CustomerDataService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.ReminderService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.CustomerListService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.ReportService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.TeamService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.LogoService>();
builder.Services.AddSingleton<VeskaFlow.Web.Services.BillingInfo>();
builder.Services.AddScoped<VeskaFlow.Web.Services.PlatformImageService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.ExpiryNoticeService>();
builder.Services.AddHostedService<VeskaFlow.Web.Services.ExpiryNoticeWorker>();
builder.Services.AddScoped<VeskaFlow.Web.Services.CatalogAdminService>();
builder.Services.AddScoped<VeskaFlow.Web.Services.ShopSettingsService>();
builder.Services.AddSingleton<VeskaFlow.Web.Services.IAiModel, VeskaFlow.Web.Services.AnthropicAiModel>();
builder.Services.AddScoped<VeskaFlow.Web.Services.AiAccess>();
builder.Services.AddScoped<VeskaFlow.Web.Services.AiReceptionistService>();

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
var chatPerMinute = builder.Configuration.GetValue("RateLimits:ChatPerMinute", 12);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Sign-in and registration count attempts, i.e. form posts; merely viewing the form is not limited.
    o.AddPolicy(RateLimitPolicies.Auth, ctx => ByClientIp(ctx, authPerMinute, postOnly: true));
    // Booking and cancel posts are limited too. Browsing times is not (revisit if availability queries get abused).
    o.AddPolicy(RateLimitPolicies.Booking, ctx => ByClientIp(ctx, bookingPerMinute, postOnly: true));
    // Each chat message can cost money, so messages are limited per client IP as well as per shop and month.
    o.AddPolicy(RateLimitPolicies.Chat, ctx => ByClientIp(ctx, chatPerMinute, postOnly: true));
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

// With Turnstile set up, its script and frame are the only outside sources the pages may load.
var turnstileOn = !string.IsNullOrWhiteSpace(app.Configuration["Turnstile:SiteKey"]) && !string.IsNullOrWhiteSpace(app.Configuration["Turnstile:SecretKey"]);
var contentSecurityPolicy =
    "default-src 'self'; script-src 'self'" + (turnstileOn ? " https://challenges.cloudflare.com" : "") + "; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
    (turnstileOn ? "frame-src https://challenges.cloudflare.com; connect-src 'self' https://challenges.cloudflare.com; " : "") +
    "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] = contentSecurityPolicy;
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
app.MapMethods("/ping", ["GET", "HEAD"], () => Results.Text("ok")); // uptime monitors often use HEAD

// The shop's logo for the booking page. The browser keeps it and asks again each time (ETag), so a new logo shows at once.
// The GCash QR code clients scan to pay. Only for signed-in people (shop owners on About and on the trial-ended page).
app.MapGet("/pay-qr", async (HttpContext http, VeskaFlow.Web.Services.PlatformImageService images, CancellationToken ct) =>
{
    var image = await images.GetAsync(VeskaFlow.Web.Services.PlatformImageService.GcashQr, ct);
    if (image is null) return Results.NotFound();
    var etag = $"\"{image.Version:x}\"";
    http.Response.Headers.CacheControl = "private, no-cache";
    http.Response.Headers.ETag = etag;
    if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    if (http.Request.Query.ContainsKey("download"))
    {
        var extension = image.ContentType switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };
        return Results.Bytes(image.Data, image.ContentType, $"gcash-qr.{extension}");
    }
    return Results.Bytes(image.Data, image.ContentType);
}).RequireAuthorization();

app.MapGet("/logo/{slug}", async (string slug, HttpContext http, VeskaFlow.Web.Services.LogoService logos, CancellationToken ct) =>
{
    var logo = await logos.GetBySlugAsync(slug, ct);
    if (logo is null) return Results.NotFound();
    var etag = $"\"{logo.Version:x}\"";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.ETag = etag;
    if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Bytes(logo.Data, logo.ContentType);
});

app.MapMethods("/healthz", ["GET", "HEAD"], async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
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
    public const string Chat = "chat";
}

public partial class Program;
