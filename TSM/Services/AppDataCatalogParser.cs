using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

public sealed partial class AppDataCatalogParser
{
    public static IReadOnlySet<string> SupportedTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "AUCTIONDB_NON_COMMODITY_DATA",
        "AUCTIONDB_NON_COMMODITY_SCAN_STAT",
        "AUCTIONDB_NON_COMMODITY_HISTORICAL",
        "AUCTIONDB_COMMODITY_DATA",
        "AUCTIONDB_COMMODITY_SCAN_STAT",
        "AUCTIONDB_COMMODITY_HISTORICAL",
        "AUCTIONDB_REGION_STAT",
        "AUCTIONDB_REGION_HISTORICAL",
        "AUCTIONDB_REGION_SALE",
        "SHOPPING_SEARCHES",
        "GROUP_IMPORTS",
        "APP_INFO"
    };

    public AppDataCatalog Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var entries = new List<AppDataEntry>();
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            var match = MetadataLine().Match(line.Trim());
            if (!match.Success)
                continue;

            var type = match.Groups["type"].Value;
            if (!SupportedTypes.Contains(type) ||
                !long.TryParse(match.Groups["timestamp"].Value, out var timestamp))
                continue;

            var realm = match.Groups["realm"].Value.Trim();
            var payload = match.Groups["payload"].Value.TrimEnd();
            var house = match.Groups["house"].Success ? match.Groups["house"].Value : null;
            if (realm.Contains('|') && GroupedPayload().Match(payload) is { Success: true } grouped)
            {
                var data = grouped.Groups["data"].Value;
                foreach (var alias in realm.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    entries.Add(new AppDataEntry(type, alias, timestamp, payload, house, data));
            }
            else
            {
                entries.Add(new AppDataEntry(type, realm, timestamp, payload, house));
            }
        }

        return new AppDataCatalog(entries);
    }

    public AppDataCatalog ParseFile(string path) => Parse(File.ReadAllText(path));

    [GeneratedRegex(@"^(?<payload>.*?)\s+--<(?<type>[A-Z_][A-Z_0-9]*),(?<realm>[^,>]*),(?<timestamp>\d+)(?:,(?<house>[^,>]+))?>\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MetadataLine();

    [GeneratedRegex("""^for _,r in ipairs\(\{.*?\}\) do select\(2, \.\.\.\)\.LoadData\("[^"]+",r,\[(?<delimiter>=*)\[return (?<data>.*)\]\k<delimiter>\]\) end$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex GroupedPayload();
}
