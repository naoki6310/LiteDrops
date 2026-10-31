using System.Text.Json.Nodes;

namespace LiteDrops.Mining;

public sealed record ChannelRef(string Id, string Login, string DisplayName, int Viewers, string StreamId);

public sealed class Drop
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int RequiredMinutes { get; init; }
    public required DateTime StartAt { get; init; }
    public required DateTime EndAt { get; init; }
    public IReadOnlyList<string> BenefitIds { get; init; } = [];
    /// <summary>Reward kinds ("DIRECT_ENTITLEMENT", "BADGE", "EMOTE", ...), empty when unknown.</summary>
    public List<string> RewardTypes { get; init; } = [];
    public IReadOnlyList<string> PreconditionIds { get; init; } = [];
    public int CurrentMinutes { get; set; }
    public bool IsClaimed { get; set; }
    public string? InstanceId { get; set; }

    /// <summary>Subscriptions needed instead of watch time (mission-based drops).</summary>
    public int RequiredSubs { get; init; }
    /// <summary>Earned by watching; sub / mission-based drops aren't.</summary>
    public bool WatchEarnable => RequiredSubs == 0 && RequiredMinutes > 0;
    public bool IsComplete => CurrentMinutes >= RequiredMinutes;
    public bool IsActive(DateTime now) => StartAt <= now && now < EndAt;

    public static Drop Parse(JsonNode node, IReadOnlyDictionary<string, DateTime> claimedBenefits)
    {
        var benefitIds = (node["benefitEdges"] as JsonArray ?? [])
            .Select(e => (string?)e?["benefit"]?["id"])
            .OfType<string>()
            .ToList();
        var drop = new Drop
        {
            Id = (string)node["id"]!,
            Name = (string?)node["name"] ?? "?",
            RequiredMinutes = (int?)node["requiredMinutesWatched"] ?? 0,
            RequiredSubs = (int?)node["requiredSubs"] ?? 0,
            StartAt = ParseTime(node["startAt"]),
            EndAt = ParseTime(node["endAt"]),
            BenefitIds = benefitIds,
            RewardTypes = (node["benefitEdges"] as JsonArray ?? [])
                .Select(e => (string?)e?["benefit"]?["distributionType"])
                .OfType<string>()
                .ToList(),
            PreconditionIds = (node["preconditionDrops"] as JsonArray ?? [])
                .Select(p => (string?)p?["id"]).OfType<string>().ToList(),
        };
        if (node["self"] is JsonObject self)
        {
            drop.CurrentMinutes = (int?)self["currentMinutesWatched"] ?? 0;
            drop.IsClaimed = (bool?)self["isClaimed"] ?? false;
            drop.InstanceId = (string?)self["dropInstanceID"];
        }
        else if (benefitIds.Count > 0
                 && benefitIds.All(b => claimedBenefits.TryGetValue(b, out var at) && at >= drop.StartAt))
        {
            // not in the inventory, but every reward was already awarded during this drop's window
            drop.IsClaimed = true;
            drop.CurrentMinutes = drop.RequiredMinutes;
        }
        return drop;
    }

    public static DateTime ParseTime(JsonNode? node)
        => node is null ? DateTime.MaxValue : DateTime.Parse((string)node!, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
}

public sealed class Campaign
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required DateTime EndAt { get; init; }
    public required IReadOnlyList<Drop> Drops { get; init; }
    /// <summary>Whether the game account is linked to Twitch; null when unknown.</summary>
    public bool? AccountLinked { get; set; }

    /// <summary>
    /// None of the drops can be earned by watching (they need a subscription or other mission).
    /// Twitch reports such campaigns as "not linked" too, with a help article as the link page.
    /// </summary>
    public bool WatchUnearnable => Drops.Count > 0 && Drops.All(d => !d.WatchEarnable);

    /// <summary>The game account really has to be linked first.</summary>
    public bool NeedsLink => AccountLinked == false && !WatchUnearnable
                             && !(LinkUrl ?? "").Contains("help.twitch.tv", StringComparison.OrdinalIgnoreCase);
    /// <summary>Page where the game account can be linked.</summary>
    public string? LinkUrl { get; set; }
    public bool InInventory { get; init; }
    public List<ChannelRef> Channels { get; } = [];

    /// <summary>The drop that is earned next: unclaimed, active, with its preconditions met.</summary>
    public Drop? NextDrop(DateTime now) => Drops
        .Where(d => d.WatchEarnable && !d.IsClaimed && !d.IsComplete && d.IsActive(now)
                    && d.PreconditionIds.All(p => Drops.FirstOrDefault(x => x.Id == p) is not { IsComplete: false }))
        .OrderBy(d => d.RequiredMinutes - d.CurrentMinutes)
        .FirstOrDefault();

    /// <summary>
    /// True when every reward is a Twitch chat badge or emote, i.e. there's no in-game item.
    /// Unknown reward types count as items, so nothing gets skipped by mistake.
    /// </summary>
    public bool BadgeOrEmoteOnly
    {
        get
        {
            var types = Drops.SelectMany(d => d.RewardTypes).ToList();
            return types.Count > 0 && types.All(t => t is "BADGE" or "EMOTE");
        }
    }

    public bool CanEarn(DateTime now) => !NeedsLink && now < EndAt && NextDrop(now) is not null;

    public double Progress
    {
        get
        {
            var total = Drops.Sum(d => d.RequiredMinutes);
            return total == 0 ? 0 : Drops.Sum(d => Math.Min(d.CurrentMinutes, d.RequiredMinutes)) / (double)total;
        }
    }
}

public enum MinerStatus { Starting, Mining, Idle, LoginRequired, Error }

public sealed record WatchInfo(
    ChannelRef Channel, string GameName, string CampaignName, DateTime CampaignEndAt,
    string DropName, int CurrentMinutes, int RequiredMinutes);

public enum CampaignTag { None, NeedsLink, NotByWatching }

public sealed record CampaignRow(
    string GameName, string CampaignName, double Progress, bool Earnable, CampaignTag Tag, string? LinkUrl);

public sealed record MinerSnapshot(
    MinerStatus Status, string StatusText, WatchInfo? Watching, IReadOnlyList<CampaignRow> Campaigns);
