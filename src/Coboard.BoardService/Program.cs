using Microsoft.AspNetCore.Http;


using Coboard.BoardService.Endpoints;
using Coboard.BoardService.Persistence;
using Coboard.BoardService.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Pattern: DbContext pooling + NoTracking default + interceptor ----
builder.Services.AddHttpContextAccessor();
 builder.Services.AddSingleton<SoftDeleteAndAuditInterceptor>();
builder.Services.AddDbContextPool<BoardDbContext>((sp, opt) =>
{
    var conn = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres not set");
    opt.UseNpgsql(conn);
    opt.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    opt.AddInterceptors(sp.GetRequiredService<SoftDeleteAndAuditInterceptor>());
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "board" }));

app.MapShapes();
app.Run();
