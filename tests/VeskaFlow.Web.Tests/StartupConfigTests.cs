using static VeskaFlow.Web.Tests.HttpApp;

namespace VeskaFlow.Web.Tests;

/// <summary>Startup with a bad connection string, against the real app. Shares the "http" collection because the app reads environment variables.</summary>
[Collection("http")]
public class StartupConfigTests
{
    [Fact]
    public async Task A_url_style_connection_string_stops_startup_with_a_clear_message_and_never_echoes_the_password()
    {
        var before = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        const string secret = "S3cretValueThatMustNotLeak";
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"postgresql://someone:{secret}@db.example.com/appdb?sslmode=require&channel_binding=require");
        try
        {
            using var factory = new AppFactory();
            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            var all = new List<string>();
            for (Exception? e = ex; e is not null; e = e.InnerException) all.Add(e.GetType().Name + ": " + e.Message);
            var text = string.Join("\n", all);

            Assert.Contains("not a valid Npgsql connection string", text);
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("someone", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", before);
        }
        await Task.CompletedTask;
    }
}
