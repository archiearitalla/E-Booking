using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BarberBook.Web.Tests;

public sealed class AppFactory : WebApplicationFactory<Program> { }

/// <summary>The real app in-process against the real PostgreSQL test database. Use with <c>[Collection("http")]</c>.</summary>
public static class HttpApp
{
    /// <summary>Configuration is read when the app starts, so settings are applied just before the host is created.</summary>
    public static async Task<(AppFactory Factory, HttpClient Client)> StartAsync(int authPerMinute = 1000, int bookingPerMinute = 1000)
    {
        var cs = await TestDb.ConnectionStringAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("RateLimits__AuthPerMinute", authPerMinute.ToString());
        Environment.SetEnvironmentVariable("RateLimits__BookingPerMinute", bookingPerMinute.ToString());
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false"); // the test database is already migrated
        var factory = new AppFactory();
        return (factory, NewClient(factory));
    }

    public static HttpClient NewClient(AppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static async Task<string> TokenFromAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        return TokenIn(html, path);
    }

    public static string TokenIn(string html, string where)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No antiforgery token on {where}");
        return match.Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields, string? tokenPage = null)
    {
        fields["__RequestVerificationToken"] = await TokenFromAsync(client, tokenPage ?? path);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    public static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];
}
