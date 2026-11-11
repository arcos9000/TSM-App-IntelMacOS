using System.Globalization;
using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

internal sealed partial class TsmSnapshotUploadService(
    TsmDesktopApiClient apiClient, LuaSavedVariablesParser parser, Func<string> systemId) : ITsmDataUploadService
{
    private readonly Dictionary<(string? User, string Path), DateTime> _uploaded = new();

    public async Task<int> UploadAsync(IReadOnlyList<TsmAccount> accounts, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string>? gameTypes = null;
        var count = 0;
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(account.SavedVariablesPath) || !File.Exists(account.AppHelperSavedVariablesPath))
                continue;

            var savedAt = File.GetLastWriteTimeUtc(account.SavedVariablesPath);
            var key = (apiClient.AccountId, account.SavedVariablesPath);
            if (savedAt <= _uploaded.GetValueOrDefault(key))
                continue;

            var folder = new FileInfo(account.SavedVariablesPath).Directory?.Parent?.Parent?.Parent?.Parent?.Name;
            if (folder is null)
                continue;
            gameTypes ??= await apiClient.GetGameTypesAsync(cancellationToken);
            if (!gameTypes.TryGetValue(folder, out var gameType))
                continue;

            var helper = parser.ParseFile(account.AppHelperSavedVariablesPath);
            if (!helper.TryGetTable("TradeSkillMaster_AppHelperDB", out var helperDb) ||
                !helperDb.TryGetString("region", out var region) || string.IsNullOrWhiteSpace(region))
                continue;
            var tsm = parser.ParseFile(account.SavedVariablesPath);
            if (!tsm.TryGetTable("TradeSkillMasterDB", out var tsmDb))
                continue;

            var payload = new
            {
                account = account.AccountName,
                gameType,
                tsmDB = ToJson(tsmDb),
                appHelperDB = ToJson(helperDb),
                saveAge = Math.Max(0, (long)(DateTime.UtcNow - savedAt).TotalSeconds),
                systemId = systemId()
            };
            await apiClient.UploadAsync(new TsmUploadRequest("sv-snapshots", [], payload), cancellationToken);
            _uploaded[key] = savedAt;
            count++;
        }
        return count;
    }

    private static object ToJson(LuaTable table)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in table.Entries)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            if (IsDesktopOnlyField(key, entry.Value, includeInventory: true))
                continue;
            fields[key] = entry.Value is LuaTable nested ? ToJson(nested) : entry.Value;
        }

        // Preserve the zero-indexed arrays emitted by the desktop snapshot protocol.
        var values = new object?[fields.Count];
        for (var index = 0; index < values.Length; index++)
        {
            if (!fields.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out values[index]))
                return fields;
        }
        return values.Length > 0 ? values : fields;
    }

    internal static bool IsDesktopOnlyField(string key, object? value, bool includeInventory) =>
        DesktopOnlyFields().IsMatch(key) ||
        (key is "data" or "names" or "itemStrings" && value is string) ||
        (!includeInventory && InventoryFields().IsMatch(key));

    [GeneratedRegex(@"^(?:g@ @(?:auction|banking|crafting|destroying|mailing|taskList|vendoring)UIContext@|p@.*@(?:auction|banking|crafting|mailing|main|vendoring)UIContext@|f@.*@gatheringContext@|f@.*@internalData@(?:guildVaults|pendingMail|expiringMail|expiringAuction|csvAuctionDBScan|auctionDBScanTime|auctionDBScanHash)$|c@.*@internalData@|s@.*@internalData@reagentBankQuantity$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DesktopOnlyFields();

    [GeneratedRegex(@"^s@.*@internalData@(?:bag|bank|auction|mail)Quantity$", RegexOptions.CultureInvariant)]
    private static partial Regex InventoryFields();
}
