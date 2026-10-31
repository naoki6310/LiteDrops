namespace LiteDrops.Tests;

public class HelperTests
{
    [Theory]
    [InlineData("1.5.1", "1.5.0", true)]
    [InlineData("v1.6.0", "1.5.1", true)]
    [InlineData("1.5.0", "1.5.0", false)]
    [InlineData("1.4.9", "1.5.0", false)]
    [InlineData("not-a-version", "1.5.0", false)]
    public void Update_version_comparison(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsNewer(latest, current));

    [Fact]
    public void Claim_lines_are_read_back_from_the_log()
    {
        var record = ClaimHistory.ParseLogLine("2026-10-16 21:31:00 INFO  Claimed: Rainbow Six Siege / Esports Pack");
        Assert.NotNull(record);
        Assert.Equal("Rainbow Six Siege", record.Game);
        Assert.Equal("Esports Pack", record.Drop);
        Assert.Equal(DateTimeKind.Utc, record.ClaimedAt.Kind);
    }

    [Fact]
    public void Game_names_with_colons_are_kept()
    {
        var record = ClaimHistory.ParseLogLine("2026-10-16 20:03:16 INFO  Claimed: PUBG: BATTLEGROUNDS / Bundle");
        Assert.Equal("PUBG: BATTLEGROUNDS", record!.Game);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/#access_token=abc123def456ghi789jkl012mno345&scope=&token_type=bearer", "abc123def456ghi789jkl012mno345")]
    [InlineData("  https://www.twitch.tv/?x=1#ACCESS_TOKEN=Zz9Yy8Xx7  ", "Zz9Yy8Xx7")]
    [InlineData("abcdefghij0123456789abcdefghij", "abcdefghij0123456789abcdefghij")]
    [InlineData("https://www.twitch.tv/", null)]
    [InlineData("", null)]
    public void Token_is_read_from_the_pasted_address(string pasted, string? expected) =>
        Assert.Equal(expected, Twitch.TwitchApi.ParseToken(pasted));

    [Fact]
    public void Client_can_be_created_with_the_app_user_agent()
    {
        using var api = new Twitch.TwitchApi("device");
        Assert.StartsWith("Dalvik/", string.Join(" ", api.Http.DefaultRequestHeaders.GetValues("User-Agent")));
    }

    [Fact]
    public void Authorize_url_uses_the_redirect_the_app_client_accepts()
    {
        var url = new Uri(Twitch.TwitchApi.AuthorizeUrl);
        Assert.Equal("id.twitch.tv", url.Host);
        Assert.Contains("response_type=token", url.Query);
        Assert.Contains("redirect_uri=https%3A%2F%2Fwww.twitch.tv%2F", url.Query);
    }

    [Fact]
    public void Spade_events_are_a_base64_json_array()
    {
        var data = Twitch.TwitchApi.EncodeEvent("minute-watched", new() { ["channel_id"] = "12826", ["live"] = true });
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data));
        Assert.Equal("""[{"event":"minute-watched","properties":{"channel_id":"12826","live":true}}]""", json);
    }

    [Theory]
    [InlineData("2026-10-16 21:31:11 INFO  Found 3 campaign(s), 1 earnable")]
    [InlineData("2026-10-16 21:31:11 WARN  Claim failed for Esports Pack: no status")]
    public void Other_lines_are_ignored(string line) => Assert.Null(ClaimHistory.ParseLogLine(line));
}
