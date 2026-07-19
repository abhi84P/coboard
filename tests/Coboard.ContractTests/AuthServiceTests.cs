using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Coboard.AuthService.Auth;
using Coboard.AuthService.Endpoints;
using Coboard.AuthService.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Coboard.ContractTests;

/// <summary>
/// Integration test for the Auth service. Program.cs skips the Npgsql DbContext
/// registration in Testing env, so this factory adds the SQLite-bound one via
/// ConfigureTestServices. Program.cs owns JWT/Auth/Authorization setup.
/// </summary>
public class AuthServiceTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _signingKey = "test-signing-key-must-be-at-least-32-bytes-long!!";
    private WebApplicationFactory<Coboard.AuthService.Marker>? _factory;

    public AuthServiceTests()
    {
        // Shared in-memory SQLite: all connections see the same database for the test's lifetime.
        // Cache=Shared keeps the DB alive while the pooling connection is open.
        _conn = new SqliteConnection("DataSource=file::memory:?cache=shared");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Coboard.AuthService.Marker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SigningKey"] = _signingKey,
                    ["Jwt:Issuer"] = "coboard-auth",
                    ["Jwt:Audience"] = "coboard",
                    ["Jwt:ExpiryMinutes"] = "60",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<AuditFieldsInterceptor>();
                services.AddDbContext<AuthDbContext>((sp, opt) =>
                    opt.UseSqlite(_conn)
                       .AddInterceptors(sp.GetRequiredService<AuditFieldsInterceptor>()));
            });
        });
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ctx.Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _factory?.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Register_CreatesUserAndReturnsToken()
    {
        var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("alice@example.com", "hunter2hunter2", "Alice"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        body!.Email.Should().Be("alice@example.com");
        body.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var client = _factory!.CreateClient();
        await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("dup@example.com", "hunter2hunter2", "Dup1"));
        var resp = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("dup@example.com", "hunter2hunter2", "Dup2"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Register_WeakPassword_Returns400()
    {
        var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("weak@example.com", "short", "Weak"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsToken()
    {
        var client = _factory!.CreateClient();
        await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("bob@example.com", "hunter2hunter2", "Bob"));

        var resp = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest("bob@example.com", "hunter2hunter2"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var client = _factory!.CreateClient();
        await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("carol@example.com", "hunter2hunter2", "Carol"));

        var resp = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest("carol@example.com", "wrongpassword"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithValidToken_ReturnsUserAndStampedCreatedAt()
    {
        var client = _factory!.CreateClient();
        var reg = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("dave@example.com", "hunter2hunter2", "Dave"));
        var auth = await reg.Content.ReadFromJsonAsync<AuthResponse>();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth!.Token);

        var resp = await client.GetAsync("/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await resp.Content.ReadFromJsonAsync<UserResponse>();
        user!.Email.Should().Be("dave@example.com");
        user.DisplayName.Should().Be("Dave");
        user.CreatedAt.Should().NotBe(default(DateTime));
        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IssuedToken_HasCorrectClaims()
    {
        var client = _factory!.CreateClient();
        var reg = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest("eve@example.com", "hunter2hunter2", "Eve"));
        var auth = await reg.Content.ReadFromJsonAsync<AuthResponse>();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth!.Token);
        jwt.Issuer.Should().Be("coboard-auth");
        jwt.Audiences.Should().Contain("coboard");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == "eve@example.com");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub);
    }
}
