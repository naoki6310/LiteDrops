using System.Net;
using System.Text.Json.Nodes;
using LiteDrops.Twitch;

namespace LiteDrops.Mining;

/// <summary>
/// The mining loop: find campaigns for the selected games, pick a live channel that progresses
/// one of them, "watch" it by fetching the stream playlist, and claim finished drops.
/// </summary>
public sealed class Miner(TwitchApi api, AppSettings settings, TwitchUser user)
{
    static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(20);
    static readonly TimeSpan ProgressCheckInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan NoProgressTimeout = TimeSpan.FromMinutes(7);
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    static readonly TimeSpan PriorityCheckInterval = TimeSpan.FromMinutes(10);
    static readonly TimeSpan IdleRetry = TimeSpan.FromMinutes(5);
    static readonly TimeSpan BadChannelCooldown = TimeSpan.FromMinutes(30);
    const int StreamsPerGame = 30;

    readonly Dictionary<string, (string Id, string Slug)?> _gameCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _badChannels = [];
    List<Campaign> _campaigns = [];
    List<(string Name, string Id, string Slug)> _wantedGames = [];
    HashSet<string> _liveGameIds = [];
    bool _accountLinkQuery = true; // switched off if Twitch rejects that part of the query
    readonly HashSet<string> _loggedSkips = [];
    CancellationTokenSource _wake = new();
    MinerSnapshot _snapshot = new(MinerStatus.Starting, "Starting…", null, []);

    public event Action<MinerSnapshot>? Changed;
    public event Action<string, string, string>? Claimed; // game, campaign, drop
    public MinerSnapshot Snapshot => _snapshot;

