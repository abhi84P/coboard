using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Coboard.AuthService.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Coboard.AuthService.Auth;

public interface ITokenService
{
    string Issue(User user);
}

public class TokenService : ITokenService
{
    private readonly JwtOptions _opts;

    public TokenService(IOptions<JwtOptions> opts) => _opts = opts.Value;

    public string Issue(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("displayName", user.DisplayName),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_opts.ExpiryMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public interface IPasswordHasher
{
    string Hash(string plaintext);
    bool Verify(string plaintext, string hash);
}

public class BCryptPasswordHasher : IPasswordHasher
{
    public string Hash(string plaintext) => BCrypt.Net.BCrypt.HashPassword(plaintext, workFactor: 11);
    public bool Verify(string plaintext, string hash) => BCrypt.Net.BCrypt.Verify(plaintext, hash);
}
