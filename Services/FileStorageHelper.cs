namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // FILE STORAGE HELPER — shared by every mobile-API endpoint that
    // accepts an uploaded photo (punch selfies, face enrollment, profile
    // photo). Saves under wwwroot/uploads/<subfolder>/ so the file is
    // directly servable through the app's existing UseStaticFiles()
    // middleware, and returns the web-relative path (e.g.
    // "/uploads/punches/xxx.jpg") — that's what gets stored on the
    // AttendancePunch/FaceProfile/Employee row, never a full disk path.
    // ═══════════════════════════════════════════
    public static class FileStorageHelper
    {
        public static async Task<string> SavePhotoAsync(IFormFile file, string subfolder)
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", subfolder);
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            var fileName = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(dir, fileName);
            using (var fs = File.Create(fullPath))
                await file.CopyToAsync(fs);
            return $"/uploads/{subfolder}/{fileName}";
        }

        public static byte[] ReadBytes(string webRelativePath)
        {
            var full = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", webRelativePath.TrimStart('/'));
            return File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
        }
    }
}
