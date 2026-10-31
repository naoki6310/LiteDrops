using System.Text.Json.Nodes;

namespace LiteDrops.Twitch;

/// <summary>Persisted GQL operations used by the Twitch site and apps.</summary>
public static class Gql
{
    static JsonObject Persisted(string name, string sha256, JsonObject variables) => new()
    {
        ["operationName"] = name,
        ["variables"] = variables,
        ["extensions"] = new JsonObject
        {
            ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = sha256 },
        },
    };

    /// <summary>Campaigns the user has progress in, plus claimed rewards.</summary>
    public static JsonObject Inventory() => Persisted(
        "Inventory", "8337eb8541b314040b0edde0c09c5c7a2783ba1960aa9edfbf3bac16d0fec404",
        new() { ["fetchRewardCampaigns"] = false });

    /// <summary>Progress of the drop currently being earned on a channel.</summary>
    public static JsonObject CurrentDrop(string channelId) => Persisted(
        "DropCurrentSessionContext", "4d06b702d25d652afb9ef835d2a550031f1cf762b193523a92166f40ea3d142b",
        new() { ["channelID"] = channelId, ["channelLogin"] = "" });

    public static JsonObject ClaimDrop(string dropInstanceId) => Persisted(
        "DropsPage_ClaimDropRewards", "a455deea71bdc9015b78eb49f4acfbce8baa7ccbedd28e549bb025bd0f751930",
        new() { ["input"] = new JsonObject { ["dropInstanceID"] = dropInstanceId } });

    /// <summary>Campaigns that can be progressed by watching a given channel.</summary>
    public static JsonObject AvailableDrops(string channelId) => Persisted(
        "DropsHighlightService_AvailableDrops", "782dad0f032942260171d2d80a654f88bdd0c5a9dddc392e9bc92218a0f42d20",
        new() { ["channelID"] = channelId });

    /// <summary>Resolves a game name to its directory slug (ex. "Overwatch" -> "overwatch-2").</summary>
    public static JsonObject GameSlug(string gameName) => Persisted(
        "DirectoryGameRedirect", "1f0300090caceec51f33c5e20647aceff9017f740f223c3c532ba6fa59f6b6cc",
        new() { ["name"] = gameName });

    /// <summary>Live streams of a game, limited to the ones with drops enabled.</summary>
    public static JsonObject DropsStreams(string slug, int limit) => Persisted(
        "DirectoryPage_Game", "86bcceb4e8b1a51256ff8eed8bd8aae4acacf80d737efe904f84f3aeadf8cafd",
        new()
        {
            ["limit"] = limit,
            ["slug"] = slug,
            ["imageWidth"] = 50,
            ["includeCostreaming"] = false,
            ["sortTypeIsRecency"] = false,
            ["options"] = new JsonObject
            {
                ["broadcasterLanguages"] = new JsonArray(),
                ["freeformTags"] = null,
                ["includeRestricted"] = new JsonArray("SUB_ONLY_LIVE"),
                ["recommendationsContext"] = new JsonObject { ["platform"] = "web" },
                ["sort"] = "RELEVANCE",
                ["systemFilters"] = new JsonArray("DROPS_ENABLED"),
                ["tags"] = new JsonArray(),
                ["requestID"] = "JIRA-VXP-2397",
            },
        });

    /// <summary>
    /// Details AvailableDrops leaves out for the campaigns on a channel: the reward kinds
    /// (item, badge, emote) and, optionally, whether the game account is linked.
    /// </summary>
    public static JsonObject CampaignDetails(string channelId, bool withAccountLink) => new()
    {
        ["query"] = "query($channelID: ID!) { channel(id: $channelID) { id viewerDropCampaigns { id " +
                    (withAccountLink ? "accountLinkURL self { isAccountConnected } " : "") +
                    "timeBasedDrops { id benefitEdges { benefit { id distributionType } } } } } }",
        ["variables"] = new JsonObject { ["channelID"] = channelId },
    };

    /// <summary>
    /// Every campaign of the user's drops page. Unlike AvailableDrops it doesn't depend on Twitch
    /// listing the campaign on the channels checked. Only the Android app client gets data from it.
    /// </summary>
    public static JsonObject DropDashboard() => Persisted(
        "ViewerDropsDashboard", "c16bb890cc8ce7647a96ee69cd313d423a378a3dedadf630a1017cde18975feb",
        new() { ["fetchRewardCampaigns"] = false });

    /// <summary>The drops of one campaign and the channels it is limited to.</summary>
    public static JsonObject CampaignDrops(string userId, string campaignId) => Persisted(
        "DropCampaignDetails", "039277bf98f3130929262cc7c6efd9c141ca3749cb6dca442fc8ead9a53f77c1",
        new() { ["channelLogin"] = userId, ["dropID"] = campaignId });

    public static JsonObject LiveChannels(IEnumerable<string> channelIds) => new()
    {
        ["query"] = "query($ids: [ID!]!) { users(ids: $ids) { id login displayName stream { id viewersCount game { id } } } }",
        ["variables"] = new JsonObject { ["ids"] = new JsonArray(channelIds.Select(i => (JsonNode)i).ToArray()) },
    };

    public static JsonObject PlaybackAccessToken(string login) => Persisted(
        "PlaybackAccessToken", "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9",
        new()
        {
            ["isLive"] = true,
            ["isVod"] = false,
            ["login"] = login,
            ["platform"] = "web",
            ["playerType"] = "site",
            ["vodID"] = "",
        });
}
