namespace AmpmHrmsPro.Models
{
    // Summary handed back to the Bulk Upload result screen after an import —
    // what got created/updated, any masters that were auto-created along the
    // way, and any manager names that couldn't be matched to an employee.
    public class BulkImportResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public List<string> MastersCreated { get; set; } = new();
        public List<string> ManagerNotFound { get; set; } = new();
    }
}
