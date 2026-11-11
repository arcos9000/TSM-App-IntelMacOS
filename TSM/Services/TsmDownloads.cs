using System.IO.Compression;
using System.Text;

namespace TSM.Services;

internal static class TsmDownloads
{
    private const int MaximumDownloadedCharacters = 100_000_000;

    internal static Uri HttpsUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || uri.UserInfo.Length > 0)
            throw new InvalidDataException("The TradeSkillMaster service did not provide a remote HTTPS download URL.");
        return uri;
    }

    internal static async Task<string> ReadDataAsync(HttpClient httpClient, Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        Stream input = responseStream;
        var prefix = new byte[2];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var read = await responseStream.ReadAsync(prefix.AsMemory(prefixLength), cancellationToken);
            if (read == 0)
                break;
            prefixLength += read;
        }
        var combined = new PrefixStream(prefix.AsMemory(0, prefixLength), responseStream);
        if (prefixLength == 2 && prefix[0] == 0x1f && prefix[1] == 0x8b)
            input = new GZipStream(combined, CompressionMode.Decompress);
        else
            input = combined;

        using var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 8192, false);
        var result = new StringBuilder();
        var characters = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(characters, cancellationToken);
            if (read == 0)
                break;
            if (result.Length + read > MaximumDownloadedCharacters)
                throw new InvalidDataException("Downloaded data exceeded the 100 MB safety limit.");
            result.Append(characters, 0, read);
        }
        return result.ToString();
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream remainder) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = CopyPrefix(buffer.AsSpan(offset, count));
            return copied > 0 ? copied : remainder.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = CopyPrefix(buffer.Span);
            return copied > 0 ? copied : await remainder.ReadAsync(buffer, cancellationToken);
        }

        private int CopyPrefix(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, prefix.Length - _position);
            if (count <= 0)
                return 0;
            prefix.Span.Slice(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
