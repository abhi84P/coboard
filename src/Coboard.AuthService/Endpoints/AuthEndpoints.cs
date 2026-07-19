using System.Security.Claims;
using Coboard.AuthService.Auth;
using Coboard.AuthService.Domain;
using Coboard.AuthService.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Coboard.AuthService.Endpoints;

public record RegisterRequest(string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string Token, Guid UserId, string Email, string DisplayName);
public record UserResponse(Guid Id, string Email, string DisplayName, DateTime CreatedAt);

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", async Task<Results<Created<AuthResponse>, Conflict<string>, BadRequest<string>>> (
            [FromBody] RegisterRequest req,
            AuthDbContext db,
            IPasswordHasher hasher,
            ITokenService tokens,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return TypedResults.BadRequest("email and password are required");
            if (req.Password.Length < 8)
                return TypedResults.BadRequest("password must be at least 8 characters");

            var email = req.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                return TypedResults.Conflict("email already registered");

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = req.DisplayName.Trim(),
                PasswordHash = hasher.Hash(req.Password),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            var resp = new AuthResponse(tokens.Issue(user), user.Id, user.Email, user.DisplayName);
            return TypedResults.Created($"/auth/users/{user.Id}", resp);
        });

        group.MapPost("/login", async Task<Results<Ok<AuthResponse>, UnauthorizedHttpResult, BadRequest<string>>> (
            [FromBody] LoginRequest req,
            AuthDbContext db,
            IPasswordHasher hasher,
            ITokenService tokens,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return TypedResults.BadRequest("email and password are required");

            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.DeletedAt == null, ct);
            if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
                return TypedResults.Unauthorized();

            var resp = new AuthResponse(tokens.Issue(user), user.Id, user.Email, user.DisplayName);
            return TypedResults.Ok(resp);
        });

        // /me — protected via AddAuthentication(JwtBearer) + RequireAuthorization().
        // Returns IResult (not Results<...>) so 401 can be returned without union type.
        group.MapGet("/me", async (HttpContext http, AuthDbContext db, CancellationToken ct) =>
        {
            var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? http.User.FindFirstValue("sub");
            if (!Guid.TryParse(sub, out var userId))
                return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
            if (user is null) return Results.Unauthorized();

            return Results.Ok(new UserResponse(user.Id, user.Email, user.DisplayName, user.CreatedAt));
        }).RequireAuthorization();
    }
}
