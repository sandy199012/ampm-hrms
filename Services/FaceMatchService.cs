using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // FACE MATCH SERVICE — deliberately vendor-agnostic, same philosophy as
    // BiometricSyncService: nothing vendor-specific is hardcoded here, only
    // Admin > Attendance > Face Match Settings changes when the vendor
    // does.
    //
    // The one thing that CAN'T be made fully generic without knowing the
    // real vendor: the request payload shape. This service assumes the
    // configured VerifyUrl accepts a POST with JSON body
    // { "image1": "<base64>", "image2": "<base64>" } and returns a
    // similarity score at ConfidenceField — the most common shape for a
    // "compare two faces" REST endpoint. A cloud vendor with a different
    // native protocol (e.g. Azure Face API's separate detect-then-verify-
    // by-faceId flow, which needs two calls, not one) needs a small adapter
    // endpoint in front of it that speaks this contract — that adapter is
    // outside this codebase since it depends on which vendor is actually
    // chosen, which hasn't been set up yet. Until Face Match Settings is
    // enabled and pointed at something real, VerifyAsync always reports
    // no match with a clear reason, so a punch is never silently
    // face-verified by accident.
    // ═══════════════════════════════════════════
    public interface IFaceMatchService
    {
        Task<(bool Matched, decimal ConfidencePercent, string Message)> VerifyAsync(byte[] enrolledPhoto, byte[] livePhoto);
    }

    public class FaceMatchService : IFaceMatchService
    {
        readonly IHttpClientFactory _httpFactory;
        readonly AppDbContext _db;
        public FaceMatchService(IHttpClientFactory httpFactory, AppDbContext db) { _httpFactory = httpFactory; _db = db; }

        public async Task<(bool, decimal, string)> VerifyAsync(byte[] enrolledPhoto, byte[] livePhoto)
        {
            var settings = await _db.FaceMatchApiSettingsList.FirstOrDefaultAsync();
            if (settings == null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.VerifyUrl))
                return (false, 0, "Face Match API is not configured/enabled — punch was accepted without face verification. Configure it under Admin > Attendance > Face Match Settings.");

            try
            {
                var client = _httpFactory.CreateClient();
                var req = new HttpRequestMessage(HttpMethod.Post, settings.VerifyUrl);

                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    var headerName = string.IsNullOrWhiteSpace(settings.AuthHeaderName) ? "Authorization" : settings.AuthHeaderName!;
                    var value = settings.AuthScheme == "Bearer" ? $"Bearer {settings.ApiKey}" : settings.ApiKey!;
                    req.Headers.TryAddWithoutValidation(headerName, value);
                }

                var body = JsonSerializer.Serialize(new { image1 = Convert.ToBase64String(enrolledPhoto), image2 = Convert.ToBase64String(livePhoto) });
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var resp = await client.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return (false, 0, $"Face Match API returned HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                using var doc = JsonDocument.Parse(raw);

                decimal confidence = 0;
                if (!string.IsNullOrWhiteSpace(settings.ConfidenceField))
                {
                    var el = NavigatePath(doc.RootElement, settings.ConfidenceField!);
                    if (el.ValueKind is JsonValueKind.Number && el.TryGetDecimal(out var num))
                        confidence = settings.ConfidenceIsFraction ? num * 100m : num;
                }

                bool? explicitIdentical = null;
                if (!string.IsNullOrWhiteSpace(settings.IsIdenticalField))
                {
                    var el = NavigatePath(doc.RootElement, settings.IsIdenticalField!);
                    if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) explicitIdentical = el.GetBoolean();
                }

                bool matched = explicitIdentical ?? confidence >= settings.MinConfidencePercent;
                string msg = matched
                    ? $"Face matched ({confidence:0.#}% confidence)."
                    : $"Face did not match (confidence {confidence:0.#}%, needs {settings.MinConfidencePercent:0.#}%).";

                settings.LastTestAt = DateTime.Now;
                settings.LastTestStatus = "Success";
                settings.LastTestMessage = msg;
                await _db.SaveChangesAsync();

                return (matched, confidence, msg);
            }
            catch (Exception ex)
            {
                settings.LastTestAt = DateTime.Now;
                settings.LastTestStatus = "Failed";
                settings.LastTestMessage = ex.Message;
                await _db.SaveChangesAsync();
                return (false, 0, $"Face Match API call failed: {ex.Message}");
            }
        }

        static JsonElement NavigatePath(JsonElement root, string path)
        {
            var current = root;
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(seg, out var next))
                    return default;
                current = next;
            }
            return current;
        }
    }
}
