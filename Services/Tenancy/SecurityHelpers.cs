using System.Security.Cryptography;
using System.Text;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>Time-based one-time passwords (RFC 6238) — works with Google/Microsoft Authenticator.</summary>
    public static class Totp
    {
        private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string NewSecret()
        {
            var bytes = RandomNumberGenerator.GetBytes(20);
            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (var b in bytes)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { sb.Append(B32[(buffer >> (bits - 5)) & 31]); bits -= 5; }
            }
            if (bits > 0) sb.Append(B32[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        private static byte[] Decode(string secret)
        {
            var s = secret.Trim().Replace(" ", "").TrimEnd('=').ToUpperInvariant();
            var output = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var c in s)
            {
                var v = B32.IndexOf(c);
                if (v < 0) continue;
                buffer = (buffer << 5) | v; bits += 5;
                if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
            }
            return output.ToArray();
        }

        public static string Code(string secret, long step)
        {
            var counter = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian) Array.Reverse(counter);
            using var h = new HMACSHA1(Decode(secret));
            var hash = h.ComputeHash(counter);
            int o = hash[^1] & 0x0F;
            int bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
            return (bin % 1_000_000).ToString("D6");
        }

        /// <summary>Accepts the current 30-second code ± one step for clock drift.</summary>
        public static bool Verify(string? secret, string? code)
        {
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
            code = code.Replace(" ", "").Trim();
            var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            for (long d = -1; d <= 1; d++)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step + d)), Encoding.ASCII.GetBytes(code)))
                    return true;
            return false;
        }

        public static string Uri(string issuer, string account, string secret) =>
            $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6&period=30";
    }

    public static class PasswordPolicy
    {
        /// <summary>Null = OK, otherwise the reason it's rejected.</summary>
        public static string? Validate(string? password, Company? policy)
        {
            int min = Math.Max(6, policy?.PasswordMinLength ?? 8);
            bool complex = policy?.PasswordRequireComplex ?? true;
            if (string.IsNullOrEmpty(password) || password.Length < min) return $"Password must be at least {min} characters.";
            if (complex)
            {
                if (!password.Any(char.IsUpper)) return "Password needs at least one capital letter.";
                if (!password.Any(char.IsLower)) return "Password needs at least one small letter.";
                if (!password.Any(char.IsDigit)) return "Password needs at least one number.";
                if (password.All(char.IsLetterOrDigit)) return "Password needs at least one symbol (e.g. @ # $ !).";
            }
            return null;
        }

        public static string Describe(Company? policy)
        {
            int min = Math.Max(6, policy?.PasswordMinLength ?? 8);
            return (policy?.PasswordRequireComplex ?? true)
                ? $"At least {min} characters with a capital letter, a small letter, a number and a symbol."
                : $"At least {min} characters.";
        }

        /// <summary>Readable random password for resets / new company admins.</summary>
        public static string Generate()
        {
            const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", lo = "abcdefghijkmnpqrstuvwxyz", dg = "23456789", sy = "@#$%!";
            char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
            var chars = new List<char> { Pick(up), Pick(lo), Pick(dg), Pick(sy) };
            for (int i = 0; i < 6; i++) chars.Add(Pick(up + lo + dg));
            return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).ToArray());
        }
    }
}
