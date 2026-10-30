
using MediDrift.Api.Data;
using MediDrift.Api.Endpoints;
using MediDrift.Api.Models;
using MediDrift.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 12 * 1024 * 1024;
});

// PostgreSQL connection
var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "DefaultConnection is missing from configuration.");
}

// Register PostgreSQL with EF Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString)
);

builder.Services.AddSingleton<ImportFileParser>();
builder.Services.AddSingleton<ColumnMappingService>();

builder.Services.AddHttpClient<RxNormClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["RxNorm:BaseUrl"]
        ?? "https://rxnav.nlm.nih.gov/"
    );

    client.Timeout = TimeSpan.FromSeconds(25);

    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "MediDrift-LocalMVP/0.3"
    );
});

builder.Services.AddScoped<MedicationAuditService>();

builder.Services.AddHostedService<MedicationAuditWorker>();

// CORS configuration
var allowedOrigins =
    builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>()
    ?? new[]
    {
        "http://localhost:5173",
        "http://127.0.0.1:5173"
    };

builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalFrontend", policy =>
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
    );
});

var app = builder.Build();

app.UseCors("LocalFrontend");

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException)
        when (context.RequestAborted.IsCancellationRequested)
    {
        // Client disconnected.
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Unhandled request error for {Path}",
            context.Request.Path
        );

        if (!context.Response.HasStarted)
        {
            context.Response.Clear();

            context.Response.StatusCode =
                StatusCodes.Status500InternalServerError;

            await context.Response.WriteAsJsonAsync(
                new ApiError(
                    "An unexpected server error occurred."
                )
            );
        }
    }
});

// Apply PostgreSQL EF Core migrations and initialize workspace
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<AppDbContext>();

    await db.Database.MigrateAsync();

    if (!await db.Workspaces.AnyAsync(
        x => x.Id == DefaultWorkspace.Id))
    {
        db.Workspaces.Add(new Workspace
        {
            Id = DefaultWorkspace.Id,
            Name = DefaultWorkspace.Name,
            CreatedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "MediDrift Column Mapper API",
    version = "0.3.0"
}));

app.MapImportEndpoints();
app.MapMappingProfileEndpoints();
app.MapMedicationAuditEndpoints();

app.Run();

public partial class Program { }
