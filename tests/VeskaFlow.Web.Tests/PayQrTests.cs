using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The GCash QR code the platform owner uploads and the shops see.</summary>
[Collection("http")]
public class PayQrTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task The_picture_is_checked_saved_replaced_and_removed()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var images = new PlatformImageService(db, TimeProvider.System);
        await images.RemoveAsync(PlatformImageService.GcashQr);

        Assert.Null(await images.VersionAsync(PlatformImageService.GcashQr));
        Assert.Contains("image", (await images.SaveAsync(PlatformImageService.GcashQr, null)).FieldErrors.Keys);
        Assert.Contains("image", (await images.SaveAsync(PlatformImageService.GcashQr, "<svg onload=alert(1)>"u8.ToArray())).FieldErrors.Keys);
        Assert.Contains("image", (await images.SaveAsync(PlatformImageService.GcashQr, Png.Concat(new byte[PlatformImageService.MaxBytes]).ToArray())).FieldErrors.Keys);

        Assert.True((await images.SaveAsync(PlatformImageService.GcashQr, Png)).IsSuccess);
        Assert.Equal("image/png", (await images.GetAsync(PlatformImageService.GcashQr))!.ContentType);
        Assert.True((await images.SaveAsync(PlatformImageService.GcashQr, [0xFF, 0xD8, 0xFF, 0xE0, 9])).IsSuccess);
        Assert.Equal("image/jpeg", (await images.GetAsync(PlatformImageService.GcashQr))!.ContentType);

        await images.RemoveAsync(PlatformImageService.GcashQr);
        Assert.Null(await images.GetAsync(PlatformImageService.GcashQr));
    }

    [Fact]
    public async Task Only_the_platform_owner_uploads_and_only_signed_in_people_see_it_on_About_and_the_pay_address()
    {
        var bossEmail = $"{Id("boss")}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await new PlatformImageService(db, TimeProvider.System).RemoveAsync(PlatformImageService.GcashQr);

        async Task<HttpClient> Register(string email)
        {
            var client = HttpApp.NewClient(factory);
            await HttpApp.PostFormAsync(client, "/Register", new()
            {
                ["Input.Accept"] = "true", ["Input.BusinessName"] = "Qr Shop", ["Input.Slug"] = Id("qr"), ["Input.OwnerName"] = "Owner",
                ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
            });
            return client;
        }
        var boss = await Register(bossEmail);
        var shop = await Register($"{Id("s")}@example.com");
        var visitor = HttpApp.NewClient(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await shop.GetAsync("/pay-qr")).StatusCode); // nothing uploaded yet
        Assert.DoesNotContain("class=\"pay-qr\"", await shop.GetStringAsync("/Admin/About"));

        async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes)
        {
            var page = await client.GetStringAsync("/Platform");
            using var form = new MultipartFormDataContent
            {
                { new StringContent(HttpApp.TokenIn(page, "/Platform")), "__RequestVerificationToken" },
            };
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "picture", "qr.png");
            return await client.PostAsync("/Platform?handler=PayQr", form);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await Upload(boss, Png)).StatusCode);
        var afterUpload = await boss.GetStringAsync("/Platform");
        Assert.True(afterUpload.Contains("class=\"pay-qr\""), string.Join(" | ", System.Text.RegularExpressions.Regex.Matches(afterUpload, "<div class=\"(notice|alert)\"[^>]*>[^<]*").Select(m => m.Value)));

        var shown = await shop.GetAsync("/pay-qr");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("image/png", shown.Content.Headers.ContentType!.MediaType);
        Assert.Contains("class=\"pay-qr\"", await shop.GetStringAsync("/Admin/About"));

        var again = new HttpRequestMessage(HttpMethod.Get, "/pay-qr");
        again.Headers.TryAddWithoutValidation("If-None-Match", shown.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await shop.SendAsync(again)).StatusCode);

        var anonymous = await visitor.GetAsync("/pay-qr");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode); // sent to sign in, never the picture
        Assert.Contains("/Login", anonymous.Headers.Location!.OriginalString);

        var notBoss = await shop.GetAsync("/Platform");
        Assert.Equal(HttpStatusCode.NotFound, notBoss.StatusCode);
    }

    [Fact]
    public async Task The_written_price_and_pay_to_give_way_to_the_QR_code_and_come_back_without_it()
    {
        Environment.SetEnvironmentVariable("Billing__Price", "PHP 777 per month");
        Environment.SetEnvironmentVariable("Billing__PayTo", "GCash 0900 000 0000 (Test Name)");
        try
        {
            var bossEmail = $"{Id("boss")}@example.com";
            var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
            using var _f = factory;
            var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
            var images = new PlatformImageService(db, TimeProvider.System);
            await images.RemoveAsync(PlatformImageService.GcashQr);

            var client = HttpApp.NewClient(factory);
            await HttpApp.PostFormAsync(client, "/Register", new()
            {
                ["Input.Accept"] = "true", ["Input.BusinessName"] = "Qr Shop", ["Input.Slug"] = Id("qr"), ["Input.OwnerName"] = "Owner",
                ["Input.Email"] = bossEmail, ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
            });

            var without = await client.GetStringAsync("/Admin/About");
            Assert.Contains("PHP 777 per month", without);
            Assert.Contains("GCash 0900 000 0000", without);

            await images.SaveAsync(PlatformImageService.GcashQr, Png);
            var with = await client.GetStringAsync("/Admin/About");
            Assert.Contains("class=\"pay-qr\"", with);
            Assert.DoesNotContain("PHP 777 per month", with);
            Assert.DoesNotContain("GCash 0900 000 0000", with);

            await images.RemoveAsync(PlatformImageService.GcashQr);
            Assert.Contains("PHP 777 per month", await client.GetStringAsync("/Admin/About"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Billing__Price", null);
            Environment.SetEnvironmentVariable("Billing__PayTo", null);
        }
    }
}
