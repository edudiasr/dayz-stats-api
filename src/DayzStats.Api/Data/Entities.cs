namespace DayzStats.Api.Data;

public class Player
{
    public string Id { get; set; } = "";  // id que o DayZ escreve no log
    public string Name { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    public List<Session> Sessions { get; set; } = new();
    public List<Death> Deaths { get; set; } = new();
}

public class Session
{
    public int Id { get; set; }
    public string PlayerId { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int Seconds { get; set; }
}

public class Death
{
    public int Id { get; set; }
    public string PlayerId { get; set; } = "";
    public DateTime Time { get; set; }
    public string Cause { get; set; } = "";
    public string? KillerId { get; set; }  // preenchido só em kill PvP
    public string? Weapon { get; set; }
    public double? Distance { get; set; }
    public bool ByAi { get; set; }
}

public class ImportedFile
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public int Lines { get; set; }
}
