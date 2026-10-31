namespace VeskaFlow.Web.Services;

/// <summary>
/// Gives back the time of bookings that were held for an email confirmation and never confirmed (BUSINESS_RULES 8.8). Once a minute.
/// Availability and new bookings already ignore a hold whose time ran out; this makes the booking show as cancelled and keeps the list tidy.
/// </summary>
public sealed class HoldExpiryWorker(IServiceScopeFactory scopes, ILogger<HoldExpiryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var released = await scope.ServiceProvider.GetRequiredService<BookingService>().ExpireHoldsAsync(stoppingToken);
                    if (released > 0) log.LogInformation("Released {Count} bookings that were not confirmed by email in time.", released);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Releasing unconfirmed bookings could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