    /// <summary>Interrupts waiting/watching so the game selection is re-read right away.</summary>
    public void Wake()
    {
        var old = Interlocked.Exchange(ref _wake, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Publish(MinerStatus.Starting, L.LookingForCampaigns, null);
                await RefreshAsync(ct);
                var target = PickTarget();
                if (target is null)
                {
                    var anyGame = settings.Games.Any(g => g.Enabled);
                    Publish(MinerStatus.Idle,
                        anyGame ? L.NoLiveDrops : L.SelectGame, null);
                    await WaitAsync(IdleRetry, ct);
                    continue;
                }
                await WatchAsync(target.Value.Campaign, target.Value.Channel, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AuthExpiredException)
            {
                Log.Warn("Login expired");
                Publish(MinerStatus.LoginRequired, L.LoginExpired, null);
                throw;
            }
            catch (Exception ex) when (ex is TwitchApiException or HttpRequestException or TaskCanceledException)
            {
                Log.Error(ex.Message);
                Publish(MinerStatus.Error, L.ErrorRetry(ex.Message), null);
                await WaitAsync(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    // ------------------------------------------------------------ campaigns

    async Task RefreshAsync(CancellationToken ct)
    {
        var wanted = settings.Games.Where(g => g.Enabled).Select(g => g.Name).ToList();
        // match campaigns by game ID; names differ between endpoints ("Tom Clancy's ...")
        var wantedGames = new List<(string Name, string Id, string Slug)>();
        foreach (var name in wanted)
        {
            if (await ResolveGameAsync(name, ct) is { } g) wantedGames.Add((name, g.Id, g.Slug));
        }
        var nameById = wantedGames.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);

        var inventory = await api.GqlAsync(Gql.Inventory(), ct);
        var inv = inventory["data"]?["currentUser"]?["inventory"];
        var claimedBenefits = new Dictionary<string, DateTime>();
        foreach (var b in inv?["gameEventDrops"] as JsonArray ?? [])
        {
            if ((string?)b?["id"] is { } id)
                claimedBenefits[id] = Drop.ParseTime(b!["lastAwardedAt"]);
        }

        var campaigns = new Dictionary<string, Campaign>();
        foreach (var c in inv?["dropCampaignsInProgress"] as JsonArray ?? [])
        {
            if (c?["game"] is null) continue;
            var campaign = ParseCampaign(c, claimedBenefits, inInventory: true,
                nameById.GetValueOrDefault((string?)c["game"]!["id"] ?? ""));
            campaigns[campaign.Id] = campaign;
        }
        await ClaimFinishedAsync(campaigns.Values, ct);

        // discover campaigns through live, drops-enabled channels of the selected games
        var liveGameIds = new HashSet<string>();
        var liveByGame = new Dictionary<string, List<ChannelRef>>();
        foreach (var (gameName, gameId, slug) in wantedGames)
        {
            var streams = await api.GqlAsync(Gql.DropsStreams(slug, StreamsPerGame), ct);
            var channels = (streams["data"]?["game"]?["streams"]?["edges"] as JsonArray ?? [])
                .Select(e => e?["node"])
                .Where(n => n?["broadcaster"] is JsonObject)
                .Select(n => new ChannelRef(
                    (string)n!["broadcaster"]!["id"]!,
                    (string)n["broadcaster"]!["login"]!,
                    (string?)n["broadcaster"]!["displayName"] ?? (string)n["broadcaster"]!["login"]!,
                    (int?)n["viewersCount"] ?? 0,
                    (string?)n["id"] ?? ""))
                .ToList();
            if (channels.Count > 0) liveGameIds.Add(gameId);
            liveByGame[gameId] = channels;
            foreach (var batch in channels.Chunk(20))
            {
                var results = await api.GqlBatchAsync(batch.Select(ch => Gql.AvailableDrops(ch.Id)).ToList(), ct);
                for (var i = 0; i < batch.Length; i++)
                {
                    foreach (var c in results[i]["data"]?["channel"]?["viewerDropCampaigns"] as JsonArray ?? [])
                    {
                        if ((string?)c?["game"]?["id"] != gameId)
                        {
                            LogIgnored(gameName, batch[i], c, "belongs to another game");
                            continue;
                        }
                        if (c!["timeBasedDrops"] is not JsonArray { Count: > 0 })
                        {
                            LogIgnored(gameName, batch[i], c, "has no watch-time drops");
                            continue;
                        }
                        var id = (string)c["id"]!;
                        if (!campaigns.TryGetValue(id, out var campaign))
                        {
                            campaign = ParseCampaign(c, claimedBenefits, inInventory: false, gameName);
                            campaigns[id] = campaign;
                        }
                        if (!campaign.Channels.Any(x => x.Id == batch[i].Id))
                            campaign.Channels.Add(batch[i]);
                    }
                }
            }
        }

        await AddDashboardCampaignsAsync(campaigns, claimedBenefits, nameById, liveByGame, ct);

        await FillCampaignDetailsAsync(campaigns.Values.Where(c => nameById.ContainsKey(c.GameId)), ct);
        _wantedGames = wantedGames;
        _liveGameIds = liveGameIds;

        var now = DateTime.UtcNow;
        _campaigns = campaigns.Values
            .Where(c => nameById.ContainsKey(c.GameId) && c.EndAt > now)
            .OrderBy(c => wantedGames.FindIndex(g => g.Id == c.GameId))
            .ThenBy(c => c.BadgeOrEmoteOnly) // in-game items first, chat badges / emotes last
            .ThenBy(c => c.EndAt)
            .ToList();
        Log.Info($"Found {_campaigns.Count} campaign(s), {_campaigns.Count(c => Wanted(c, now))} earnable, " +
                 $"{_campaigns.Count(c => c.BadgeOrEmoteOnly)} badge/emote only, " +
                 $"{_campaigns.Count(c => c.NeedsLink)} not linked, " +
                 $"{_campaigns.Count(c => c.WatchUnearnable)} not earnable by watching");
        foreach (var c in _campaigns)
        {
            // explain each skipped campaign once, so the log says why it isn't mined
            var reason = c.WatchUnearnable ? "its drops need a subscription or another mission, not watch time"
                : c.NeedsLink ? $"the game account isn't linked ({c.LinkUrl})"
                : c.BadgeOrEmoteOnly && !settings.MineBadges ? "badge / emote rewards only"
                : null;
            if (reason is not null && _loggedSkips.Add(c.Id)) Log.Info($"Skipping {c.GameName} / {c.Name}: {reason}");
        }
    }

    /// <summary>
    /// Adds the active campaigns of the selected games from the user's drops page, and gives the
    /// ones without a channel the live channels they can be earned on. This catches campaigns that
    /// AvailableDrops doesn't list on any of the channels checked.
    /// </summary>
    async Task AddDashboardCampaignsAsync(
        Dictionary<string, Campaign> campaigns, IReadOnlyDictionary<string, DateTime> claimedBenefits,
        Dictionary<string, string> nameById, Dictionary<string, List<ChannelRef>> liveByGame, CancellationToken ct)
    {
        try
        {
            var result = await api.GqlAsync(Gql.DropDashboard(), ct);
            if (result["data"]?["currentUser"]?["dropCampaigns"] is not JsonArray list)
            {
                if (_loggedSkips.Add("dashboard-unavailable"))
                    Log.Warn($"Drops page unavailable: {(string?)result["errors"]?[0]?["message"] ?? "no data"}");
                return;
            }
            var now = DateTime.UtcNow;
            if (_loggedSkips.Add($"dashboard-size:{list.Count}"))
                Log.Info($"Drops page lists {list.Count} campaign(s)");
            foreach (var c in list)
            {
                var gameId = (string?)c?["game"]?["id"];
                if (c is null || gameId is null || !nameById.TryGetValue(gameId, out var gameName)) continue;
                if ((string?)c["status"] != "ACTIVE" || Drop.ParseTime(c["endAt"]) <= now)
                {
                    if (_loggedSkips.Add("dashboard-inactive:" + c["id"]))
                        Log.Info($"Drops page: {gameName} / {(string?)c["name"]} is {(string?)c["status"]}, not active");
                    continue;
                }

                var id = (string)c["id"]!;
                var detail = await api.GqlAsync(Gql.CampaignDrops(user.Id, id), ct);
                var full = detail["data"]?["user"]?["dropCampaign"];
                if (full?["game"] is null)
                {
                    if (_loggedSkips.Add("dashboard-nodetails:" + id))
                        Log.Warn($"Drops page: no details for {gameName} / {(string?)c["name"]}: " +
                                 (detail.ToJsonString() is { Length: > 300 } t ? t[..300] : detail.ToJsonString()));
                    continue;
                }
                if (!campaigns.TryGetValue(id, out var campaign))
                {
                    if (full["timeBasedDrops"] is not JsonArray { Count: > 0 }) continue;
                    campaign = ParseCampaign(full, claimedBenefits, inInventory: false, gameName);
                    campaigns[id] = campaign;
                }
                if (campaign.Channels.Count > 0) continue;

                var live = liveByGame.GetValueOrDefault(gameId) ?? [];
                var allowed = (full["allow"]?["channels"] as JsonArray ?? [])
                    .Select(a => (string?)a?["id"]).OfType<string>().ToHashSet();
                if (allowed.Count == 0 || (bool?)full["allow"]?["isEnabled"] == false)
                {
                    campaign.Channels.AddRange(live);
                }
                else
                {
                    campaign.Channels.AddRange(live.Where(ch => allowed.Contains(ch.Id)));
                    // the channels of an event are often too small to be among the top streams
                    var missing = allowed.Except(campaign.Channels.Select(ch => ch.Id)).Take(50).ToList();
                    if (missing.Count > 0)
                    {
                        var users = await api.GqlAsync(Gql.LiveChannels(missing), ct);
                        foreach (var u in users["data"]?["users"] as JsonArray ?? [])
                        {
                            if (u?["stream"] is not JsonObject stream || (string?)stream["game"]?["id"] != gameId) continue;
                            campaign.Channels.Add(new ChannelRef((string)u["id"]!, (string)u["login"]!,
                                (string?)u["displayName"] ?? (string)u["login"]!,
                                (int?)stream["viewersCount"] ?? 0, (string?)stream["id"] ?? ""));
                        }
                    }
                }
                campaign.Channels.Sort((a, b) => b.Viewers.CompareTo(a.Viewers));
                if (_loggedSkips.Add("dashboard:" + id))
                    Log.Info($"Drops page: {gameName} / {campaign.Name}, {campaign.Channels.Count} live channel(s)");
            }
        }
        catch (TwitchApiException ex)
        {
            Log.Warn($"Drops page unavailable: {ex.Message}");
        }
    }

    /// <summary>Says once why a campaign seen on a channel of a selected game isn't taken into account.</summary>
    void LogIgnored(string gameName, ChannelRef channel, JsonNode? campaign, string reason)
    {
        // streamers' own sub / channel reward campaigns have no game and come and go all day
        if (string.IsNullOrEmpty((string?)campaign?["game"]?["id"])) return;
        var id = (string?)campaign?["id"] ?? "?";
        if (!_loggedSkips.Add("ignored:" + id)) return;
        Log.Info($"Ignoring \"{(string?)campaign?["name"]}\" seen on {channel.Login} ({gameName}): {reason}" +
                 $" [game {(string?)campaign?["game"]?["name"]} {(string?)campaign?["game"]?["id"]}]");
    }

    bool Wanted(Campaign c, DateTime now) => c.CanEarn(now) && (settings.MineBadges || !c.BadgeOrEmoteOnly);

    /// <summary>
    /// AvailableDrops doesn't say whether a reward is an in-game item or a chat badge / emote,
    /// nor whether the game account is linked. Ask one live channel per campaign; whatever
    /// can't be learned stays unknown (rewards count as items, the account as linked).
    /// </summary>
    async Task FillCampaignDetailsAsync(IEnumerable<Campaign> campaigns, CancellationToken ct)
    {
        var targets = campaigns
            .Where(c => c.Channels.Count > 0
                        && (!c.InInventory || c.Drops.All(d => d.RewardTypes.Count == 0)))
            .ToList();
        if (targets.Count == 0) return;
        var byId = targets.ToDictionary(c => c.Id);
        var dropsById = targets.SelectMany(c => c.Drops).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var channelIds = targets.Select(c => c.Channels[0].Id).Distinct().ToList();
        try
        {
            foreach (var batch in channelIds.Chunk(20))
            {
                var results = await api.GqlBatchAsync(
                    batch.Select(id => Gql.CampaignDetails(id, _accountLinkQuery)).ToList(), ct);
                if (_accountLinkQuery && results.Any(r => r["data"]?["channel"] is null && r["errors"] is JsonArray))
                {
                    // Twitch refused the account link fields for this client; keep the reward types
                    Log.Warn($"Account link status unavailable: {(string?)results[0]["errors"]?[0]?["message"]}");
                    _accountLinkQuery = false;
                    results = await api.GqlBatchAsync(batch.Select(id => Gql.CampaignDetails(id, false)).ToList(), ct);
                }
                foreach (var result in results)
                {
                    foreach (var c in result["data"]?["channel"]?["viewerDropCampaigns"] as JsonArray ?? [])
                    {
                        if ((string?)c?["id"] is { } campaignId && byId.TryGetValue(campaignId, out var campaign))
                        {
                            if ((bool?)c["self"]?["isAccountConnected"] is { } linked) campaign.AccountLinked = linked;
                            if ((string?)c["accountLinkURL"] is { Length: > 0 } url) campaign.LinkUrl = url;
                        }
                        foreach (var d in c?["timeBasedDrops"] as JsonArray ?? [])
                        {
                            if ((string?)d?["id"] is not { } dropId || !dropsById.TryGetValue(dropId, out var drop)) continue;
                            var types = (d!["benefitEdges"] as JsonArray ?? [])
                                .Select(e => (string?)e?["benefit"]?["distributionType"])
                                .OfType<string>()
                                .ToList();
                            if (types.Count == 0) continue;
                            drop.RewardTypes.Clear();
                            drop.RewardTypes.AddRange(types);
                        }
                    }
                }
            }
        }
        catch (TwitchApiException ex)
        {
            Log.Warn($"Campaign details unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// True when a game higher in the list than <paramref name="gameId"/> now has live drop
    /// streams but had none at the last refresh, so it's worth switching back to it.
    /// </summary>
    async Task<bool> HigherPriorityGameWentLiveAsync(string gameId, CancellationToken ct)
    {
        var index = _wantedGames.FindIndex(g => g.Id == gameId);
        foreach (var game in _wantedGames.Take(Math.Max(0, index)))
        {
            if (_liveGameIds.Contains(game.Id)) continue;
            var streams = await api.GqlAsync(Gql.DropsStreams(game.Slug, 1), ct);
            if (streams["data"]?["game"]?["streams"]?["edges"] is JsonArray { Count: > 0 })
            {
                Log.Info($"{game.Name} has live drops now, switching back to it");
                return true;
            }
        }
        return false;
    }

    Campaign ParseCampaign(JsonNode c, IReadOnlyDictionary<string, DateTime> claimed, bool inInventory, string? gameName = null)
    {
        var drops = (c["timeBasedDrops"] as JsonArray ?? [])
            .Where(d => d is not null)
            .Select(d => Drop.Parse(d!, claimed))
            .ToList();
        var game = c["game"]!;
        bool? linked = (bool?)c["self"]?["isAccountConnected"];
        if (linked is null)
        {
            // discovered campaigns don't say; reuse what an inventory campaign of the game says
            linked = _campaigns.FirstOrDefault(x => x.GameId == (string?)game["id"] && x.AccountLinked is not null)
                ?.AccountLinked;
        }
        return new Campaign
        {
            Id = (string)c["id"]!,
            Name = ((string?)c["name"])?.Trim() ?? "?",
            GameId = (string?)game["id"] ?? "",
            GameName = gameName ?? (string?)game["displayName"] ?? (string?)game["name"] ?? "?",
            EndAt = Drop.ParseTime(c["endAt"]),
            Drops = drops,
            AccountLinked = linked,
            LinkUrl = (string?)c["accountLinkURL"],
            InInventory = inInventory,
        };
    }

    async Task<(string Id, string Slug)?> ResolveGameAsync(string name, CancellationToken ct)
    {
        if (_gameCache.TryGetValue(name, out var cached)) return cached;
        var result = await api.GqlAsync(Gql.GameSlug(name), ct);
        var game = result["data"]?["game"];
        (string, string)? value = game is null ? null : ((string)game["id"]!, (string)game["slug"]!);
        if (value is null) Log.Warn($"Unknown game on Twitch: {name}");
        _gameCache[name] = value;
        return value;
    }

    async Task ClaimFinishedAsync(IEnumerable<Campaign> campaigns, CancellationToken ct)
    {
        foreach (var campaign in campaigns)
        {
            foreach (var drop in campaign.Drops.Where(d => d.WatchEarnable && d.IsComplete && !d.IsClaimed
                                                           && d.IsActive(DateTime.UtcNow)))
            {
                var instance = drop.InstanceId ?? $"{user.Id}#{campaign.Id}#{drop.Id}";
                var result = await api.GqlAsync(Gql.ClaimDrop(instance), ct);
                var status = (string?)result["data"]?["claimDropRewards"]?["status"];
                if (status is "ELIGIBLE_FOR_ALL" or "DROP_INSTANCE_ALREADY_CLAIMED")
                {
                    drop.IsClaimed = true;
                    Log.Info($"Claimed: {campaign.GameName} / {drop.Name}");
                    Claimed?.Invoke(campaign.GameName, campaign.Name, drop.Name);
                }
                else
                {
                    var error = (string?)result["errors"]?[0]?["message"] ?? status ?? "no status";
                    Log.Warn($"Claim failed for {drop.Name}: {error}");
                }
            }
        }
    }

    (Campaign Campaign, ChannelRef Channel)? PickTarget()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _badChannels.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            _badChannels.Remove(key);
        foreach (var campaign in _campaigns.Where(c => Wanted(c, now)))
        {
            var channel = campaign.Channels.FirstOrDefault(ch => !_badChannels.ContainsKey(ch.Id));
            if (channel is not null) return (campaign, channel);
        }
        return null;
    }

    // ------------------------------------------------------------ watching

    async Task WatchAsync(Campaign campaign, ChannelRef channel, CancellationToken ct)
    {
        var drop = campaign.NextDrop(DateTime.UtcNow)!;
        Log.Info($"Watching {channel.Login} for {campaign.GameName} / {campaign.Name} / {drop.Name}");
        PublishWatching(campaign, channel, drop);

        var watcher = new StreamWatcher(api, channel, campaign, user.Id);
        var started = DateTime.UtcNow;
        var lastProgressAt = started;
        var nextCheck = started + ProgressCheckInterval;
        var lastMinutes = drop.CurrentMinutes;
        var nextPriorityCheck = started + PriorityCheckInterval;
        var failures = 0;

        while (true)
        {
            if (await watcher.TickAsync(ct)) failures = 0;
            else if (++failures >= 3)
            {
                Log.Info($"{channel.Login} went offline or stopped serving the stream");
                MarkBad(channel);
                return;
            }

            var now = DateTime.UtcNow;
            if (now >= nextCheck)
            {
                nextCheck = now + ProgressCheckInterval;
                var result = await api.GqlAsync(Gql.CurrentDrop(channel.Id), ct);
                var session = result["data"]?["currentUser"]?["dropCurrentSession"];
                var dropId = (string?)session?["dropID"];
                var minutes = (int?)session?["currentMinutesWatched"] ?? 0;
                var current = string.IsNullOrEmpty(dropId) ? null : campaign.Drops.FirstOrDefault(d => d.Id == dropId);
                if (current is not null)
                {
                    // minutes count for every drop of the campaign at once
                    var delta = minutes - current.CurrentMinutes;
                    if (delta > 0)
                    {
                        foreach (var d in campaign.Drops.Where(d => !d.IsClaimed))
                            d.CurrentMinutes = Math.Min(d.RequiredMinutes, d.CurrentMinutes + delta);
                        current.CurrentMinutes = minutes;
                    }
                    drop = current;
                    PublishWatching(campaign, channel, drop);
                    if (minutes > lastMinutes)
                    {
                        lastMinutes = minutes;
                        lastProgressAt = now;
                    }
                    if (current.IsComplete)
                    {
                        Log.Info($"Drop finished: {current.Name}");
                        return; // the refresh claims it and picks the next target
                    }
                }
                if (now - lastProgressAt > NoProgressTimeout)
                {
                    Log.Warn($"No progress on {channel.Login} for {NoProgressTimeout.TotalMinutes:0} min, switching");
                    MarkBad(channel);
                    return;
                }
            }
            if (now - started > RefreshInterval) return;
            if (now >= nextPriorityCheck)
            {
                nextPriorityCheck = now + PriorityCheckInterval;
                if (await HigherPriorityGameWentLiveAsync(campaign.GameId, ct)) return;
            }
            if (!await WaitAsync(WatchInterval, ct)) return; // woken up: selection changed
        }
    }

    void MarkBad(ChannelRef channel) => _badChannels[channel.Id] = DateTime.UtcNow + BadChannelCooldown;

    /// <summary>Waits for the delay; returns false if <see cref="Wake"/> interrupted it.</summary>
    async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token);
        try
        {
            await Task.Delay(delay, linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ UI snapshot

    void PublishWatching(Campaign campaign, ChannelRef channel, Drop drop) => Publish(
        MinerStatus.Mining, L.Mining,
        new WatchInfo(channel, campaign.GameName, campaign.Name, campaign.EndAt, drop.Name,
            drop.CurrentMinutes, drop.RequiredMinutes));

    void Publish(MinerStatus status, string text, WatchInfo? watching)
    {
        var now = DateTime.UtcNow;
        var rows = _campaigns
            .Select(c => new CampaignRow(c.GameName, c.Name, c.Progress, Wanted(c, now),
                c.WatchUnearnable ? CampaignTag.NotByWatching : c.NeedsLink ? CampaignTag.NeedsLink : CampaignTag.None,
                c.LinkUrl))
            .ToList();
        _snapshot = new MinerSnapshot(status, text, watching, rows);
        Changed?.Invoke(_snapshot);
    }
}

/// <summary>
/// Simulates a viewer by periodically fetching the stream's lowest quality playlist and
/// sending HEAD requests for its new segments, without downloading any video data. Like the
/// web player, it also reports a "minute-watched" event once a minute while the stream plays.
/// </summary>
sealed class StreamWatcher(TwitchApi api, ChannelRef channel, Campaign campaign, string userId)
{
    static readonly TimeSpan MinuteWatchedInterval = TimeSpan.FromMinutes(1);

    Uri? _playlist;
    readonly Queue<string> _seen = new();
    DateTime _nextMinuteWatched = DateTime.UtcNow + MinuteWatchedInterval;
    bool _trackFailing;

    public async Task<bool> TickAsync(CancellationToken ct)
    {
        try
        {
            _playlist ??= await GetPlaylistAsync(ct);
            if (_playlist is null) return false;

            using var req = new HttpRequestMessage(HttpMethod.Get, _playlist);
            req.Headers.ConnectionClose = true;
            using var resp = await api.Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _playlist = null;
                return false;
            }
            var segments = (await resp.Content.ReadAsStringAsync(ct))
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("http", StringComparison.Ordinal))
                .ToList();
            if (segments.Count == 0)
            {
                _playlist = null;
                return false;
            }
            foreach (var segment in segments.Where(s => !_seen.Contains(s)))
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, segment);
                using var headResp = await api.Http.SendAsync(head, ct);
                if (headResp.StatusCode != HttpStatusCode.OK) return false;
                _seen.Enqueue(segment);
                if (_seen.Count > 30) _seen.Dequeue();
            }
            if (DateTime.UtcNow >= _nextMinuteWatched)
            {
                _nextMinuteWatched = DateTime.UtcNow + MinuteWatchedInterval;
                await SendMinuteWatchedAsync(ct);
            }
            return true;
        }
        catch (HttpRequestException)
        {
            _playlist = null;
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _playlist = null;
            return false;
        }
    }

    /// <summary>Failures are only logged: the segment requests alone still earn progress.</summary>
    async Task SendMinuteWatchedAsync(CancellationToken ct)
    {
        bool ok;
        try
        {
            ok = await api.TrackAsync("minute-watched", MinuteWatchedProperties(), ct);
        }
        catch (HttpRequestException) { ok = false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { ok = false; }
        if (!ok && !_trackFailing) Log.Warn($"Twitch did not accept the minute-watched event for {channel.Login}");
        _trackFailing = !ok;
    }

    // the fields the web player sends; "site" matches the player type of the playback token
    JsonObject MinuteWatchedProperties() => new()
    {
        ["broadcast_id"] = channel.StreamId,
        ["channel_id"] = channel.Id,
        ["channel"] = channel.Login,
        ["game"] = campaign.GameName,
        ["game_id"] = campaign.GameId,
        ["hidden"] = false,
        ["live"] = true,
        ["location"] = "channel",
        ["logged_in"] = true,
        ["muted"] = false,
        ["player"] = "site",
        ["user_id"] = userId,
    };

    async Task<Uri?> GetPlaylistAsync(CancellationToken ct)
    {
        var result = await api.GqlAsync(Gql.PlaybackAccessToken(channel.Login), ct);
        var token = result["data"]?["streamPlaybackAccessToken"];
        if (token is null) return null;
        var url = $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(channel.Login)}.m3u8" +
                  $"?sig={Uri.EscapeDataString((string)token["signature"]!)}" +
                  $"&token={Uri.EscapeDataString((string)token["value"]!)}" +
                  $"&allow_source=true&p={Random.Shared.Next(1_000_000, 9_999_999)}";
        using var resp = await api.Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var lines = (await resp.Content.ReadAsStringAsync(ct))
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("http", StringComparison.Ordinal))
            .ToList();
        // variants are listed best first; the last one is the lightest
        return lines.Count == 0 ? null : new Uri(lines[^1]);
    }
}
