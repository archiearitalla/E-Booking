// Placeholder entry point so the project builds in Step 0. Step 1 replaces this with the real startup.
var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = BarberBook.Web.AppInfo.Version }));
app.Run();
