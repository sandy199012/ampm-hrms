using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // NOTIFICATION HELPER — one call-site for creating an in-app
    // Notification row, used from both the existing (Admin/HR) web
    // Applications flow and the new mobile Applications/Approvals API, so
    // an employee gets notified the same way regardless of which side
    // acted on their request. Doesn't call SaveChangesAsync itself — the
    // caller is already about to save the rest of its own changes in the
    // same request, so this just stages the row.
    // ═══════════════════════════════════════════
    public static class NotificationHelper
    {
        public static void Notify(AppDbContext db, int employeeId, string title, string? message, string type, int? relatedApplicationId = null)
        {
            db.Notifications.Add(new Notification
            {
                EmployeeId = employeeId,
                Title = title,
                Message = message,
                Type = type,
                RelatedApplicationId = relatedApplicationId,
            });
        }
    }
}
