using System.Text;
using TSM.Models;

namespace TSM.Services;

public sealed class AppDataFileWriter(AppDataCatalogParser parser)
{
    public async Task WriteAsync(
        string path,
        IEnumerable<AppDataUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = new Dictionary<(string Type, string Realm), StoredEntry>();

        if (File.Exists(path))
        {
            foreach (var entry in parser.ParseFile(path).Entries)
            {
                entries[(entry.Type, entry.Realm)] =
                    new StoredEntry(entry.Type, entry.Realm, entry.UpdatedAtUnix, entry.RawPayload, entry.AuctionHouseId,
                        entry.SharedDataBlob);
            }
        }

        foreach (var update in updates)
        {
            ValidateKey(update.Type, update.Realm);
            var key = (update.Type, update.Realm);
            if (entries.TryGetValue(key, out var existing) && existing.Timestamp > update.Timestamp)
                continue;
            if (string.IsNullOrWhiteSpace(update.DataBlob))
                throw new InvalidDataException($"The {update.Type}/{update.Realm} data blob was empty.");
            entries[key] =
                new StoredEntry(update.Type, update.Realm, update.Timestamp, Render(update), update.AuctionHouseId);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new InvalidOperationException("AppData.lua has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".AppData.{Guid.NewGuid():N}.tmp");
        var lines = entries.Values.Where(entry => entry.SharedDataBlob is null).Select(entry =>
            (entry.Type, entry.Realm, Line: Metadata(entry.Payload, entry.Type, entry.Realm, entry.Timestamp,
                entry.AuctionHouseId)));
        var sharedLines = entries.Values.Where(entry => entry.SharedDataBlob is not null)
            .GroupBy(entry => (entry.Type, entry.Timestamp, entry.AuctionHouseId, entry.SharedDataBlob))
            .Select(group =>
            {
                var key = group.Key;
                var realms = group.Select(entry => entry.Realm).Order(StringComparer.Ordinal).ToArray();
                var payload = realms.Length == 1
                    ? Render(new AppDataUpdate(key.Type, realms[0], key.SharedDataBlob!, key.Timestamp))
                    : RenderShared(key.Type, realms, key.SharedDataBlob!);
                var realm = string.Join('|', realms);
                return (key.Type, Realm: realm, Line: Metadata(payload, key.Type, realm, key.Timestamp, key.AuctionHouseId));
            });
        var content = string.Join('\n', lines.Concat(sharedLines)
            .OrderBy(entry => entry.Type, StringComparer.Ordinal)
            .ThenBy(entry => entry.Realm, StringComparer.Ordinal)
            .Select(entry => entry.Line)) + "\n";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string Render(AppDataUpdate update) =>
        $"select(2, ...).LoadData(\"{EscapeLua(update.Type)}\",\"{EscapeLua(update.Realm)}\",{RenderBody(update.DataBlob)})";

    private static string RenderShared(string type, IReadOnlyList<string> realms, string data)
    {
        var keys = string.Join(',', realms.Select(realm => $"\"{EscapeLua(realm)}\""));
        return $"for _,r in ipairs({{{keys}}}) do select(2, ...).LoadData(\"{EscapeLua(type)}\",r,{RenderBody(data)}) end";
    }

    private static string Metadata(string payload, string type, string realm, long timestamp, string? auctionHouseId) =>
        $"{payload} --<{type},{realm},{timestamp}" + (auctionHouseId is null ? ">" : $",{auctionHouseId}>");

    private static string RenderBody(string data)
    {
        var blob = data.Trim();
        if (blob.StartsWith("return ", StringComparison.Ordinal))
            blob = blob[7..].TrimStart();
        var equals = string.Empty;
        while (blob.Contains($"]{equals}]", StringComparison.Ordinal))
            equals += "=";
        return $"[{equals}[return {blob}]{equals}]";
    }

    private static void ValidateKey(string type, string realm)
    {
        if (!AppDataCatalogParser.SupportedTypes.Contains(type))
            throw new InvalidDataException($"Unsupported AppData type '{type}'.");
        if (realm.IndexOfAny([',', '|', '\r', '\n']) >= 0)
            throw new InvalidDataException("AppData realm names cannot contain commas, pipes, or newlines.");
    }

    private static string EscapeLua(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record StoredEntry(string Type, string Realm, long Timestamp, string Payload, string? AuctionHouseId,
        string? SharedDataBlob = null);
}
