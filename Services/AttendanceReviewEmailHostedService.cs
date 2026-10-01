// Services/AttendanceReviewEmailHostedService.cs
// Daily background service that sends attendance-gap emails each morning.
// Follows the same IHostedService pattern as BiometricSyncHostedService
// and HrNotificationHostedService (see those files for pattern rationale).
using AmpmHrmsPro.Services;
using Microsoft.Extensions.Options;

namespace AmpmHrmsPro.Services
{
    public class AttendanceReviewEmailHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory              _scopeFactory;
        private readonly ILogger<AttendanceReviewEmailHostedService> _log;
        private readonly AttendanceReviewOptions           _opts;

        public AttendanceReviewEmailHostedService(
            IServiceScopeFactory scopeFactory,
            IOptions<AttendanceReviewOptions> opts,
            ILogger<AttendanceReviewEmailHostedService> log)
        {
            _scopeFactory = scopeFactory;
            _opts         = opts.Value;
            _log          = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation(
                "AttendanceReviewEmailHostedService started — daily fire at {:D2}:{:D2}",
                _opts.EmailScheduleHour, _opts.EmailScheduleMinute);

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeUntilNextFire(_opts.EmailScheduleHour, _opts.EmailScheduleMinute);
                _log.LogInformation("AttendanceReview: next email run in {Minutes:F0} min", delay.TotalMinutes);

                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { break; }

                if (stoppingToken.IsCancellationRequested) break;

                await RunAsync(stoppingToken);
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAttendanceReviewService>();
                var (sent, failed) = await svc.SendDailyEmailsAsync(ct);
                _log.LogInformation("AttendanceReview daily run — sent: {Sent}, failed: {Failed}", sent, failed);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AttendanceReview daily run error");
            }
        }

        // Calculate delay until the next scheduled fire time (daily).
        private static TimeSpan TimeUntilNextFire(int hour, int minute)
        {
            var now  = DateTime.Now;
            var next = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }
    }
}
