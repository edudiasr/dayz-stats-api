using System.Globalization;
using System.Text.RegularExpressions;

namespace DayzStats.Api.Parsing;

public sealed record ParsedSession(string PlayerId, DateTime Start, DateTime End);

public sealed record ParsedDeath(
    string PlayerId,
    DateTime Time,
    string Cause,
    string? KillerId,
    string? Weapon,
    double? Distance,
    bool ByAi);

public sealed class ParsedLog
{
    public Dictionary<string, string> Players { get; } = new();  // id -> nome
    public List<ParsedSession> Sessions { get; } = new();
    public List<ParsedDeath> Deaths { get; } = new();
    public int Lines { get; set; }
    public int Unrecognized { get; set; }
}

/// <summary>
/// Lê um .ADM (log de administração do servidor de DayZ).
/// Jogador é identificado pelo id do log: vários podem se chamar "Survivor".
/// </summary>
public sealed partial class AdmParser
{
    [GeneratedRegex(@"^AdminLog started on (\d{4})-(\d{2})-(\d{2}) at (\d{2}):(\d{2}):(\d{2})")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^(\d{2}):(\d{2}):(\d{2}) \| (.*)$")]
    private static partial Regex LineRegex();

    // Player "Nome" (DEAD) (id=XXXX= pos=<...>)
    [GeneratedRegex(@"^Player ""(?<name>[^""]*)""(?: \(DEAD\))? \(id=(?<id>[^ )]+)")]
    private static partial Regex SubjectRegex();

    [GeneratedRegex(@"killed by Player ""(?<name>[^""]*)""(?: \(DEAD\))? \(id=(?<id>[^ )]+)[^)]*\)(?: with (?<weapon>.+?))?(?: from (?<dist>[\d.]+) meters)?\s*$")]
    private static partial Regex PlayerKillRegex();

    [GeneratedRegex(@"killed by AI ""[^""]*"" \([^)]*\)(?: with (?<weapon>.+?))?(?: from (?<dist>[\d.]+) meters)?\s*$")]
    private static partial Regex AiKillRegex();

    [GeneratedRegex(@"killed by (?<killer>[^ (]+)")]
    private static partial Regex OtherKillRegex();

    [GeneratedRegex(@"hit by (?<source>.*)$")]
    private static partial Regex HitRegex();

    private readonly ParsedLog _log = new();
    private readonly Dictionary<string, DateTime> _online = new();
    private readonly Dictionary<string, DeathBuilder> _dead = new();
    private readonly List<DeathBuilder> _deaths = new();

    private DateTime _date;
    private TimeSpan? _previousClock;
    private DateTime _lastTime;

    public static ParsedLog Parse(TextReader reader)
    {
        var parser = new AdmParser();
        string? line;
        while ((line = reader.ReadLine()) is not null)
            parser.Handle(line);
        return parser.Finish();
    }

    private ParsedLog Finish()
    {
        CloseSessions(_lastTime);
        foreach (var d in _deaths)
            _log.Deaths.Add(new ParsedDeath(d.PlayerId, d.Time, d.Cause ?? "desconhecida", d.KillerId, d.Weapon, d.Distance, d.ByAi));
        return _log;
    }

    private void Handle(string line)
    {
        _log.Lines++;
        if (line.Length == 0 || line[0] == '*') return;

        var header = HeaderRegex().Match(line);
        if (header.Success)
        {
            CloseSessions(_lastTime);  // restart: quem estava online caiu junto
            _date = new DateTime(Int(header, 1), Int(header, 2), Int(header, 3));
            _previousClock = new TimeSpan(Int(header, 4), Int(header, 5), Int(header, 6));
            return;
        }

        var m = LineRegex().Match(line);
        if (!m.Success)
        {
            _log.Unrecognized++;
            return;
        }

        var clock = new TimeSpan(Int(m, 1), Int(m, 2), Int(m, 3));
        // o log passa da meia-noite sem avisar: 23:59 -> 00:01
        if (_previousClock is { } previous && clock + TimeSpan.FromHours(1) < previous)
            _date = _date.AddDays(1);
        _previousClock = clock;
        var time = _date + clock;
        _lastTime = time;

        var text = m.Groups[4].Value;
        if (text.StartsWith("AI \"") || text.StartsWith('[') || text.StartsWith("Chat("))
            return;  // eventos de IA, mensagens de mods e chat não entram nas estatísticas

        var subject = SubjectRegex().Match(text);
        if (!subject.Success)
        {
            _log.Unrecognized++;
            return;
        }

        var id = subject.Groups["id"].Value;
        _log.Players.TryAdd(id, subject.Groups["name"].Value);
        HandlePlayerEvent(text, id, time);
    }

    private void HandlePlayerEvent(string text, string id, DateTime time)
    {
        Match k;
        if ((k = PlayerKillRegex().Match(text)).Success)
        {
            var killerId = k.Groups["id"].Value;
            _log.Players.TryAdd(killerId, k.Groups["name"].Value);
            AddDeath(id, time, "jogador", fromHit: false, killerId, Weapon(k), Distance(k), byAi: false);
        }
        else if ((k = AiKillRegex().Match(text)).Success)
        {
            AddDeath(id, time, "IA", fromHit: false, null, Weapon(k), Distance(k), byAi: true);
        }
        else if ((k = HitRegex().Match(text)).Success)
        {
            var before = text[..k.Index];
            if (before.Contains("[HP: 0]") || before.Contains("(DEAD)"))
                AddDeath(id, time, CauseFromHit(k.Groups["source"].Value), fromHit: true);
        }
        else if (text.Contains(" is connected"))
        {
            _dead.Remove(id);  // conectou de novo = personagem novo depois de morrer
            _online.TryAdd(id, time);
        }
        else if (text.Contains("has been disconnected"))
        {
            if (_online.Remove(id, out var since))
                _log.Sessions.Add(new ParsedSession(id, since, time));
        }
        else if (text.Contains("committed suicide"))
        {
            AddDeath(id, time, "suicídio", fromHit: false);
        }
        else if (text.Contains("bled out"))
        {
            AddDeath(id, time, "sangramento", fromHit: false);
        }
        else if ((k = OtherKillRegex().Match(text)).Success)
        {
            AddDeath(id, time, CauseFromKiller(k.Groups["killer"].Value), fromHit: false);
        }
        else if (text.Contains(" died."))
        {
            AddDeath(id, time, null, fromHit: false);
        }
        else if (!text.Contains(" is connecting") && !text.Contains("unconscious") &&
                 !text.Contains("regained consciousness") && !text.Contains("is choosing to respawn"))
        {
            _log.Unrecognized++;
        }
    }

    // A mesma morte aparece em até 3 linhas ("hit by ... [HP: 0]", "killed by ...", "died. Stats>").
    // Depois da primeira o jogador fica marcado como morto até conectar de novo, e as outras só completam
    // os dados. A causa de um "killed by" vale mais que a deduzida do dano (que vem com nome traduzido).
    private void AddDeath(string id, DateTime time, string? cause, bool fromHit,
                          string? killerId = null, string? weapon = null, double? distance = null, bool byAi = false)
    {
        if (_dead.TryGetValue(id, out var existing))
        {
            if (cause is not null && (existing.Cause is null || (existing.CauseFromHit && !fromHit)))
            {
                existing.Cause = cause;
                existing.CauseFromHit = fromHit;
            }
            existing.KillerId ??= killerId;
            existing.Weapon ??= weapon;
            existing.Distance ??= distance;
            existing.ByAi |= byAi;
            return;
        }

        var death = new DeathBuilder
        {
            PlayerId = id, Time = time, Cause = cause, CauseFromHit = fromHit,
            KillerId = killerId, Weapon = weapon, Distance = distance, ByAi = byAi,
        };
        _dead[id] = death;
        _deaths.Add(death);
    }

    private void CloseSessions(DateTime at)
    {
        foreach (var (id, since) in _online)
            _log.Sessions.Add(new ParsedSession(id, since, at));
        _online.Clear();
    }

    private static string CauseFromHit(string source)
    {
        if (source.StartsWith("Player \"")) return "jogador";
        if (source.StartsWith("AI \"")) return "IA";
        if (source.Contains("TransportHit")) return "veículo";
        var token = source.Split(' ', '(')[0];
        if (token.StartsWith("FallDamage")) return "queda";
        if (token == "Infectado" || token.StartsWith("Zmb")) return "infectados";
        if (token.StartsWith("Animal_")) return "animais";
        return token.Length == 0 ? "desconhecida" : token;
    }

    private static string CauseFromKiller(string killer) =>
        killer.StartsWith("Zmb") ? "infectados" :
        killer.StartsWith("Animal_") ? "animais" :
        killer;

    private static string? Weapon(Match m) =>
        m.Groups["weapon"].Success ? m.Groups["weapon"].Value.Trim() : null;

    // InvariantCulture: em pt-BR o "." do log seria lido como separador de milhar
    private static double? Distance(Match m) =>
        m.Groups["dist"].Success && double.TryParse(m.Groups["dist"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].ValueSpan, CultureInfo.InvariantCulture);

    private sealed class DeathBuilder
    {
        public required string PlayerId { get; init; }
        public required DateTime Time { get; init; }
        public string? Cause { get; set; }
        public bool CauseFromHit { get; set; }
        public string? KillerId { get; set; }
        public string? Weapon { get; set; }
        public double? Distance { get; set; }
        public bool ByAi { get; set; }
    }
}
