using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using mirr.Media;

namespace mirr.Engine;

public sealed record AccessUnitMessage(byte Kind, byte[] AnnexB);

public delegate void AccessUnitHandler(AccessUnitMessage message);

public sealed class ScreenStreamClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly EngineHandle _engine;

    public ScreenStreamClient(EngineHandle engine)
    {
        _engine = engine;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", engine.BasicAuthToken);
        _http.DefaultRequestHeaders.ConnectionClose = false;
    }

    public Uri BaseUri => new($"http://127.0.0.1:{_engine.Port}");

    public async Task<CodecDescription?> FetchCodecAsync(CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 60 && !cancellation.IsCancellationRequested; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await _http.GetAsync(
                    new Uri(BaseUri, "/codec"),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellation);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellation);
                    using JsonDocument document = JsonDocument.Parse(body);
                    string? codec = document.RootElement.TryGetProperty("codec", out JsonElement codecElement)
                        ? codecElement.GetString()
                        : null;
                    string? description = document.RootElement.TryGetProperty("description", out JsonElement descriptionElement)
                        ? descriptionElement.GetString()
                        : null;
                    if (CodecDescription.Parse(codec, description) is { } parsed)
                    {
                        return parsed;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
        }
        return null;
    }

    public async Task RequestKeyframeAsync(CancellationToken cancellation)
    {
        try
        {
            using HttpResponseMessage response = await _http.PostAsync(
                new Uri(BaseUri, "/pli"),
                new ByteArrayContent([]),
                cancellation);
            _ = response;
        }
        catch (Exception)
        {
        }
    }

    public async Task ReadStreamAsync(AccessUnitHandler onAccessUnit, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            new Uri(BaseUri, "/stream.bin"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellation);
        response.EnsureSuccessStatusCode();

        Stream body = await response.Content.ReadAsStreamAsync(cancellation);
        await using (body.ConfigureAwait(false))
        {
            while (!cancellation.IsCancellationRequested)
            {
                byte[]? message = await ReadMessageAsync(body, cancellation);
                if (message is null)
                {
                    return;
                }
                byte kind = message[0];
                byte[] payload = message[1..];
                byte[]? annexB = BuildAnnexB(payload);
                if (annexB is null)
                {
                    continue;
                }
                onAccessUnit(new AccessUnitMessage(kind, annexB));
            }
        }
    }

    private static async Task<byte[]?> ReadMessageAsync(Stream body, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(body, header, cancellation))
        {
            return null;
        }
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > (1 << 26))
        {
            return null;
        }
        var message = new byte[length];
        if (!await ReadExactAsync(body, message, cancellation))
        {
            return null;
        }
        return message;
    }

    private static async Task<bool> ReadExactAsync(Stream body, byte[] target, CancellationToken cancellation)
    {
        int offset = 0;
        while (offset < target.Length)
        {
            int read = await body.ReadAsync(target.AsMemory(offset), cancellation);
            if (read <= 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static byte[]? BuildAnnexB(byte[] payload)
    {
        List<byte[]> nals = NalUtils.SplitLengthPrefixed(payload);
        if (nals.Count == 0)
        {
            return null;
        }
        return NalUtils.ToAnnexB(nals);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
