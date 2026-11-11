using DayzStats.Api.Parsing;

namespace DayzStats.Api.Tests;

public class AdmParserTests
{
    private static ParsedLog Parse(string text) => AdmParser.Parse(new StringReader(text));

    private const string Header = "AdminLog started on 2026-11-08 at 10:00:00\n";

    [Fact]
    public void PvpKill_RecordsKillerWeaponAndDistance()
    {
        var log = Parse(Header +
            "10:05:00 | Player \"Vitima\" (DEAD) (id=v1= pos=<1, 2, 3>) killed by Player \"Atirador\" (id=a1= pos=<4, 5, 6>) with M4-A1 from 245.3 meters \n");

        var death = Assert.Single(log.Deaths);
        Assert.Equal("v1=", death.PlayerId);
        Assert.Equal("a1=", death.KillerId);
        Assert.Equal("M4-A1", death.Weapon);
        Assert.Equal(245.3, death.Distance);
        Assert.Equal("jogador", death.Cause);
        Assert.Equal(new DateTime(2026, 11, 8, 10, 5, 0), death.Time);
        Assert.Equal("Atirador", log.Players["a1="]);
    }

    [Fact]
    public void AiKill_HasNoKillerButKeepsWeapon()
    {
        var log = Parse(Header +
            "12:56:43 | Player \"P\" (DEAD) (id=p= pos=<1, 2, 3>) killed by AI \"Manua\" (group=1 faction=\"West\" pos=<4, 5, 6>) with AUR A1 from 41.0918 meters \n");

        var death = Assert.Single(log.Deaths);
        Assert.True(death.ByAi);
        Assert.Null(death.KillerId);
        Assert.Equal("AUR A1", death.Weapon);
        Assert.Equal("IA", death.Cause);
        Assert.Single(log.Players);
    }

    [Fact]
    public void SameDeathOnThreeLines_CountsOnce_AndKilledByWinsOverTranslatedName()
    {
        var log = Parse(Header +
            "10:00:00 | Player \"A\" (DEAD) (id=a=)[HP: 0] hit by Urso-pardo into Head(0) for 30 damage (MeleeBearShock)\n" +
            "10:00:00 | Player \"A\" (DEAD) (id=a=) killed by Animal_UrsusArctos\n" +
            "10:00:01 | Player \"A\" (DEAD) (id=a=) died. Stats> Water: 1 Energy: 1 Bleed sources: 0\n");

        var death = Assert.Single(log.Deaths);
        Assert.Equal("animais", death.Cause);
    }

    [Fact]
    public void PlayerStaysDeadUntilReconnect()
    {
        var log = Parse(Header +
            "10:00:00 | Player \"A\" (DEAD) (id=a=) died. Stats> Water: 1\n" +
            "10:30:00 | Player \"A\" (DEAD) (id=a=)[HP: 0] hit by Infectado into Torso(1) for 5 damage (MeleeInfected)\n" +
            "10:31:00 | Player \"A\" (id=a=) is connected\n" +
            "10:40:00 | Player \"A\" (id=a=) committed suicide\n");

        Assert.Equal(["infectados", "suicídio"], log.Deaths.Select(d => d.Cause));
    }

    [Theory]
    [InlineData("hit by FallDamageHealth", "queda")]
    [InlineData("hit by OffroadHatchback with TransportHit", "veículo")]
    [InlineData("hit by Infectado into Torso(1) for 5 damage (MeleeInfected)", "infectados")]
    [InlineData("hit by AI \"X\" (group=1) into Torso(1) for 5 damage (Bullet)", "IA")]
    public void DeathFromHit_TakesCauseFromSource(string hit, string cause)
    {
        var log = Parse(Header + $"10:00:00 | Player \"A\" (DEAD) (id=a=)[HP: 0] {hit}\n");
        Assert.Equal(cause, Assert.Single(log.Deaths).Cause);
    }

    [Fact]
    public void Sessions_AreMatchedById_EvenWhenNameChanges()
    {
        var log = Parse(Header +
            "10:00:00 | Player \"Lobo\" (id=L= pos=<1, 2, 3>) is connected\n" +
            "10:30:00 | Player \"Lobo (2)\" (id=L= pos=<1, 2, 3>) has been disconnected\n");

        var session = Assert.Single(log.Sessions);
        Assert.Equal(TimeSpan.FromMinutes(30), session.End - session.Start);
        Assert.Equal("Lobo", log.Players["L="]);
    }

    [Fact]
    public void Midnight_AdvancesTheDate_AndRestartClosesSessions()
    {
        var log = Parse(
            "AdminLog started on 2026-11-08 at 23:40:00\n" +
            "23:50:00 | Player \"A\" (id=a=) is connected\n" +
            "00:20:00 | Player \"B\" (id=b=) is connecting\n" +
            "AdminLog started on 2026-11-09 at 00:30:00\n" +
            "00:40:00 | Player \"A\" (id=a=) is connected\n" +
            "00:50:00 | Player \"A\" (id=a=) has been disconnected\n");

        Assert.Equal(2, log.Sessions.Count);
        Assert.Equal(new DateTime(2026, 11, 9, 0, 20, 0), log.Sessions[0].End);
        Assert.Equal(new DateTime(2026, 11, 9, 0, 50, 0), log.Sessions[1].End);
    }

    [Fact]
    public void AiModAndChatLines_AreIgnored()
    {
        var log = Parse(Header +
            "10:00:01 | AI \"Eva\" (group=73) turned hostile to attacker Player \"A\" (id=a= pos=<1, 2, 3>)\n" +
            "10:00:02 | [AlgumMod] configuração carregada\n" +
            "10:00:03 | Chat(\"A\"(id=a=)): salve\n" +
            "10:00:04 | Player \"A\" (id=a=) is unconscious\n");

        Assert.Empty(log.Deaths);
        Assert.Equal(0, log.Unrecognized);
    }
}
