using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DayzStats.Api.Data;
using DayzStats.Api.Endpoints;
using DayzStats.Api.Import;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DayzStats.Api.Tests;

/// <summary>Sobe a API inteira com um SQLite em memória e o log de exemplo já importado.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public const string ApiKey = "chave-de-teste";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseSetting("Import:ApiKey", ApiKey);
        builder.UseSetting("Import:Folder", ExamplesFolder);
        builder.ConfigureServices(services =>
        {
            // tira o SQLite em arquivo configurado no Program e põe o de memória no lugar
            services.RemoveAll<DbContextOptions<StatsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<StatsDbContext>>();
            services.AddDbContext<StatsDbContext>(o => o.UseSqlite(_connection));
        });
    }

    public static string ExamplesFolder => Path.Combine(AppContext.BaseDirectory, "examples");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}

public class ApiTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async ValueTask InitializeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import");
        request.Headers.Add("X-Api-Key", ApiFactory.ApiKey);
        (await _client.SendAsync(request, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stats_SummarizesTheExampleLog()
    {
        var stats = await _client.GetFromJsonAsync<ServerStats>("/api/stats", Ct);

        Assert.NotNull(stats);
        Assert.Equal(5, stats.Players);
        Assert.Equal(7, stats.Deaths);
        Assert.Equal(2, stats.PvpKills);
        Assert.Equal(1, stats.AiKills);
        Assert.Equal(1, stats.ImportedFiles);
        Assert.Contains(stats.DeathCauses, c => c.Cause == "jogador" && c.Count == 2);
    }

    [Fact]
    public async Task ImportingAgain_SkipsFilesAlreadyImported()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import");
        request.Headers.Add("X-Api-Key", ApiFactory.ApiKey);
        var response = await _client.SendAsync(request, Ct);
        var result = await response.Content.ReadFromJsonAsync<ImportResult>(Ct);

        Assert.Equal(0, result!.FilesImported);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public async Task Import_WithoutApiKey_IsUnauthorized()
    {
        var response = await _client.PostAsync("/api/import", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadingNonAdmFile_IsRejected()
    {
        using var form = new MultipartFormDataContent();
        var file = new StringContent("qualquer coisa");
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "notas.txt");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/file") { Content = form };
        request.Headers.Add("X-Api-Key", ApiFactory.ApiKey);

        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Leaderboard_ByKills()
    {
        var board = await _client.GetFromJsonAsync<List<LeaderboardEntry>>("/api/leaderboard?by=kills", Ct);

        var first = Assert.Single(board!);
        Assert.Equal("Lobo", first.Name);
        Assert.Equal(2, first.Value);
        Assert.Equal(1, first.Rank);
    }

    [Fact]
    public async Task Leaderboard_ByPlaytime_IsInHours()
    {
        var board = await _client.GetFromJsonAsync<List<LeaderboardEntry>>("/api/leaderboard?by=playtime&top=2", Ct);

        Assert.Equal(2, board!.Count);
        Assert.Equal("Lobo", board[0].Name);
        Assert.Equal(1.55, board[0].Value);  // 5585 s
    }

    [Theory]
    [InlineData("/api/leaderboard?by=dano")]
    [InlineData("/api/leaderboard?top=0")]
    [InlineData("/api/players?page=0")]
    [InlineData("/api/deaths?pageSize=500")]
    public async Task InvalidParameters_Return400(string url)
    {
        var response = await _client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_SearchByName_SeparatesPlayersWithTheSameName()
    {
        var page = await _client.GetFromJsonAsync<PagedResult<PlayerSummary>>("/api/players?search=surv", Ct);

        Assert.Equal(2, page!.Total);
        Assert.All(page.Items, p => Assert.Equal("Survivor", p.Name));
        Assert.Equal(2, page.Items.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task PlayerDetail_HasKillsDeathsAndRecentDeaths()
    {
        var players = await _client.GetFromJsonAsync<PagedResult<PlayerSummary>>("/api/players?search=Lobo", Ct);
        var lobo = Assert.Single(players!.Items);

        var detail = await _client.GetFromJsonAsync<PlayerDetail>($"/api/players/{Uri.EscapeDataString(lobo.Id)}", Ct);

        Assert.Equal(2, detail!.Kills);
        Assert.Equal(1, detail.Deaths);
        Assert.Equal(2, detail.Sessions);
        Assert.Equal(2.0, detail.KillDeathRatio);
        Assert.Equal("animais", Assert.Single(detail.RecentDeaths).Cause);
    }

    [Fact]
    public async Task UnknownPlayer_Returns404()
    {
        var response = await _client.GetAsync("/api/players/nao-existe", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deaths_FilterByCause_IncludesKillerName()
    {
        var page = await _client.GetFromJsonAsync<PagedResult<DeathInfo>>("/api/deaths?cause=jogador", Ct);

        Assert.Equal(2, page!.Total);
        Assert.All(page.Items, d => Assert.Equal("Lobo", d.KillerName));
        Assert.Contains(page.Items, d => d.Weapon == "Mosin9130" && d.Distance == 462.3);
    }
}
