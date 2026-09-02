using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // MOBILE NOTIFICATIONS — in-app only for now (see Models/MobileModels.cs's
    // Notification class remarks: no push-service credentials are
    // configured yet). The app polls unread-count and the list itself;
    // ApplicationsController and MobileApplicationsController/
    // MobileManagerController are what actually CREATE these rows.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile/notifications")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class MobileNotificationsController : ControllerBase
    {
        readonly AppDbContext _db;
        public MobileNotificationsController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var rows = await _db.Notifications.Where(n => n.EmployeeId == CurrentEmpId)
                .OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync();
            return Ok(rows.Select(n => new
            {
                id = n.Id, title = n.Title, message = n.Message, type = n.Type,
                relatedApplicationId = n.RelatedApplicationId, isRead = n.IsRead, createdAt = n.CreatedAt,
            }));
        }

        [HttpGet("unread-count")]
        public async Task<IActionResult> UnreadCount()
            => Ok(new { count = await _db.Notifications.CountAsync(n => n.EmployeeId == CurrentEmpId && !n.IsRead) });

        [HttpPost("mark-read/{id}")]
        public async Task<IActionResult> MarkRead(int id)
        {
            var n = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.EmployeeId == CurrentEmpId);
            if (n == null) return NotFound();
            n.IsRead = true;
            await _db.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost("mark-all-read")]
        public async Task<IActionResult> MarkAllRead()
        {
            var unread = await _db.Notifications.Where(n => n.EmployeeId == CurrentEmpId && !n.IsRead).ToListAsync();
            foreach (var n in unread) n.IsRead = true;
            await _db.SaveChangesAsync();
            return Ok(new { success = true, count = unread.Count });
        }
    }
}
