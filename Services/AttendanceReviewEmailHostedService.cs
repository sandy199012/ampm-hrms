// Services/AttendanceReviewEmailHostedService.cs
// Daily background job that emails yesterday's attendance gaps to the HoDs.
//
// Checks every 5 minutes (India time) instead of sleeping until one exact
// moment: on Render's free plan the app can be asleep at 7:30 and wake up
// later, and the CSV may be uploaded after 7:30 — polling means the emails
// still go out as soon as both are true. Safe to repeat: SendDailyEmailsAsync
// only sends gaps that haven't been emailed yet.
using Microsoft.Extensions.Options;

using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Services
{
    public class AttendanceReviewEmailHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AttendanceReviewEmailHostedService> _log;
        private readonly AttendanceReviewOptions _opts;

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
                "AttendanceReviewEmailHostedService started — daily send from {Hour:D2}:{Minute:D2} IST",
                _opts.EmailScheduleHour, _opts.EmailScheduleMinute);

            // Give the app a moment to finish starting (DB schema init etc.).
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (TaskCanceledException) { return; }

            var start = new TimeSpan(_opts.EmailScheduleHour, _opts.EmailScheduleMinute, 0);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (IndiaTime.Now.TimeOfDay >= start)
                    await RunAsync(stoppingToken);

                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                using var root = _scopeFactory.CreateScope();
                await TenantJobs.ForEachActiveCompanyAsync(root.ServiceProvider, async (sp, companyId) =>
                {
                    var db = sp.GetRequiredService<AmpmHrmsPro.Data.AppDbContext>();
                    var modules = db.Companies.Where(c => c.Id == companyId).Select(c => c.Modules).FirstOrDefault();
                    if (!Modules.Parse(modules).Contains(Modules.AttendanceReview)) return;
                    var svc = sp.GetRequiredService<IAttendanceReviewService>();
                    var (sent, failed) = await svc.SendDailyEmailsAsync(ct);
                    if (sent > 0 || failed > 0)
                        _log.LogInformation("AttendanceReview daily run (company {Company}) — sent: {Sent}, failed: {Failed} ({Error})",
                            companyId, sent, failed, svc.LastSendError);
                }, _log, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AttendanceReview daily run error");
            }
        }
    }
}
