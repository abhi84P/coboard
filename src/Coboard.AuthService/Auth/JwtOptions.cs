namespace Coboard.AuthService.Auth;

public class JwtOptions
{
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "coboard-auth";
    public string Audience { get; set; } = "coboard";
    public int ExpiryMinutes { get; set; } = 60;
}
