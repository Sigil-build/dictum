using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dictum.TestPlugin;

/// <summary>A request as <see cref="TestHttpServer"/> read it: the path without its query, the headers, and the whole body.</summary>
internal sealed record TestHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>An answer, and what happens to the connection once it has gone out.</summary>
internal sealed record TestHttpResponse(int Status, byte[] Body, string? ContentType)
{
    /// <summary>
    /// Promises this many bytes of body, more than it sends, and drops the connection after the body: an answer that
    /// cannot be read.
    /// </summary>
    public int? PromisedLength { get; init; }

    /// <summary>Runs once the answer has gone out, as ASP.NET Core's <c>OnCompleted</c> does.</summary>
    public Action? Sent { get; init; }

    public static TestHttpResponse Empty(int status) => new(status, [], null);

    public static TestHttpResponse Text(int status, string text, string contentType) => new(status, Encoding.UTF8.GetBytes(text), contentType);
}

/// <summary>
/// The test plugin's HTTP/1.1 server on 127.0.0.1: what protocol 1's requests need and no more, over a socket of its own.
/// It keeps a connection open between requests, as Dictum's client expects, and reads a body by its Content-Length.
/// ASP.NET Core would serve as well, but loading and compiling it costs several times the runtime's own start, which
/// on a 2-core runner short of memory stretched a plugin's start to over 10 s.
/// </summary>
internal sealed class TestHttpServer(Func<TestHttpRequest, TestHttpResponse> handle) : IDisposable
{
    private const int MaxHead = 64 * 1024;
    private static readonly byte[] EndOfHead = "\r\n\r\n"u8.ToArray();

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public string Url => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public void Start()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public void Dispose() => _listener.Dispose();

    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Stopped.
                return;
            }

            _ = ServeAsync(socket);
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        using (socket)
        {
            var stream = new NetworkStream(socket, ownsSocket: false);
            await using (stream)
            {
                var reader = new RequestReader(stream);
                try
                {
                    while (await reader.ReadAsync() is { } request)
                    {
                        var response = handle(request);
                        await WriteAsync(stream, response);
                        if (response.PromisedLength is not null)
                        {
                            return;
                        }

                        response.Sent?.Invoke();
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or FormatException)
                {
                    // The client went away, or sent what this server does not read; either way the connection ends.
                }
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, TestHttpResponse response)
    {
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {ReasonPhrase(response.Status)}\r\n");
        if (response.ContentType is { } type)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: {type}\r\n");
        }

        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.PromisedLength ?? response.Body.Length}\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.WriteAsync(response.Body);
        await stream.FlushAsync();
    }

    /// <summary>The phrases ASP.NET Core sends, for the statuses the tests use; an empty phrase is valid HTTP for the rest.</summary>
    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "",
    };

    /// <summary>Reads one request after another off a connection, keeping what arrived past the end of the last one.</summary>
    private sealed class RequestReader(NetworkStream stream)
    {
        private byte[] _buffer = new byte[8192];
        private int _count;

        /// <summary>The next request, or null once the client has closed the connection between requests.</summary>
        /// <exception cref="FormatException">The request is not one this server reads, such as a chunked body.</exception>
        public async Task<TestHttpRequest?> ReadAsync()
        {
            int headLength;
            while ((headLength = _buffer.AsSpan(0, _count).IndexOf(EndOfHead)) < 0)
            {
                if (_count >= MaxHead)
                {
                    throw new FormatException("The request's head is too long.");
                }

                if (!await FillAsync())
                {
                    return _count == 0 ? null : throw new FormatException("The connection closed inside a request's head.");
                }
            }

            var lines = Encoding.Latin1.GetString(_buffer, 0, headLength).Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length != 3)
            {
                throw new FormatException($"Not a request line: {lines[0]}");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            if (headers.ContainsKey("Transfer-Encoding"))
            {
                throw new FormatException("This server reads a body by its Content-Length only.");
            }

            var bodyLength = headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, CultureInfo.InvariantCulture) : 0;
            Consume(headLength + EndOfHead.Length);
            while (_count < bodyLength)
            {
                if (!await FillAsync())
                {
                    throw new FormatException("The connection closed inside a request's body.");
                }
            }

            var body = _buffer.AsSpan(0, bodyLength).ToArray();
            Consume(bodyLength);
            var path = start[1].Split('?')[0];
            return new TestHttpRequest(start[0], path, headers, body);
        }

        private async Task<bool> FillAsync()
        {
            if (_count == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_count));
            _count += read;
            return read > 0;
        }

        private void Consume(int bytes)
        {
            _buffer.AsSpan(bytes, _count - bytes).CopyTo(_buffer);
            _count -= bytes;
        }
    }
}

/// <summary>The fields and the file of a <c>multipart/form-data</c> body, read as ASP.NET Core's form reader reads them.</summary>
internal sealed class TestForm
{
    private readonly Dictionary<string, List<string>> _fields = new(StringComparer.OrdinalIgnoreCase);

    private TestForm()
    {
    }

    public string? FileName { get; private set; }

    public string? FileContentType { get; private set; }

    public int FileLength { get; private set; }

    /// <summary>The field's values, joined by commas as ASP.NET Core's <c>StringValues</c> prints them; empty when absent.</summary>
    public string this[string name] => _fields.TryGetValue(name, out var values) ? string.Join(',', values) : "";

    /// <exception cref="FormatException">The body is not multipart, or has no boundary.</exception>
    public static TestForm Read(string? contentType, byte[] body)
    {
        var boundary = Parameters(contentType ?? "").GetValueOrDefault("boundary")
            ?? throw new FormatException("The form has no boundary.");
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var nextPart = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        var form = new TestForm();
        var span = body.AsSpan();
        var at = span.IndexOf(delimiter);
        while (at >= 0)
        {
            at += delimiter.Length;
            if (span[at..].StartsWith("--"u8))
            {
                break;
            }

            at += 2;
            var headLength = span[at..].IndexOf("\r\n\r\n"u8);
            var head = Encoding.UTF8.GetString(span.Slice(at, headLength)).Split("\r\n");
            var contentStart = at + headLength + 4;
            var contentLength = span[contentStart..].IndexOf(nextPart);
            form.Add(head, span.Slice(contentStart, contentLength));
            at = contentStart + contentLength + 2;
        }

        return form;
    }

    private void Add(string[] head, ReadOnlySpan<byte> content)
    {
        var headers = head.Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var disposition = Parameters(headers.GetValueOrDefault("Content-Disposition") ?? "");
        var name = disposition.GetValueOrDefault("name") ?? "";
        var fileName = disposition.GetValueOrDefault("filename*") is { } star
            ? Uri.UnescapeDataString(star[(star.IndexOf("''", StringComparison.Ordinal) + 2)..])
            : disposition.GetValueOrDefault("filename");
        if (fileName is not null)
        {
            FileName = fileName;
            FileContentType = headers.GetValueOrDefault("Content-Type");
            FileLength = content.Length;
            return;
        }

        if (!_fields.TryGetValue(name, out var values))
        {
            _fields[name] = values = [];
        }

        values.Add(Encoding.UTF8.GetString(content));
    }

    /// <summary>The <c>key=value</c> parameters of a header such as Content-Type, unquoted.</summary>
    private static Dictionary<string, string> Parameters(string header) =>
        header.Split(';').Skip(1).Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim().Trim('"'), StringComparer.OrdinalIgnoreCase);
}
