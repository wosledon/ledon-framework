using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Ledon.Security;

/// <summary>JWT 令牌签发器（登录成功后调用）。</summary>
public interface IJwtTokenGenerator
{
    /// <summary>签发访问令牌。</summary>
    /// <param name="userId">用户标识（写入 sub 与 nameidentifier）。</param>
    /// <param name="userName">用户名。</param>
    /// <param name="permissions">权限列表（每条一个 permission claim）。</param>
    string Generate(string userId, string userName, IEnumerable<string>? permissions = null);
}

internal sealed class JwtTokenGenerator(LedonSecurityOptions options) : IJwtTokenGenerator
{
    public string Generate(string userId, string userName, IEnumerable<string>? permissions = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (permissions is not null)
            claims.AddRange(permissions.Select(permission => new Claim(options.PermissionClaimType, permission)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Jwt.SigningKey));
        var token = new JwtSecurityToken(
            issuer: options.Jwt.Issuer,
            audience: options.Jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(options.Jwt.Expiration),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
