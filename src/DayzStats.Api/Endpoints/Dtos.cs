namespace DayzStats.Api.Endpoints;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record ServerStats(
    int Players,
    int Deaths,
    int PvpKills,
    int AiKills,
    double HoursPlayed,
    DateTime? FirstActivity,
    DateTime? LastActivity,
    IReadOnlyList<CauseCount> DeathCauses,
    int ImportedFiles);

public sealed record CauseCount(string Cause, int Count);

public sealed record PlayerSummary(string Id, string Name, int Kills, int Deaths, double HoursPlayed, DateTime LastSeen);

public sealed record PlayerDetail(
    string Id,
    string Name,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Sessions,
    double HoursPlayed,
    int Kills,
    int Deaths,
    double KillDeathRatio,
    IReadOnlyList<DeathInfo> RecentDeaths);

public sealed record DeathInfo(
    int Id,
    DateTime Time,
    string PlayerId,
    string PlayerName,
    string Cause,
    string? KillerId,
    string? KillerName,
    string? Weapon,
    double? Distance);

public sealed record LeaderboardEntry(int Rank, string PlayerId, string Name, double Value);
