using System.Text.Json;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Limits;
using ClaudeWidget.Core.StatusLine;

namespace ClaudeWidget.Tests.Limits;

public sealed class ServerLimitsParseTests
{
    private const long Now = 1_795_434_000_000;

    // Odpowiedź /api/oauth/usage z konta Max 20x (skrócona do pól, które czytamy): bez okna 5 h,
    // z tygodniowym limitem Fable w liście „limits”.
    private const string MaxPlanResponse = """
        {
          "five_hour": null,
          "seven_day": { "utilization": 35.0, "resets_at": "2026-11-27T06:59:59.563842+00:00" },
          "seven_day_opus": null,
          "seven_day_sonnet": null,
          "limits": [
            { "kind": "weekly_all", "group": "weekly", "percent": 35, "severity": "normal",
              "resets_at": "2026-11-27T06:59:59.563842+00:00", "scope": null, "is_active": true },
            { "kind": "weekly_scoped", "group": "weekly", "percent": 0, "severity": "normal",
              "resets_at": "2026-11-27T07:00:00+00:00",
              "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }, "is_active": false }
          ]
        }
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Max_plan_has_no_five_hour_window_and_a_fable_limit()
    {
        var server = OAuthUsage.ParseServer(Json(MaxPlanResponse), Now)!;

        Assert.False(server.HasFiveHour);
        Assert.Equal(Now, server.FetchedAt);
        // Nieaktywny limit (is_active: false) też się liczy — 0% zużycia to 100% zostało, jak w Orce.
        var fable = Assert.Single(server.Scoped!);
        Assert.Equal("Fable", fable.Label);
        Assert.Equal("weekly", fable.Group);
        Assert.Equal(0, fable.Pct);
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T07:00:00Z").ToUnixTimeSeconds(), fable.ResetsAt);
    }

    [Fact]
    public void Five_hour_window_in_the_response_is_evidence_of_the_plan()
    {
        var server = OAuthUsage.ParseServer(Json("""{ "five_hour": { "utilization": 0, "resets_at": null }, "seven_day": { "utilization": 3 } }"""), Now)!;
        Assert.True(server.HasFiveHour);
        Assert.Null(server.Scoped);
    }

    [Fact]
    public void Unscoped_session_entry_also_counts_as_five_hour_window()
    {
        var server = OAuthUsage.ParseServer(Json("""
            { "five_hour": null, "limits": [ { "group": "session", "percent": 12, "scope": null } ] }
            """), Now)!;
        Assert.True(server.HasFiveHour);
    }

    [Fact]
    public void Unknown_unscoped_group_says_nothing_about_five_hour_window()
    {
        var server = OAuthUsage.ParseServer(Json("""
            { "five_hour": null, "limits": [ { "group": "monthly", "kind": "monthly_all", "percent": 12, "scope": null } ] }
            """), Now)!;
        Assert.False(server.HasFiveHour);
    }

    [Fact]
    public void Without_limits_list_falls_back_to_legacy_model_fields()
    {
        var server = OAuthUsage.ParseServer(Json("""
            { "five_hour": { "utilization": 10 }, "seven_day": { "utilization": 20 },
              "seven_day_opus": { "utilization": 55.5, "resets_at": 1795700000 }, "seven_day_sonnet": null }
            """), Now)!;

        var opus = Assert.Single(server.Scoped!);
        Assert.Equal("Opus", opus.Label);
        Assert.Equal(55.5, opus.Pct);
        Assert.Equal(1795700000, opus.ResetsAt);
    }

    [Fact]
    public void Odd_entries_are_skipped_and_percent_is_clamped()
    {
        var server = OAuthUsage.ParseServer(Json("""
            { "limits": [ 7, { "group": "weekly", "percent": 140, "scope": { "model": { "display_name": "Fable" } } },
                          { "group": "weekly", "percent": 1, "scope": { "model": { "display_name": "Fable" } } },
                          { "group": "weekly", "percent": 5, "scope": { "model": { "display_name": "  " } } } ] }
            """), Now)!;

        var fable = Assert.Single(server.Scoped!);
        Assert.Equal(100, fable.Pct);
    }
}

public sealed class LimitRowsTests
{
    private const long Now = 1_795_434_000_000;
    private const long Hour = 3_600_000;

    private static AccountLimits Limits(double? fiveHour, double week) => new()
    {
        FiveHour = fiveHour is double pct ? new LimitWindow { Pct = pct, ResetsAt = (Now + 2 * Hour) / 1000.0 } : null,
        SevenDay = new LimitWindow { Pct = week, ResetsAt = (Now + 72 * Hour) / 1000.0 },
        UpdatedAt = Now - 60_000,
    };

    private static ServerLimits Server(bool hasFiveHour, params ScopedLimit[] scoped) => new()
    {
        HasFiveHour = hasFiveHour,
        Scoped = scoped.Length > 0 ? [.. scoped] : null,
        FetchedAt = Now - 120_000,
    };

    private static readonly ScopedLimit Fable = new() { Label = "Fable", Group = "weekly", Pct = 0, ResetsAt = (Now + 72 * Hour) / 1000.0 };

    [Fact]
    public void Without_any_reading_shows_five_hour_and_week_placeholders()
    {
        var rows = LimitRows.Build(null, null, Now, panel: false);
        Assert.Equal([LimitRows.FiveHourKey, LimitRows.SevenDayKey], rows.Select(r => r.Key));
        Assert.All(rows, row => Assert.Null(row.View.Pct));
    }

    [Fact]
    public void Plan_without_five_hour_window_hides_it_and_shows_model_limits()
    {
        var rows = LimitRows.Build(Limits(null, 35), Server(false, Fable), Now, panel: true);

        Assert.Equal([LimitRows.SevenDayKey, "model:Fable:weekly"], rows.Select(r => r.Key));
        Assert.Equal(35, rows[0].View.Pct);
        Assert.Equal("Fable", rows[1].CardLabel);
        Assert.Equal("Fable · tydzień", rows[1].PanelLabel);
        Assert.Equal(0, rows[1].View.Pct);
        Assert.False(rows[1].IsAccountWide);
    }

    [Fact]
    public void Five_hour_stays_until_the_server_says_the_plan_has_none()
    {
        // Odczyt tylko ze statusline (bez serwera) — 5 h z kreską, jak dotąd.
        Assert.Contains(LimitRows.Build(Limits(null, 35), null, Now, false), r => r.Key == LimitRows.FiveHourKey);
        // Serwer zna okno 5 h, choć statusline go nie podała.
        Assert.Contains(LimitRows.Build(Limits(null, 35), Server(true), Now, false), r => r.Key == LimitRows.FiveHourKey);
        // Statusline podała 5 h — pokazujemy, cokolwiek mówił starszy odczyt serwera.
        var rows = LimitRows.Build(Limits(23, 35), Server(false), Now, false);
        Assert.Equal(23, rows.Single(r => r.Key == LimitRows.FiveHourKey).View.Pct);
    }

    [Fact]
    public void Model_limit_survives_a_statusline_write()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        JsonStore.Write(paths.ServerLimitsFile, Server(false, Fable), StateJson.Default.ServerLimits);

        using var input = JsonDocument.Parse("""
            { "session_id": "s1", "rate_limits": { "seven_day": { "used_percentage": 41, "resets_at": 1795700000 } } }
            """);
        StatusLineEngine.Record(paths, input.RootElement, Now);

        var limits = JsonStore.Read(paths.LimitsFile, StateJson.Default.AccountLimits);
        var server = JsonStore.Read(paths.ServerLimitsFile, StateJson.Default.ServerLimits);
        var rows = LimitRows.Build(limits, server, Now, panel: false);
        Assert.Equal([LimitRows.SevenDayKey, "model:Fable:weekly"], rows.Select(r => r.Key));
        Assert.Equal(41, rows[0].View.Pct);
    }

    [Fact]
    public void Exhausted_model_limit_does_not_turn_the_border_red()
    {
        var fableOut = Fable with { Pct = 100 };
        var rows = LimitRows.Build(Limits(null, 35), Server(false, fableOut), Now, panel: false);

        Assert.Equal("#FF5A4E", rows[1].View.Color);
        Assert.Null(LimitRows.Warning(rows));
        Assert.Equal("#FF5A4E", LimitRows.Warning(LimitRows.Build(Limits(null, 95), Server(false), Now, false)));
    }

    [Fact]
    public void Model_limit_with_unknown_window_gets_no_pace_forecast()
    {
        var odd = new ScopedLimit { Label = "Nowy", Group = "monthly", Pct = 80, ResetsAt = (Now + 72 * Hour) / 1000.0 };
        var row = LimitRows.Build(Limits(null, 1), Server(false, odd), Now, panel: true).Last();

        Assert.Equal("Nowy", row.PanelLabel);
        Assert.Equal(80, row.View.Pct);
        Assert.StartsWith("reset ", row.View.Note);
    }
}

public sealed class StaleModelLimitTests
{
    private const long Now = 1_795_434_000_000;
    private const long Minute = 60_000;

    private static IReadOnlyList<LimitRow> Rows(long fetchedAgo, bool panel)
    {
        var limits = new AccountLimits { SevenDay = new LimitWindow { Pct = 35 }, UpdatedAt = Now - Minute };
        var server = new ServerLimits
        {
            Scoped = [new ScopedLimit { Label = "Fable", Group = "weekly", Pct = 10, ResetsAt = (Now + 3 * 86_400_000L) / 1000.0 }],
            FetchedAt = Now - fetchedAgo,
        };
        return LimitRows.Build(limits, server, Now, panel);
    }

    [Fact]
    public void Old_server_reading_says_when_the_model_limit_was_measured()
    {
        Assert.StartsWith("reset ", Rows(14 * Minute, panel: false)[1].View.Note);

        var card = Rows(15 * Minute, panel: false)[1].View.Note;
        var panel = Rows(15 * Minute, panel: true)[1].View.Note;
        Assert.StartsWith("stan z ", card);
        Assert.DoesNotContain("reset", card);
        Assert.StartsWith("stan z ", panel);
        Assert.Contains(" · reset ", panel);
    }
}
