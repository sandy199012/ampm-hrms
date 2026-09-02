using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // TAX DECLARATION HELPER — the actual investment-declaration mutation
    // logic (upsert header/item, upload proof, admin approve/reject,
    // submit), shared between TaxController's "fill on an employee's
    // behalf" admin actions and MyTaxController's self-service actions, so
    // the two authorization surfaces (admin-scoped vs. self-scoped) never
    // drift out of sync on what a save actually does.
    // ═══════════════════════════════════════════
    public static class TaxDeclarationHelper
    {
        // Read-only — used to render the Declaration page. Deliberately does
        // NOT persist anything when no header exists yet (returns a
        // transient, unsaved instance with Id 0): this used to eagerly
        // INSERT an empty Draft row on every GET, so simply opening the
        // page (or a stray browser prefetch, or clicking through every row
        // of Admin's "Fill for employee..." picker) created junk rows for
        // employees who never declared anything. The real row is only
        // created lazily, the first time an actual write happens — see
        // EnsureHeaderId below.
        public static TaxDeclarationHeader GetOrCreateHeader(AppDbContext db, int employeeId, string financialYear)
        {
            var header = db.TaxDeclarationHeaders
                .Include(h => h.Items).ThenInclude(i => i.Section)
                .Include(h => h.Items).ThenInclude(i => i.ReviewedByEmployee)
                .FirstOrDefault(h => h.EmployeeId == employeeId && h.FinancialYear == financialYear);
            return header ?? new TaxDeclarationHeader { EmployeeId = employeeId, FinancialYear = financialYear };
        }

        // Get-or-create-and-save — call only from an actual write path.
        // Always resolves by (employeeId, financialYear), never by a
        // caller-posted headerId, so it's safe to call with a
        // caller-controlled employeeId as long as THAT value itself is
        // trustworthy (MyTaxController always passes CurrentEmpId from the
        // auth claim; TaxController's admin actions are role-gated).
        public static int EnsureHeaderId(AppDbContext db, int employeeId, string financialYear)
        {
            var existing = db.TaxDeclarationHeaders.FirstOrDefault(h => h.EmployeeId == employeeId && h.FinancialYear == financialYear);
            if (existing != null) return existing.Id;
            var header = new TaxDeclarationHeader { EmployeeId = employeeId, FinancialYear = financialYear };
            db.TaxDeclarationHeaders.Add(header);
            db.SaveChanges();
            return header.Id;
        }

        public static (bool ok, string message) SaveHeaderFields(AppDbContext db, int employeeId, string financialYear, string regimeChoice, decimal annualRentPaid, bool isMetroCity)
        {
            var header = db.TaxDeclarationHeaders.Find(EnsureHeaderId(db, employeeId, financialYear));
            if (header == null) return (false, "Declaration not found.");
            header.RegimeChoice = (regimeChoice == "Old" || regimeChoice == "New") ? regimeChoice : "Auto";
            header.AnnualRentPaid = Math.Max(0, annualRentPaid);
            header.IsMetroCity = isMetroCity;
            header.UpdatedAt = DateTime.Now;
            db.SaveChanges();
            return (true, "Saved.");
        }

        public static (bool ok, string message) UpsertItem(AppDbContext db, int employeeId, string financialYear, int sectionId, string? description, decimal declaredAmount, int? existingItemId)
        {
            var headerId = EnsureHeaderId(db, employeeId, financialYear);
            var header = db.TaxDeclarationHeaders.Find(headerId);
            if (header == null) return (false, "Declaration not found.");
            var section = db.TaxSectionMasters.Find(sectionId);
            if (section == null) return (false, "Please choose a valid section.");
            if (declaredAmount <= 0) return (false, "Declared amount must be greater than zero.");

            if (existingItemId.HasValue && existingItemId.Value > 0)
            {
                var existing = db.TaxDeclarationItems.FirstOrDefault(i => i.Id == existingItemId && i.TaxDeclarationHeaderId == headerId);
                if (existing == null) return (false, "Item not found.");
                if (existing.Status == "Approved") return (false, "This item is already approved — it can no longer be edited directly. Ask HR to revise it.");
                existing.Description = description; existing.DeclaredAmount = declaredAmount; existing.TaxSectionMasterId = sectionId;
                existing.Status = "Pending"; existing.ApprovedAmount = null; existing.AdminRemarks = null;
                existing.ReviewedByEmployeeId = null; existing.ReviewedAt = null;
            }
            else
            {
                db.TaxDeclarationItems.Add(new TaxDeclarationItem { TaxDeclarationHeaderId = headerId, TaxSectionMasterId = sectionId, Description = description, DeclaredAmount = declaredAmount });
            }
            header.UpdatedAt = DateTime.Now;
            db.SaveChanges();
            return (true, "Investment declared.");
        }

        public static (bool ok, string message) DeleteItem(AppDbContext db, int itemId, int headerIdGuard)
        {
            var item = db.TaxDeclarationItems.FirstOrDefault(i => i.Id == itemId && i.TaxDeclarationHeaderId == headerIdGuard);
            if (item == null) return (false, "Item not found.");
            if (item.Status == "Approved") return (false, "Can't remove an already-approved item.");
            db.TaxDeclarationItems.Remove(item);
            db.SaveChanges();
            return (true, "Removed.");
        }

        // Only images/PDFs — a proof-of-investment document has no reason
        // to be anything else, and this list also blocks the more dangerous
        // upload types (.html/.svg/.js) that would otherwise execute
        // same-origin when an HR reviewer opens one via "View Document".
        static readonly HashSet<string> AllowedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".pdf" };

        public static async Task<(bool ok, string message)> UploadDocumentAsync(AppDbContext db, int itemId, int headerIdGuard, IFormFile? file)
        {
            var item = db.TaxDeclarationItems.FirstOrDefault(i => i.Id == itemId && i.TaxDeclarationHeaderId == headerIdGuard);
            if (item == null) return (false, "Item not found.");
            if (item.Status == "Approved") return (false, "This item is already approved — its document can no longer be changed. Ask HR to revise it.");
            if (file == null || file.Length == 0) return (false, "Choose a file first.");
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !AllowedDocumentExtensions.Contains(ext))
                return (false, "Only JPG, PNG or PDF files are accepted for supporting documents.");
            item.DocumentUrl = await FileStorageHelper.SavePhotoAsync(file, "tax-documents");
            db.SaveChanges();
            return (true, "Document uploaded.");
        }

        // Admin-only in practice (both call sites gate this behind
        // [Authorize(Roles="admin,hr")] before calling) — approve/reject a
        // single declared item, per the user's explicit "verify/approve
        // workflow" choice.
        public static (bool ok, string message) ReviewItem(AppDbContext db, int itemId, string decision, decimal? approvedAmount, string? remarks, int reviewerEmployeeId)
        {
            var item = db.TaxDeclarationItems.Find(itemId);
            if (item == null) return (false, "Item not found.");
            if (decision != "Approved" && decision != "Rejected") return (false, "Invalid decision.");
            item.Status = decision;
            item.ApprovedAmount = decision == "Approved" ? (approvedAmount ?? item.DeclaredAmount) : null;
            item.AdminRemarks = remarks;
            item.ReviewedByEmployeeId = reviewerEmployeeId;
            item.ReviewedAt = DateTime.Now;
            db.SaveChanges();
            return (true, $"Item {decision.ToLower()}.");
        }

        public static (bool ok, string message) Submit(AppDbContext db, int headerId)
        {
            var header = db.TaxDeclarationHeaders.Find(headerId);
            if (header == null) return (false, "Declaration not found.");
            header.Status = "Submitted";
            header.SubmittedAt = DateTime.Now;
            db.SaveChanges();
            return (true, "Declaration submitted for HR review.");
        }
    }
}
