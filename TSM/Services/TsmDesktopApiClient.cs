using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TSM.Models;

namespace TSM.Services;

public sealed partial class TsmDesktopApiClient : ITsmApiClient, IDisposable
{
    private const string DesktopUrl = "https://desktop-backend.tradeskillmaster.com";
    private const string RealmUrl = "https://realm-api.tradeskillmaster.com/v2";
    private const string SnapshotUrl = DesktopUrl + "/sv-snapshots";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TsmDesktopFeed _feed;
    private IReadOnlyList<TsmDesktopFeed.Addon> _addons = [];

    public TsmDesktopApiClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = TimeSpan.FromMinutes(2) };
        _feed = new TsmDesktopFeed(_httpClient, JsonOptions);
    }

    public bool SupportsEndpoint(string endpoint) => endpoint == "sv-snapshots" || (endpoint == "backup" && IsPremium);

    internal async Task<IReadOnlyDictionary<string, string>> GetGameTypesAsync(CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, DesktopUrl + "/game-versions", session, null,
            cancellationToken);
        return (await ReadAsync<GameVersion[]>(response, cancellationToken))
            .Where(game => !string.IsNullOrWhiteSpace(game.Folder) && !string.IsNullOrWhiteSpace(game.GameType))
            .ToDictionary(game => game.Folder, game => game.GameType, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<TsmStatusResponse> GetStatusAsync(string channel, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var realms = ReadItemsAsync<Realm>(RealmUrl + "/realms", session, cancellationToken);
        var regions = ReadItemsAsync<Region>(RealmUrl + "/regions", session, cancellationToken);
        var selected = ReadUserRealmsAsync(session, cancellationToken);
        await Task.WhenAll(realms, regions, selected);
        var snapshot = await _feed.ReadSnapshotAsync(session.AccessToken, cancellationToken);
        var markets = GetMarkets(await realms).ToArray();
        var marketLookup = markets.ToLookup(market => (market.RealmId, market.HouseType));
        var regionLookup = (await regions).ToLookup(region => region.RegionId);
        var selectedMarkets = (await selected).SelectMany(realm =>
            marketLookup[(realm.RealmId, realm.AuctionHouseType.ToUpperInvariant())]).ToArray();
        var data = snapshot.Prices.ToLookup(entry => (entry.Key.Id, entry.Key.House));

        var status = new TsmStatusResponse
        {
            DesktopVersion = "1.0.10",
            AddonMessage = snapshot.Info.Message ?? new TsmAddonMessage(),
            SelectedRealms = selectedMarkets.Select(market => market.Choice).ToArray()
        };
        var selectedChannel = EffectiveAddonChannel(channel, session.Channels);
        _addons = snapshot.Addons.Values
            .Where(addon => addon.Channel.Equals(selectedChannel, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var addon in _addons)
            status.Addons.Add(new TsmAddonVersion { Name = addon.AddonName, Version = addon.Version });

        foreach (var market in selectedMarkets)
        {
            var entry = ToStatusEntry(market.Choice.Name, market.Region.RegionPrefix, market.RealmId, market.HouseId,
                data[(market.RealmId, market.HouseType)].Select(pair => pair.Value));
            if (entry is not null)
                GetGameEntries(status, market.Choice.GameVersion).Realms.Add(entry);
        }
        foreach (var group in snapshot.Prices.Values.Where(price => price.RegionId is not null)
                     .GroupBy(price => price.RegionId!))
        foreach (var region in regionLookup[group.Key])
        {
            var (game, _, prefix) = DescribeGame(region.GameVersion);
            if (game.Length == 0)
                continue;
            var key = prefix + region.RegionPrefix.ToUpperInvariant();
            var entry = ToStatusEntry(key, key, region.RegionId, null, group);
            if (entry is not null)
                GetGameEntries(status, game).Regions.Add(entry);
        }
        return status;
    }

    public async Task<IReadOnlyList<TsmRealmChoice>> GetAvailableRealmsAsync(
        CancellationToken cancellationToken = default)
        => (await GetRealmCatalogAsync(cancellationToken)).Available;

    internal async Task<(IReadOnlyList<TsmRealmChoice> Available, IReadOnlyList<TsmRealmChoice> Selected)>
        GetRealmCatalogAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var realms = ReadItemsAsync<Realm>(RealmUrl + "/realms", session, cancellationToken);
        var selected = ReadUserRealmsAsync(session, cancellationToken);
        await Task.WhenAll(realms, selected);
        var markets = GetMarkets(await realms).ToArray();
        var selectedKeys = (await selected).Select(realm => (realm.RealmId, realm.AuctionHouseType.ToUpperInvariant()))
            .ToHashSet();
        var available = markets.Select(market => market.Choice)
            .DistinctBy(choice => (choice.GameVersion, choice.Id))
            .OrderBy(choice => choice.GameLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Region, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return (available, markets.Where(market => selectedKeys.Contains((market.RealmId, market.HouseType)))
            .Select(market => market.Choice).ToArray());
    }

    public async Task AddRealmAsync(string gameVersion, string realmId, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var markets = GetMarkets(await ReadItemsAsync<Realm>(RealmUrl + "/realms", session, cancellationToken));
        var market = markets.FirstOrDefault(market => market.Choice.GameVersion == gameVersion && market.Choice.Id == realmId)
                     ?? throw new InvalidOperationException("This market is no longer available.");
        var selected = await ReadUserRealmsAsync(session, cancellationToken);
        if (selected.Any(realm => realm.RealmId == market.RealmId &&
                realm.AuctionHouseType.Equals(market.HouseType, StringComparison.OrdinalIgnoreCase)))
            return;
        using var response = await SendAsync(HttpMethod.Post, DesktopUrl + "/user-realms", session,
            new { realmId = market.ApiRealmId, userId = session.Subject, auctionHouseType = market.HouseType },
            cancellationToken);
    }

    public async Task RemoveRealmAsync(string gameVersion, string region, string realmName,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var realms = ReadItemsAsync<Realm>(RealmUrl + "/realms", session, cancellationToken);
        var selected = ReadUserRealmsAsync(session, cancellationToken);
        await Task.WhenAll(realms, selected);
        var matches = GetMarkets(await realms).Where(market =>
            string.Equals(market.Choice.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(market.Choice.Region, region, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(market.Choice.Name, realmName, StringComparison.OrdinalIgnoreCase))
            .Select(market => (market.RealmId, market.HouseType)).ToHashSet();
        foreach (var selection in await selected)
        {
            if (matches.Contains((selection.RealmId, selection.AuctionHouseType.ToUpperInvariant())))
            {
                using var response = await SendAsync(HttpMethod.Delete,
                    DesktopUrl + "/user-realms/" + Uri.EscapeDataString(selection.Id), session, null,
                    cancellationToken);
            }
        }
    }

    public Task<string> DownloadDataAsync(string url, CancellationToken cancellationToken = default) =>
        TsmDownloads.ReadDataAsync(_httpClient, TsmDownloads.HttpsUri(url), cancellationToken);

    internal string EffectiveAddonChannel(string channel) => EffectiveAddonChannel(channel, ReleaseChannels);

    private static string EffectiveAddonChannel(string channel, IReadOnlyList<string> channels) =>
        channels.FirstOrDefault(value => value.Equals(channel, StringComparison.OrdinalIgnoreCase)) ?? "Release";

    public async Task<byte[]> DownloadAddonAsync(string name, string channel, string version,
        CancellationToken cancellationToken = default)
    {
        var addon = _addons.FirstOrDefault(addon => addon.AddonName == name && addon.Version == version)
                    ?? throw new InvalidOperationException("The current TradeSkillMaster feed did not include this addon.");
        return await DownloadArchiveAsync(addon.Url, cancellationToken);
    }

    public async Task UploadAsync(TsmUploadRequest upload, CancellationToken cancellationToken = default)
    {
        if (!SupportsEndpoint(upload.Endpoint))
            throw new NotSupportedException("This addon upload is unavailable on the desktop API.");
        var session = await GetSessionAsync(cancellationToken);
        using var reserve = await SendAsync(HttpMethod.Post, SnapshotUrl + "/uploads", session, null, cancellationToken);
        var target = await ReadAsync<SnapshotUpload>(reserve, cancellationToken);

        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gzip, upload.Data, JsonOptions, cancellationToken);
        await UploadFileAsync(target.Url, buffer.ToArray(), cancellationToken);
        using var complete = await SendAsync(HttpMethod.Post, SnapshotUrl, session, new { target.Id }, cancellationToken);
    }

    public Task<long> GetLastUploadAsync(string endpoint, IReadOnlyList<string> path,
        CancellationToken cancellationToken = default) => Task.FromResult(0L);

    public Task UploadLogAsync(string log, bool crash = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TradeSkillMaster log submission is unavailable on the desktop API.");

    public void Dispose()
    {
        Logout();
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private Task<UserRealm[]> ReadUserRealmsAsync(Session session, CancellationToken cancellationToken) =>
        ReadItemsAsync<UserRealm>(DesktopUrl + "/user-realms?userIds=" + Uri.EscapeDataString(session.Subject),
            session, cancellationToken);

    private async Task<T[]> ReadItemsAsync<T>(string url, Session session, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, session, null, cancellationToken);
        return (await ReadAsync<ItemsResponse<T>>(response, cancellationToken)).Items
               ?? throw new JsonException("The TradeSkillMaster response did not contain an items list.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Session session, object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"The TradeSkillMaster service returned HTTP {(int)status}.", null, status);
    }

    private async Task<byte[]> DownloadArchiveAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(TsmDownloads.HttpsUri(url),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(100_000_000, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task UploadFileAsync(string url, byte[] content, CancellationToken cancellationToken)
    {
        using var body = new ByteArrayContent(content);
        using var response = await _httpClient.PutAsync(TsmDownloads.HttpsUri(url), body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
        ?? throw new JsonException("Empty TradeSkillMaster response.");

    private static IEnumerable<Market> GetMarkets(IEnumerable<Realm> realms)
    {
        foreach (var realm in realms)
        {
            if (realm.Region is null)
                continue;
            var (game, label, _) = DescribeGame(realm.Region.GameVersion);
            var realmId = realm.RealmId.ToString();
            if (game.Length == 0 || realmId.Length == 0)
                continue;
            foreach (var house in realm.AuctionHouses ?? [])
            {
                var houseType = house.Type.ToUpperInvariant();
                var localizedName = string.IsNullOrWhiteSpace(realm.LocalizedName) ? realm.Name : realm.LocalizedName;
                var name = houseType == "ALL" ? localizedName :
                    localizedName + "-" + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(houseType.ToLowerInvariant());
                var id = realmId + ":" + houseType;
                var choice = new TsmRealmChoice(game, label, realm.Region.RegionPrefix.ToUpperInvariant(), name, id);
                yield return new Market(realmId, realm.RealmId, houseType, house.AuctionHouseId, realm.Region, choice);
            }
        }
    }

    private static TsmRealmEntry? ToStatusEntry(string name, string region, string id, string? houseId,
        IEnumerable<TsmDesktopFeed.FeedData> updates)
    {
        var strings = updates.Where(data => data.LastModified > 0 && !string.IsNullOrWhiteSpace(data.Url) &&
                                            AppDataCatalogParser.SupportedTypes.Contains(data.StringType))
            .ToDictionary(data => data.StringType,
                data => new TsmDataString { Url = data.Url, LastModified = data.LastModified }, StringComparer.Ordinal);
        return strings.Count == 0 ? null : new TsmRealmEntry
        {
            Name = name, Region = region, Id = JsonSerializer.SerializeToElement(id),
            AuctionHouseId = JsonSerializer.SerializeToElement(houseId), AppDataStrings = strings
        };
    }

    private static (List<TsmRealmEntry> Realms, List<TsmRealmEntry> Regions) GetGameEntries(TsmStatusResponse status,
        string game) => game switch
    {
        "retail" => (status.Realms, status.Regions),
        "progression" => (status.ProgressionRealms, status.ProgressionRegions),
        "classic" => (status.ClassicRealms, status.ClassicRegions),
        "anniversary" => (status.AnniversaryRealms, status.AnniversaryRegions),
        "forever" => (status.ForeverRealms, status.ForeverRegions),
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };

    private static (string Game, string Label, string Prefix) DescribeGame(string gameVersion) => gameVersion switch
    {
        "Retail" => ("retail", "Retail", ""),
        "Wrath" => ("progression", "Progression", "BCC-"),
        "Classic Era" => ("classic", "Classic Era", "Classic-"),
        "Classic Era - Hardcore" => ("classic", "Classic Era", "HC-"),
        "Season of Discovery" => ("classic", "Classic Era", "SoD-"),
        "Season of Mastery" => ("classic", "Classic Era", "SoM-"),
        "Classic Era - Fresh" => ("anniversary", "Anniversary", "Fresh-"),
        "Forever" => ("forever", "Forever", "Forever-"),
        _ => ("", "", "")
    };

    private sealed record ItemsResponse<T>(T[] Items);
    private sealed record SnapshotUpload(JsonElement Id, string Url);
    private sealed record GameVersion(string Folder, string GameType);
    private sealed record Market(string RealmId, JsonElement ApiRealmId, string HouseType, string? HouseId,
        Region Region, TsmRealmChoice Choice);
    private sealed record Realm(JsonElement RealmId, string Name, string LocalizedName, Region? Region,
        AuctionHouse[]? AuctionHouses);
    private sealed record Region([property: JsonConverter(typeof(TsmStringIdConverter))] string RegionId,
        string RegionPrefix, string GameVersion);
    private sealed record AuctionHouse(string Type,
        [property: JsonConverter(typeof(TsmStringIdConverter))] string? AuctionHouseId);
    private sealed record UserRealm([property: JsonConverter(typeof(TsmStringIdConverter))] string Id,
        [property: JsonConverter(typeof(TsmStringIdConverter))] string RealmId, string AuctionHouseType);
}

internal sealed class TsmStringIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Number => reader.GetInt64().ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException("The TradeSkillMaster ID must be a string or an integer.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
