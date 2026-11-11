using System.Net;
using System.Text;
using System.Text.Json;

namespace TSM.Services;

internal sealed class TsmDesktopFeed(HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    private const string SocketUrl = "https://app-backend-sockets.tradeskillmaster.com/socket.io/?EIO=4&transport=polling";
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SnapshotQuietPeriod = TimeSpan.FromSeconds(3);

    internal async Task<Snapshot> ReadSnapshotAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SnapshotTimeout);
        string? sessionUrl = null;
        Task<string>? pendingPoll = null;
        var snapshot = new Snapshot();
        var connected = false;
        try
        {
            var opening = await SendAsync(SocketUrl, accessToken, null, timeout.Token);
            if (!opening.StartsWith('0'))
                throw new InvalidDataException("The TradeSkillMaster data feed sent an invalid handshake.");
            using var handshake = JsonDocument.Parse(opening[1..]);
            var sessionId = handshake.RootElement.GetProperty("sid").GetString();
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidDataException("The TradeSkillMaster data feed did not return a session ID.");

            sessionUrl = SocketUrl + "&sid=" + Uri.EscapeDataString(sessionId);
            await SendAsync(sessionUrl, accessToken, "40", timeout.Token);
            while (true)
            {
                // The feed pushes its initial records as separate events. The addon catalog has an
                // end marker and the price feeds finish by going idle.
                pendingPoll = SendAsync(sessionUrl, accessToken, null, timeout.Token);
                string batch;
                try
                {
                    batch = connected && snapshot.HasCompleteAddons
                        ? await pendingPoll.WaitAsync(SnapshotQuietPeriod, timeout.Token)
                        : await pendingPoll;
                }
                catch (TimeoutException)
                {
                    return snapshot;
                }
                pendingPoll = null;

                foreach (var packet in batch.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (packet.StartsWith("40", StringComparison.Ordinal))
                        connected = true;
                    else if (packet.StartsWith("42", StringComparison.Ordinal))
                        snapshot.Apply(packet.AsSpan(2), jsonOptions);
                    else if (packet.StartsWith("44", StringComparison.Ordinal))
                        throw new HttpRequestException("TradeSkillMaster rejected the data feed session.",
                            null, HttpStatusCode.Unauthorized);
                    else if (packet is "1" or "41")
                        throw new IOException("The TradeSkillMaster data feed disconnected during sync.");
                    else if (packet.StartsWith('2'))
                        await SendAsync(sessionUrl, accessToken, "3" + packet[1..], timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("TradeSkillMaster did not finish sending its data feed.");
        }
        finally
        {
            if (sessionUrl is not null && !cancellationToken.IsCancellationRequested)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await SendAsync(sessionUrl, accessToken, "1", closeTimeout.Token);
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                {
                    // Engine.IO also expires sessions if the closing packet cannot be sent.
                }
            }
            if (pendingPoll is not null)
            {
                timeout.Cancel();
                try
                {
                    await pendingPoll;
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
            }
        }
    }

    private async Task<string> SendAsync(string url, string token, string? packet,
        CancellationToken cancellationToken)
    {
        if (packet is null)
            url += "&t=" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(packet is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", token);
        if (packet is not null)
            request.Content = new StringContent(packet, Encoding.UTF8, "text/plain");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    internal sealed class Snapshot
    {
        internal Dictionary<(string Name, string Channel), Addon> Addons { get; } = new();
        internal Dictionary<(string Id, string House, string Type), FeedData> Prices { get; } = new();
        internal AppInfo Info { get; private set; } = new();
        internal bool HasCompleteAddons { get; private set; }

        internal void Apply(ReadOnlySpan<char> packet, JsonSerializerOptions options)
        {
            using var message = JsonDocument.Parse(packet.ToString());
            var args = message.RootElement;
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2)
                return;
            switch (args[0].GetString())
            {
                case "addon":
                    var update = args[1].Deserialize<AddonUpdate>(options)
                                 ?? throw new JsonException("Invalid addon feed event.");
                    var complete = update.IsComplete ?? true;
                    if (complete)
                        Addons.Clear();
                    foreach (var addon in update.Data)
                        Addons[(addon.AddonName, addon.Channel.ToLowerInvariant())] = addon;
                    HasCompleteAddons |= complete;
                    break;
                case "realm_auctiondb":
                case "realm_shopping":
                case "region_auctiondb":
                    var data = args[1].Deserialize<FeedData>(options)
                               ?? throw new JsonException("Invalid price feed event.");
                    var key = (data.RealmId ?? data.RegionId ?? string.Empty,
                        (data.AuctionHouseType ?? string.Empty).ToUpperInvariant(), data.StringType);
                    if (!Prices.TryGetValue(key, out var old) || old.LastModified <= data.LastModified)
                        Prices[key] = data;
                    break;
                case "app_info":
                    Info = args[1].Deserialize<AppInfo>(options) ?? new AppInfo();
                    break;
            }
        }
    }

    internal sealed record Addon(
        [property: System.Text.Json.Serialization.JsonPropertyName("addon")] string AddonName,
        string Channel, string Version, string Url);

    internal sealed record AddonUpdate(Addon[] Data, bool? IsComplete);

    internal sealed record FeedData(
        [property: System.Text.Json.Serialization.JsonConverter(typeof(TsmStringIdConverter))] string? RealmId,
        [property: System.Text.Json.Serialization.JsonConverter(typeof(TsmStringIdConverter))] string? RegionId,
        string? AuctionHouseType, string StringType, string Url, long LastModified);

    internal sealed record AppInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("message")] TSM.Models.TsmAddonMessage? Message = null);
}
