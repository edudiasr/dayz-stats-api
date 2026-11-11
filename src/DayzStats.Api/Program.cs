using DayzStats.Api.Data;
using DayzStats.Api.Endpoints;
using DayzStats.Api.Import;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<StatsDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Stats") ?? "Data Source=dayzstats.db"));
builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection("Import"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LogImporter>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// banco pequeno e de um processo só: EnsureCreated basta, sem migrations
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<StatsDbContext>().Database.EnsureCreated();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("DayZ Stats API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Saúde");

app.MapStatsEndpoints();
app.MapImportEndpoints();

app.Run();

// deixa o WebApplicationFactory dos testes enxergar a classe
public partial class Program;
