using System.Text;
using Coboard.AuthService.Auth;
using Coboard.AuthService.Endpoints;
using Coboard.AuthService.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// In Testing, the test factory swaps DbContext registration via ConfigureTestServices.
// Don't pre-register here or the pool will lock in the Npgsql provider.
if (!builder.Environment.IsEnvironment("Testing"))
{
    var conn = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres not set");
    builder.Services.AddSingleton<AuditFieldsInterceptor>();
    builder.Services.AddDbContextPool<AuthDbContext>((sp, opt) =>
    {
        opt.UseNpgsql(conn);
        opt.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        opt.AddInterceptors(sp.GetRequiredService<AuditFieldsInterceptor>());
    });
}

builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<ITokenService, TokenService>();

// JWT validation. Resolve the signing key from IOptions<JwtOptions> at request time
// so the test factory's config override (applied after this registration) takes effect.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOpts) =>
    {
        var jwt = jwtOpts.Value;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "auth" }));
app.MapAuth();

app.Run();
