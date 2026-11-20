using System.Text.Json;
using ClaudeWidget.Core.Limits;

namespace ClaudeWidget.Tests.Limits;

public sealed class OAuthUsageTests
{
    private const long Now = 1_795_000_000_000;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Parses_endpoint_response_into_statusline_shape()
    {
        // Kształt odpowiedzi /api/oauth/usage: utilization 0–100, resets_at jako data ISO.
        var limits = OAuthUsage.ParseResponse(Json("""
            {
              "five_hour": { "utilization": 37.0, "resets_at": "2026-11-20T17:00:00.512345+00:00" },
              "seven_day": { "utilization": 12, "resets_at": "2026-11-24T08:00:00+00:00" },
              "seven_day_opus": null,
              "extra_usage": { "is_enabled": false }
            }
            """), Now);

        Assert.NotNull(limits);
        Assert.Equal(37, limits.FiveHour!.Pct);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T17:00:00.512345+00:00").ToUnixTimeMilliseconds() / 1000.0, limits.FiveHour.ResetsAt);
        Assert.Equal(12, limits.SevenDay!.Pct);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T08:00:00Z").ToUnixTimeSeconds(), limits.SevenDay.ResetsAt);
        Assert.Equal(Now, limits.UpdatedAt);
    }

    [Fact]
    public void Accepts_statusline_field_names_too()
    {
        var limits = OAuthUsage.ParseResponse(Json("""{ "five_hour": { "used_percentage": 50, "resets_at": 1795003600 } }"""), Now);
        Assert.Equal(50, limits!.FiveHour!.Pct);
        Assert.Equal(1_795_003_600, limits.FiveHour.ResetsAt);
        Assert.Null(limits.SevenDay);
    }

    [Theory]
    [InlineData("1795003600", 1_795_003_600)]
    [InlineData("1795003600000", 1_795_003_600)]
    [InlineData("\"1795003600\"", 1_795_003_600)]
    [InlineData("\"2026-11-20T17:00:00Z\"", 1_795_194_000)]
    public void Reset_time_is_normalised_to_seconds(string raw, double expected)
    {
        Assert.Equal(expected, OAuthUsage.ResetSeconds(Json(raw)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"wkrótce\"")]
    [InlineData("0")]
    public void Unusable_reset_time_is_null(string raw)
    {
        Assert.Null(OAuthUsage.ResetSeconds(Json(raw)));
    }

    [Fact]
    public void Percentage_is_clamped()
    {
        var limits = OAuthUsage.ParseResponse(Json("""{ "five_hour": { "utilization": 104.2 }, "seven_day": { "utilization": -1 } }"""), Now);
        Assert.Equal(100, limits!.FiveHour!.Pct);
        Assert.Equal(0, limits.SevenDay!.Pct);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "five_hour": null, "seven_day": { "resets_at": "2026-11-20T17:00:00Z" } }""")]
    [InlineData("""{ "error": { "type": "authentication_error" } }""")]
    public void Response_without_any_limit_gives_null(string text)
    {
        Assert.Null(OAuthUsage.ParseResponse(Json(text), Now));
    }

    [Fact]
    public void Valid_token_is_read()
    {
        var token = OAuthUsage.ParseToken(Json($$"""{ "claudeAiOauth": { "accessToken": "sk-ant-oat01-x", "refreshToken": "r", "expiresAt": {{Now + 3_600_000}} } }"""), Now);
        Assert.Equal(TokenStatus.Valid, token.Status);
        Assert.Equal("sk-ant-oat01-x", token.AccessToken);
    }

    [Fact]
    public void Token_about_to_expire_counts_as_expired_and_is_not_exposed()
    {
        var token = OAuthUsage.ParseToken(Json($$"""{ "claudeAiOauth": { "accessToken": "sk-ant-oat01-x", "expiresAt": {{Now + 30_000}} } }"""), Now);
        Assert.Equal(TokenStatus.Expired, token.Status);
        Assert.Null(token.AccessToken);
    }

    [Fact]
    public void Token_without_expiry_is_used()
    {
        var token = OAuthUsage.ParseToken(Json("""{ "claudeAiOauth": { "accessToken": "t" } }"""), Now);
        Assert.Equal(TokenStatus.Valid, token.Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "claudeAiOauth": null }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "" } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": 5 } }""")]
    public void Missing_login_is_missing(string text)
    {
        Assert.Equal(TokenStatus.Missing, OAuthUsage.ParseToken(Json(text), Now).Status);
    }

    [Fact]
    public void Unreadable_credentials_file_is_missing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Equal(TokenStatus.Missing, OAuthUsage.ReadToken(Path.Combine(dir.FullName, ".credentials.json"), Now).Status);
            var broken = Path.Combine(dir.FullName, "broken.json");
            File.WriteAllText(broken, "{ nie json");
            Assert.Equal(TokenStatus.Missing, OAuthUsage.ReadToken(broken, Now).Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_dir_honours_CLAUDE_CONFIG_DIR()
    {
        Assert.Equal(Path.Combine(@"C:\Users\x", ".claude"), OAuthUsage.ConfigDir(null, @"C:\Users\x"));
        Assert.Equal(Path.Combine(@"C:\Users\x", ".claude"), OAuthUsage.ConfigDir("  ", @"C:\Users\x"));
        Assert.Equal(@"D:\claude", OAuthUsage.ConfigDir(@"D:\claude", @"C:\Users\x"));
    }
}
