using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using ZPlus.Server.Models;

namespace ZPlus.Server.Services;

/// <summary>JWT signing material, loaded from the database at startup.</summary>
public record JwtConfig(byte[] SigningKey)
{
    public const string Issuer = "ZPlus.Server";
    public const string Audience = "ZPlus.Client";
}

public class TokenService(JwtConfig config)
{
    public string CreateToken(User user)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(config.SigningKey), SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
        };

        var token = new JwtSecurityToken(
            issuer: JwtConfig.Issuer,
            audience: JwtConfig.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
