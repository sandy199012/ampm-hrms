using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // MOBILE PAYSLIP — placeholder. There is no Salary/Payroll module in
    // this system yet (pay structure, deductions, payslip generation),
    // so there is nothing real to serve here. Kept as its own endpoint —
    // not just hidden in the app — so the screen can show a clear
    // "coming soon" state instead of an error, and so wiring the real
    // payslip data in later is a one-file change (this controller) with
    // no app-side changes needed.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile/payslip")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class MobilePayslipController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get(int? year, int? month)
            => Ok(new { available = false, message = "Payslips aren't available yet — the Payroll module hasn't been set up." });
    }
}
