using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TSM.Services;

internal static class TsmBrowserSignIn
{
    internal const string Issuer = "https://id.tradeskillmaster.com/realms/app";
    internal const string ClientId = "desktop-app";
    internal const string OpenId = Issuer + "/protocol/openid-connect";
    private const string CallbackPath = "/auth/callback";

    internal static async Task<Dictionary<string, string>> AuthorizeAsync(
        Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = new Uri($"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}{CallbackPath}");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect.AbsoluteUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = "openid",
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        };
        openBrowser(new Uri(OpenId + "/auth?" + string.Join('&', parameters.Select(pair =>
            pair.Key + "=" + Uri.EscapeDataString(pair.Value)))));

        try
        {
            var code = await ReadCallbackAsync(listener, redirect, state, timeout.Token);
            return new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect.AbsoluteUri,
                ["code_verifier"] = verifier
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Sign-in timed out. Open the browser again to retry.");
        }
    }

    internal static void OpenBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> ReadCallbackAsync(TcpListener listener, Uri redirect, string state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, requestTimeout.Token);
                var parts = requestLine.Split(' ');
                var query = parts.Length == 3 && parts[0] == "GET" &&
                            parts[1].StartsWith(CallbackPath + "?", StringComparison.Ordinal) &&
                            Uri.TryCreate(redirect, parts[1], out var callback)
                    ? System.Web.HttpUtility.ParseQueryString(callback.Query)
                    : null;
                if (query?["state"] != state ||
                    (query["iss"] is { } issuer && issuer != Issuer))
                {
                    await ReplyAsync(stream, false, "This request does not match the current sign-in.",
                        requestTimeout.Token);
                    continue;
                }

                var code = query["code"];
                if (query["error"] is not null || string.IsNullOrWhiteSpace(code))
                {
                    await ReplyAsync(stream, false, "Sign-in was cancelled. Return to TSM Desktop to retry.",
                        requestTimeout.Token);
                    throw new InvalidOperationException("TradeSkillMaster sign-in was cancelled.");
                }

                await ReplyAsync(stream, true, "Sign-in received. Return to TSM Desktop.", requestTimeout.Token);
                return code;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private static async Task<string> ReadRequestLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            if (newline >= 0)
                return Encoding.ASCII.GetString(buffer, 0, newline).TrimEnd('\r');
            length += read;
        }
        return string.Empty;
    }

    private static async Task ReplyAsync(Stream stream, bool success, string message,
        CancellationToken cancellationToken)
    {
        var status = success ? "200 OK" : "400 Bad Request";
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Cache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
