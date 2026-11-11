using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TSM.Models;

namespace TSM.Services;

public sealed partial class TsmDesktopApiClient
{
    private const string BackupsUrl = DesktopUrl + "/backups";
    private static readonly (string Suffix, string Folder)[] BackupGameFolders =
    [
        ("-Forever", "_forever_"), ("-Classic", "_classic_era_"),
        ("-Progression", "_classic_"), ("-Anniversary", "_anniversary_")
    ];

    public async Task<TsmBackupCatalog> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var backups = await ReadItemsAsync<CloudBackup>(BackupsUrl, session, cancellationToken);
        return new TsmBackupCatalog(backups.Select(backup => ToRemoteBackup(backup, session.Subject)).ToArray(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<byte[]> DownloadBackupAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var catalog = await ReadItemsAsync<CloudBackup>(BackupsUrl, session, cancellationToken);
        var selected = catalog.FirstOrDefault(backup => ToRemoteBackup(backup, session.Subject).RemoteName == remoteName)
                       ?? throw new InvalidOperationException("The cloud backup is no longer available.");
        var byId = catalog.ToDictionary(backup => backup.BackupId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patches = new Stack<CloudBackup>();
        var current = selected;
        while (current.Type != "original")
        {
            if (current.Type != "patch" || !seen.Add(current.BackupId) || current.PreviousId is null ||
                !byId.TryGetValue(current.PreviousId, out var previous) ||
                previous.SystemId != selected.SystemId || previous.GameVersion != selected.GameVersion ||
                previous.Account != selected.Account)
                throw new InvalidDataException("The TradeSkillMaster cloud backup chain is incomplete.");
            patches.Push(current);
            current = previous;
        }

        var original = TsmCloudBackupCodec.ReadOriginal(await DownloadDataAsync(current.Url, cancellationToken));
        foreach (var patch in patches)
            TsmCloudBackupCodec.ApplyPatch(original, await DownloadDataAsync(patch.Url, cancellationToken));
        return TsmCloudBackupCodec.ToArchive(original);
    }

    public async Task UploadBackupAsync(string remoteName, byte[] archive, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        var parts = ReadLocalBackupName(remoteName);
        var backupId = StableBackupId(session.Subject, remoteName);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(parts.Timestamp)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var data = await TsmCloudBackupCodec.EncodeAsync(archive, cancellationToken);

        using var reserve = await SendAsync(HttpMethod.Post, BackupsUrl + "/uploads", session,
            new { userId = session.Subject, backupId, type = "original", timestamp }, cancellationToken);
        var target = await ReadAsync<BackupUpload>(reserve, cancellationToken);
        await UploadFileAsync(target.Url, data, cancellationToken);
        using var complete = await SendAsync(HttpMethod.Post, BackupsUrl, session, new
        {
            userId = session.Subject, type = "original", backupId, previousId = (string?)null,
            gameVersion = parts.Folder, account = parts.Account, systemId = parts.SystemId,
            description = "TSM Desktop backup", timestamp
        }, cancellationToken);
    }

    private static TsmRemoteBackup ToRemoteBackup(CloudBackup backup, string userId)
    {
        var suffix = BackupGameFolders.FirstOrDefault(game => game.Folder == backup.GameVersion).Suffix;
        var local = new TsmRemoteBackup(backup.SystemId, backup.Account + suffix,
            backup.Timestamp.ToUnixTimeSeconds(), backup.Keep);
        return string.Equals(StableBackupId(userId, local.RemoteName), backup.BackupId, StringComparison.OrdinalIgnoreCase)
            ? local
            : local with { ArchiveId = backup.BackupId };
    }

    private static (string SystemId, string Account, string Folder, long Timestamp) ReadLocalBackupName(string name)
    {
        var first = name.IndexOf('_');
        var last = name.LastIndexOf('_');
        if (first <= 0 || last <= first + 1 || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(name.AsSpan(last + 1, name.Length - last - 5), NumberStyles.None,
                CultureInfo.InvariantCulture, out var timestamp))
            throw new InvalidDataException("Invalid local backup name.");

        var systemId = name[..first];
        var account = name[(first + 1)..last];
        foreach (var (suffix, folder) in BackupGameFolders)
        {
            if (account.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (systemId, account[..^suffix.Length], folder, timestamp);
        }
        return (systemId, account, "_retail_", timestamp);
    }

    private static string StableBackupId(string userId, string remoteName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}/{remoteName}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x40);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private sealed record CloudBackup(string BackupId, string? PreviousId, string SystemId, string Account,
        string GameVersion, string Type, DateTimeOffset Timestamp, string Url, bool Keep);
    private sealed record BackupUpload(string Url);
}
