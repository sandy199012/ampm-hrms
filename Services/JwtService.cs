using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // JWT SERVICE — the mobile app's counterpart to AccountController's
    // Cookie sign-in. Same identity (EmpCode + BCrypt password check
    // against Employee.PasswordHash), same claim shape (NameIdentifier =
    // Employee.Id, Role = Employee.Role, "EmpCode"), just packaged as a
    // signed token instead of a Set-Cookie header — a mobile app has no
    // browser cookie jar to rely on. Program.cs wires up JWT Bearer
    // validation using the same Jwt:Key/Issuer/Audience from appsettings.
    // ═══════════════════════════════════════════
    public interface IJwtService
    {
        string GenerateToken(Employee employee);
    }

    public class JwtService : IJwtService
    {
        readonly IConfiguration _config;
        public JwtService(IConfiguration config) => _config = config;

        public string GenerateToken(Employee employee)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, employee.Id.ToString()),
                new(ClaimTypes.Name, employee.Name),
                new(ClaimTypes.Role, employee.Role),
                new("EmpCode", employee.EmpCode),
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"] ?? "AMPM-HRMS-Mobile-Fallback-Key-Please-Configure-32chars+"));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            int expiryHours = int.TryParse(_config["Jwt:ExpiryHours"], out var h) ? h : 12;

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(expiryHours),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
