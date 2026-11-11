using DayzStats.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DayzStats.Api.Endpoints;

public static class StatsEndpoints
{
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Estatísticas");

        api.MapGet("/stats", GetStats).WithSummary("Resumo geral do servidor");
        api.MapGet("/players", ListPlayers).WithSummary("Lista jogadores, com busca por nome");
        api.MapGet("/players/{id}", GetPlayer).WithSummary("Detalhes de um jogador");
        api.MapGet("/leaderboard", GetLeaderboard).WithSummary("Ranking por kills, mortes ou tempo jogado");
        api.MapGet("/deaths", ListDeaths).WithSummary("Mortes, com filtro por causa, jogador e período");
        return app;
    }

    private static async Task<Ok<ServerStats>> GetStats(StatsDbContext db, CancellationToken ct)
    {
        var causes = await db.Deaths
            .GroupBy(d => d.Cause)
            .OrderByDescending(g => g.Count())
            .Select(g => new CauseCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var seconds = await db.Sessions.SumAsync(s => (long)s.Seconds, ct);
        var first = await db.Sessions.MinAsync(s => (DateTime?)s.Start, ct);
        var last = await db.Sessions.MaxAsync(s => (DateTime?)s.End, ct);

        return TypedResults.Ok(new ServerStats(
            Players: await db.Players.CountAsync(ct),
            Deaths: await db.Deaths.CountAsync(ct),
            PvpKills: await db.Deaths.CountAsync(d => d.KillerId != null, ct),
            AiKills: await db.Deaths.CountAsync(d => d.ByAi, ct),
            HoursPlayed: Hours(seconds),
            FirstActivity: first,
            LastActivity: last,
            DeathCauses: causes,
            ImportedFiles: await db.ImportedFiles.CountAsync(ct)));
    }

    private static async Task<Results<Ok<PagedResult<PlayerSummary>>, ValidationProblem>> ListPlayers(
        StatsDbContext db, CancellationToken ct, string? search = null, int page = 1, int pageSize = 20)
    {
        if (PagingErrors(page, pageSize) is { } errors) return TypedResults.ValidationProblem(errors);

        var query = db.Players.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => EF.Functions.Like(p.Name, $"%{search.Trim()}%"));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(p => p.LastSeen)
            .ThenBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Name,
                Kills = db.Deaths.Count(d => d.KillerId == p.Id),
                Deaths = p.Deaths.Count,
                Seconds = p.Sessions.Sum(s => (long)s.Seconds),
                p.LastSeen,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new PlayerSummary(r.Id, r.Name, r.Kills, r.Deaths, Hours(r.Seconds), r.LastSeen)).ToList();
        return TypedResults.Ok(new PagedResult<PlayerSummary>(items, page, pageSize, total));
    }

    private static async Task<Results<Ok<PlayerDetail>, NotFound>> GetPlayer(string id, StatsDbContext db, CancellationToken ct)
    {
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (player is null) return TypedResults.NotFound();

        var sessions = await db.Sessions.Where(s => s.PlayerId == id).ToListAsync(ct);
        var kills = await db.Deaths.CountAsync(d => d.KillerId == id, ct);
        var deaths = await db.Deaths.CountAsync(d => d.PlayerId == id, ct);
        var recent = await ToDeathInfo(db, db.Deaths
                .Where(d => d.PlayerId == id)
                .OrderByDescending(d => d.Time)
                .Take(10))
            .ToListAsync(ct);

        return TypedResults.Ok(new PlayerDetail(
            player.Id,
            player.Name,
            player.FirstSeen,
            player.LastSeen,
            sessions.Count,
            Hours(sessions.Sum(s => (long)s.Seconds)),
            kills,
            deaths,
            Math.Round((double)kills / Math.Max(deaths, 1), 2),
            recent));
    }

    private static async Task<Results<Ok<List<LeaderboardEntry>>, ValidationProblem>> GetLeaderboard(
        StatsDbContext db, CancellationToken ct, string by = "kills", int top = 10)
    {
        if (top is < 1 or > MaxPageSize)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["top"] = [$"Use um valor entre 1 e {MaxPageSize}."],
            });

        var values = by.ToLowerInvariant() switch
        {
            "kills" => db.Deaths.Where(d => d.KillerId != null)
                .GroupBy(d => d.KillerId!)
                .Select(g => new { PlayerId = g.Key, Value = (double)g.Count() }),
            "deaths" => db.Deaths
                .GroupBy(d => d.PlayerId)
                .Select(g => new { PlayerId = g.Key, Value = (double)g.Count() }),
            "playtime" => db.Sessions
                .GroupBy(s => s.PlayerId)
                .Select(g => new { PlayerId = g.Key, Value = g.Sum(s => (double)s.Seconds) / 3600 }),
            _ => null,
        };
        if (values is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["by"] = ["Use kills, deaths ou playtime."],
            });

        var rows = await values
            .OrderByDescending(v => v.Value)
            .Take(top)
            .Join(db.Players, v => v.PlayerId, p => p.Id, (v, p) => new { v.PlayerId, p.Name, v.Value })
            .ToListAsync(ct);

        var entries = rows
            .OrderByDescending(r => r.Value)
            .Select((r, i) => new LeaderboardEntry(i + 1, r.PlayerId, r.Name, Math.Round(r.Value, 2)))
            .ToList();
        return TypedResults.Ok(entries);
    }

    private static async Task<Results<Ok<PagedResult<DeathInfo>>, ValidationProblem>> ListDeaths(
        StatsDbContext db, CancellationToken ct,
        string? cause = null, string? playerId = null, DateTime? from = null, DateTime? to = null,
        int page = 1, int pageSize = 20)
    {
        if (PagingErrors(page, pageSize) is { } errors) return TypedResults.ValidationProblem(errors);

        IQueryable<Death> query = db.Deaths;
        if (!string.IsNullOrWhiteSpace(cause)) query = query.Where(d => d.Cause == cause);
        if (!string.IsNullOrWhiteSpace(playerId)) query = query.Where(d => d.PlayerId == playerId || d.KillerId == playerId);
        if (from is not null) query = query.Where(d => d.Time >= from);
        if (to is not null) query = query.Where(d => d.Time <= to);

        var total = await query.CountAsync(ct);
        var slice = query
            .OrderByDescending(d => d.Time)
            .ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);
        var items = await ToDeathInfo(db, slice).ToListAsync(ct);
        return TypedResults.Ok(new PagedResult<DeathInfo>(items, page, pageSize, total));
    }

    // Filtro, ordem e paginação vão na entidade; o DTO só é montado no fim
    // (o EF Core não traduz pra SQL um Where feito depois do construtor do record).
    private static IQueryable<DeathInfo> ToDeathInfo(StatsDbContext db, IQueryable<Death> deaths) =>
        from d in deaths
        join p in db.Players on d.PlayerId equals p.Id
        join k in db.Players on d.KillerId equals k.Id into killers
        from k in killers.DefaultIfEmpty()
        select new DeathInfo(d.Id, d.Time, d.PlayerId, p.Name, d.Cause, d.KillerId, k == null ? null : k.Name, d.Weapon, d.Distance);

    private static Dictionary<string, string[]>? PagingErrors(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = ["A página começa em 1."];
        if (pageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"Use um valor entre 1 e {MaxPageSize}."];
        return errors.Count > 0 ? errors : null;
    }

    private static double Hours(long seconds) => Math.Round(seconds / 3600.0, 2);
}
