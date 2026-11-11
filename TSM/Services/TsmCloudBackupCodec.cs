using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

internal static partial class TsmCloudBackupCodec
{
    internal static async Task<byte[]> EncodeAsync(byte[] archive, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        var entry = zip.GetEntry("TradeSkillMaster.lua")
                    ?? throw new InvalidDataException("The local backup does not contain TradeSkillMaster.lua.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var document = new LuaSavedVariablesParser().Parse(await reader.ReadToEndAsync(cancellationToken));
        if (!document.TryGetTable("TradeSkillMasterDB", out var database))
            throw new InvalidDataException("The local backup does not contain a TradeSkillMaster database.");

        var types = new JsonObject();
        var values = EncodeTable(database, types, string.Empty);
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gzip, new JsonArray(values, types), cancellationToken: cancellationToken);
        return output.ToArray();
    }

    internal static JsonArray ReadOriginal(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray { Count: 2 } original ||
            original[0] is not JsonObject values || original[1] is not JsonObject)
            throw new InvalidDataException("The TradeSkillMaster cloud backup has an invalid database.");

        foreach (var (key, value) in values.ToArray())
        {
            if (!CsvFields().IsMatch(key) || value is not JsonValue text || !text.TryGetValue<string>(out var csv))
                continue;
            var rows = new JsonObject();
            foreach (var row in csv.Split('\n'))
                rows[row.TrimEnd('\r')] = "";
            values[key] = rows;
        }
        return original;
    }

    internal static void ApplyPatch(JsonArray database, string json)
    {
        if (JsonNode.Parse(json)?["diff"] is not JsonArray changes)
            throw new InvalidDataException("The TradeSkillMaster cloud backup has an invalid patch.");

        foreach (var change in changes)
        {
            var path = change?["path"]?.GetValue<string>();
            var operation = change?["op"]?.GetValue<string>();
            if (path is null || !path.StartsWith('/') || operation is not ("add" or "remove" or "replace"))
                throw new InvalidDataException("The TradeSkillMaster cloud backup uses an unsupported patch.");
            var keys = path[1..].Split('/').Select(UnescapePointer).ToArray();
            JsonNode? parent = database;
            foreach (var key in keys[..^1])
            {
                parent = parent switch
                {
                    JsonObject obj => obj[key],
                    JsonArray array => array[ReadIndex(key, array.Count, false)],
                    _ => throw new InvalidDataException("A cloud backup patch refers to a missing field.")
                };
            }

            var field = keys[^1];
            var value = change?["value"]?.DeepClone();
            if (parent is JsonObject target)
            {
                if (operation != "add" && !target.ContainsKey(field))
                    throw new InvalidDataException("A cloud backup patch refers to a missing field.");
                if (operation == "remove")
                    target.Remove(field);
                else
                    target[field] = value;
            }
            else if (parent is JsonArray array)
            {
                var index = ReadIndex(field, array.Count, operation == "add");
                if (operation == "add")
                    array.Insert(index, value);
                else if (operation == "remove")
                    array.RemoveAt(index);
                else
                    array[index] = value;
            }
            else
            {
                throw new InvalidDataException("A cloud backup patch refers to a missing field.");
            }
        }
    }

    internal static byte[] ToArchive(JsonArray database)
    {
        if (database.Count != 2 || database[0] is not JsonObject values || database[1] is not JsonObject types)
            throw new InvalidDataException("The cloud backup did not reconstruct a TradeSkillMaster database.");
        foreach (var (key, value) in values.ToArray())
        {
            if (CsvFields().IsMatch(key) && value is JsonObject rows)
                values[key] = string.Join('\n', rows.Select(row => row.Key));
        }

        var source = new StringBuilder("TradeSkillMasterDB = ");
        WriteLua(source, values, types, string.Empty, 0);
        source.Append('\n');
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("TradeSkillMaster.lua").Open(), new UTF8Encoding(false)))
            writer.Write(source.ToString());
        return output.ToArray();
    }

    private static JsonObject EncodeTable(LuaTable table, JsonObject types, string path)
    {
        var result = new JsonObject();
        foreach (var entry in table.Entries)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            if (TsmSnapshotUploadService.IsDesktopOnlyField(key, entry.Value, includeInventory: false))
                continue;
            var childPath = path + "/" + EscapePointer(key);
            types[childPath] = entry.Key is string ? "string" : "number";
            result[key] = entry.Value is LuaTable nested
                ? EncodeTable(nested, types, childPath)
                : JsonSerializer.SerializeToNode(entry.Value);
        }
        return result;
    }

    private static void WriteLua(StringBuilder output, JsonNode? value, JsonObject types, string path, int depth)
    {
        if (depth > 256)
            throw new InvalidDataException("The cloud backup database is nested too deeply.");
        switch (value)
        {
            case JsonObject fields:
                WriteTable(fields);
                break;
            case JsonArray values:
                WriteTable(values.Select((node, index) => KeyValuePair.Create(
                    (index + 1).ToString(CultureInfo.InvariantCulture), node)));
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                WriteString(output, text);
                break;
            default:
                output.Append(value?.ToJsonString() ?? "nil");
                break;
        }

        void WriteTable(IEnumerable<KeyValuePair<string, JsonNode?>> fields)
        {
            output.Append("{\n");
            foreach (var (key, child) in fields)
            {
                var childPath = path + "/" + EscapePointer(key);
                output.Append('\t', depth + 1).Append('[');
                if (types[childPath]?.GetValue<string>() == "number" || value is JsonArray)
                {
                    if (!double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        !double.IsFinite(number))
                        throw new InvalidDataException("The cloud backup contains an invalid numeric key.");
                    output.Append(key);
                }
                else
                {
                    WriteString(output, key);
                }
                output.Append("] = ");
                WriteLua(output, child, types, childPath, depth + 1);
                output.Append(",\n");
            }
            output.Append('\t', depth).Append('}');
        }
    }

    private static void WriteString(StringBuilder output, string text)
    {
        output.Append('"');
        foreach (var character in text)
        {
            if (character is '"' or '\\')
                output.Append('\\').Append(character);
            else if (character < ' ' || character == '\u007f')
                output.Append('\\').Append(((int)character).ToString("D3", CultureInfo.InvariantCulture));
            else
                output.Append(character);
        }
        output.Append('"');
    }

    private static int ReadIndex(string key, int count, bool allowAppend)
    {
        if (allowAppend && key == "-")
            return count;
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            (allowAppend ? index <= count : index < count))
            return index;
        throw new InvalidDataException("A cloud backup patch uses an invalid array index.");
    }

    private static string EscapePointer(string key) => key.Replace("~", "~0").Replace("/", "~1");
    private static string UnescapePointer(string key) => key.Replace("~1", "/").Replace("~0", "~");

    [GeneratedRegex(@"^(?:r@.*@internalData@csv.*|f@.*@internalData@guildGoldLog/.*|s@.*@internalData@goldLog)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CsvFields();
}
