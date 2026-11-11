using DayzStats.Api.Data;
using DayzStats.Api.Parsing;
using Microsoft.EntityFrameworkCore;

namespace DayzStats.Api.Import;

public sealed record ImportResult(int FilesImported, int FilesSkipped, int Sessions, int Deaths, int UnrecognizedLines);

public sealed class LogImporter(StatsDbContext db, TimeProvider clock, ILogger<LogImporter> logger)
{
    /// <summary>Importa todos os .ADM da pasta que ainda não foram importados.</summary>
    public async Task<ImportResult> ImportFolderAsync(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Pasta não encontrada: {folder}");

        // o nome tem data e hora (DayZServer_x64_2026-09-03_18-05-46.ADM), então ordem alfabética = cronológica
        var files = Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).Equals(".adm", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        var total = new ImportResult(0, 0, 0, 0, 0);
        foreach (var path in files)
        {
            await using var stream = File.OpenRead(path);
            total = Sum(total, await ImportAsync(Path.GetFileName(path), stream, ct));
        }
        return total;
    }

    /// <summary>Importa um .ADM. O mesmo nome de arquivo nunca é importado duas vezes.</summary>
    public async Task<ImportResult> ImportAsync(string fileName, Stream content, CancellationToken ct)
    {
        if (await db.ImportedFiles.AnyAsync(f => f.FileName == fileName, ct))
            return new ImportResult(0, 1, 0, 0, 0);

        ParsedLog log;
        using (var reader = new StreamReader(content))
            log = AdmParser.Parse(reader);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await UpsertPlayers(log, ct);

        db.Sessions.AddRange(log.Sessions.Select(s => new Session
        {
            PlayerId = s.PlayerId,
            Start = s.Start,
            End = s.End,
            Seconds = (int)(s.End - s.Start).TotalSeconds,
        }));
        db.Deaths.AddRange(log.Deaths.Select(d => new Death
        {
            PlayerId = d.PlayerId,
            Time = d.Time,
            Cause = d.Cause,
            KillerId = d.KillerId,
            Weapon = d.Weapon,
            Distance = d.Distance,
            ByAi = d.ByAi,
        }));
        db.ImportedFiles.Add(new ImportedFile
        {
            FileName = fileName,
            ImportedAt = clock.GetLocalNow().DateTime,
            Lines = log.Lines,
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("{File}: {Sessions} sessões, {Deaths} mortes, {Unrecognized} linhas não reconhecidas",
            fileName, log.Sessions.Count, log.Deaths.Count, log.Unrecognized);
        return new ImportResult(1, 0, log.Sessions.Count, log.Deaths.Count, log.Unrecognized);
    }

    private async Task UpsertPlayers(ParsedLog log, CancellationToken ct)
    {
        var ids = log.Players.Keys.ToList();
        var existing = await db.Players.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        foreach (var (id, name) in log.Players)
        {
            var times = log.Sessions.Where(s => s.PlayerId == id).SelectMany(s => new[] { s.Start, s.End })
                .Concat(log.Deaths.Where(d => d.PlayerId == id).Select(d => d.Time))
                .ToList();
            var first = times.Count > 0 ? times.Min() : DateTime.MinValue;
            var last = times.Count > 0 ? times.Max() : DateTime.MinValue;

            if (existing.TryGetValue(id, out var player))
            {
                if (times.Count == 0) continue;
                if (player.FirstSeen == DateTime.MinValue || first < player.FirstSeen) player.FirstSeen = first;
                if (last > player.LastSeen) player.LastSeen = last;
            }
            else
            {
                db.Players.Add(new Player { Id = id, Name = name, FirstSeen = first, LastSeen = last });
            }
        }
    }

    private static ImportResult Sum(ImportResult a, ImportResult b) => new(
        a.FilesImported + b.FilesImported,
        a.FilesSkipped + b.FilesSkipped,
        a.Sessions + b.Sessions,
        a.Deaths + b.Deaths,
        a.UnrecognizedLines + b.UnrecognizedLines);
}
