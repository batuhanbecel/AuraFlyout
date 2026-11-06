using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using AuraFlyout.Core;

namespace AuraFlyout.Modules.SpeedTest;

public enum Phase { Preparing, Ping, Download, Upload, Done }

/// <summary>One measurement. Speeds in Mbps, latencies in ms; -1 = not measured.</summary>
public sealed class SpeedResult
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DateTime Time { get; set; }                 // UTC
    public double PingMs { get; set; } = -1;
    public double JitterMs { get; set; } = -1;
    public double DownMbps { get; set; } = -1;
    public double UpMbps { get; set; } = -1;
    public string Server { get; set; } = "";           // Cloudflare data center, "Istanbul (IST)"
    public string Isp { get; set; } = "";
    public string Link { get; set; } = "";             // "Wi-Fi", "Ethernet" or the adapter name
    public string? Error { get; set; }                 // null when the test went through

    public bool Complete => Error == null && DownMbps >= 0 && UpMbps >= 0;

    public SpeedResult Clone() => (SpeedResult)MemberwiseClone();

    /// <summary>History entry, same format as TaskbarSpeedTest: "ticks|ping|jitter|down|up|server|isp|link".</summary>
    public string Serialize() =>
        string.Join("|", Time.Ticks.ToString(Inv), PingMs.ToString("0.##", Inv), JitterMs.ToString("0.##", Inv),
            DownMbps.ToString("0.##", Inv), UpMbps.ToString("0.##", Inv), Clean(Server), Clean(Isp), Clean(Link));

    public static SpeedResult? Parse(string s)
    {
        string[] p = s.Split('|');
        if (p.Length < 5) return null;
        try
        {
            return new SpeedResult
            {
                Time = new DateTime(long.Parse(p[0], Inv), DateTimeKind.Utc),
                PingMs = double.Parse(p[1], Inv),
                JitterMs = double.Parse(p[2], Inv),
                DownMbps = double.Parse(p[3], Inv),
                UpMbps = double.Parse(p[4], Inv),
                Server = p.Length > 5 ? p[5] : "",
                Isp = p.Length > 6 ? p[6] : "",
                Link = p.Length > 7 ? p[7] : "",
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string Clean(string? s) => (s ?? "").Replace('|', ' ').Replace(';', ' ');
}

/// <summary>A progress report. Result holds the values of the phases finished so far.</summary>
public sealed class TestStatus
{
    public Phase Phase { get; init; }
    public double Live { get; init; }      // Mbps right now, or the last round trip in ms during the ping phase
    public double Progress { get; init; }  // 0..1 within the phase
    public bool Running { get; init; }
    public bool Cancelled { get; init; }
    public SpeedResult Result { get; init; } = new();
}

/// <summary>
/// Measures latency, download and upload against Cloudflare's speed test (speed.cloudflare.com).
/// Port of TaskbarSpeedTest's engine to HttpClient: same phases, stream counts, request sizes,
/// warm-up handling and 429 fallback. Changed fires ~10x per second from the test thread.
/// </summary>
public sealed class SpeedTestEngine
{
    private const string Host = "speed.cloudflare.com";
    private const int PingCount = 12;
    // Requests of 10 MB and more count toward an hourly server-side limit; just under it keeps
    // the overhead negligible. If refused anyway, streams fall back to smaller requests.
    private const long Chunk = 9_999_999, MinChunk = 1_000_000;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version11,
    };

    private static byte[]? _payload;

    public int DownloadStreams { get; set; } = 6;
    public int UploadStreams { get; set; } = 4;
    public int Seconds { get; set; } = 10; // per direction

    public event Action<TestStatus>? Changed;

    private volatile Thread? _thread;
    private volatile bool _cancel;
    private volatile string? _streamError;
    private CancellationTokenSource? _testCts;
    private CancellationTokenSource? _phaseCts;
    private long _transferred, _requestCount, _chunk;
    private SpeedResult _result = new();

    public bool Running => _thread != null;

    public void Start()
    {
        if (_thread != null) return;
        _cancel = false;
        _testCts = new CancellationTokenSource();
        _result = new SpeedResult { Time = DateTime.UtcNow };
        _thread = new Thread(Run) { IsBackground = true, Name = "SpeedTest" };
        _thread.Start();
    }

    public void Cancel()
    {
        _cancel = true;
        try { _testCts?.Cancel(); } catch { /* disposed */ }
    }

    private static bool Turkish => L10n.ActiveLanguage == "tr";
    private static string T(string en, string tr) => Turkish ? tr : en;

    private void Run()
    {
        var r = _result;
        try
        {
            Report(Phase.Preparing, 0, 0);
            if (!NetworkInterface.GetIsNetworkAvailable())
                throw new TestException(T("No network connection.", "Ağ bağlantısı yok."));

            r.Link = NetworkInfo.Describe();
            ReadMeta(r);
            MeasurePing(r);
            r.DownMbps = MeasureThroughput(Phase.Download);
            r.UpMbps = MeasureThroughput(Phase.Upload);
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "speed test done: ping {0:0.0} ms, jitter {1:0.0} ms, down {2:0.0} Mbps, up {3:0.0} Mbps, {4}",
                r.PingMs, r.JitterMs, r.DownMbps, r.UpMbps, r.Server));
        }
        catch (Exception ex)
        {
            r.Error = _cancel ? T("Test stopped.", "Test durduruldu.") : ex is TestException ? ex.Message : Describe(ex);
            if (!_cancel) Log.Warn("speed test failed", ex);
        }
        _thread = null;
        Report(Phase.Done, 0, 1);
    }

    private void Report(Phase phase, double live, double progress) =>
        Changed?.Invoke(new TestStatus
        {
            Phase = phase,
            Live = live,
            Progress = progress,
            Running = _thread != null,
            Cancelled = _cancel,
            Result = _result.Clone(),
        });

    private void Check()
    {
        if (_cancel) throw new OperationCanceledException();
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url) { Version = HttpVersion.Version11 };
        req.Headers.UserAgent.ParseAdd("AuraFlyout/1.0");
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        return req;
    }

    // Who and where: the ISP and the Cloudflare data center that answers (also warms up DNS and TLS).
    private void ReadMeta(SpeedResult r)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_testCts!.Token);
            cts.CancelAfter(15000);
            using var req = NewRequest(HttpMethod.Get, $"https://{Host}/meta");
            req.Headers.Referrer = new Uri($"https://{Host}/"); // the endpoint answers {} without it
            using var resp = Http.Send(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            string json = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            r.Isp = Text(root, "asOrganization");
            if (root.TryGetProperty("colo", out var colo))
            {
                string iata = colo.ValueKind == JsonValueKind.Object ? Text(colo, "iata") : colo.ValueKind == JsonValueKind.String ? colo.GetString() ?? "" : "";
                string city = colo.ValueKind == JsonValueKind.Object ? Text(colo, "city") : "";
                r.Server = city.Length > 0 ? city + (iata.Length > 0 ? $" ({iata})" : "") : iata;
            }
        }
        catch (HttpRequestException ex)
        {
            Check();
            // not reaching the server at all ends the test here; anything else only loses the names
            if (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
                throw new TestException(Describe(ex));
            Log.Warn("speed meta: " + ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or TaskCanceledException)
        {
            Check();
            Log.Warn("speed meta: " + ex.Message);
        }
    }

    private static string Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    // Latency from TCP handshakes with the server; median = ping, mean consecutive change = jitter.
    // A handshake far above the rest had its SYN resent: a lost packet, not latency.
    private void MeasurePing(SpeedResult r)
    {
        Report(Phase.Ping, 0, 0);
        IPAddress[] addresses;
        try { addresses = Dns.GetHostAddresses(Host); }
        catch (SocketException)
        {
            throw new TestException(T("Could not find the speed test server. Are you online?", "Hız testi sunucusu bulunamadı. İnternete bağlı mısın?"));
        }
        if (addresses.Length == 0) throw new TestException(T("Could not find the speed test server.", "Hız testi sunucusu bulunamadı."));
        IPAddress ip = addresses[0];

        var samples = new List<double>();
        for (int i = 0; i < PingCount; i++)
        {
            Check();
            double ms = Handshake(ip);
            if (ms >= 0) samples.Add(ms);
            Report(Phase.Ping, ms >= 0 ? ms : 0, (i + 1) / (double)PingCount);
            Thread.Sleep(40);
        }
        if (samples.Count == 0) throw new TestException(T("The speed test server did not answer.", "Hız testi sunucusu yanıt vermedi."));

        double typical = Median(samples);
        var kept = samples.Where(v => v <= typical * 3 + 20).ToList();
        r.PingMs = Median(kept);
        double sum = 0;
        for (int i = 1; i < kept.Count; i++) sum += Math.Abs(kept[i] - kept[i - 1]);
        r.JitterMs = kept.Count > 1 ? sum / (kept.Count - 1) : 0;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Handshake(IPAddress ip)
    {
        using var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(3000);
            var clock = Stopwatch.StartNew();
            s.ConnectAsync(ip, 443, cts.Token).AsTask().GetAwaiter().GetResult();
            return clock.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    // Parallel streams for Seconds; the result averages the steady part (first quarter left out).
    private double MeasureThroughput(Phase phase)
    {
        bool upload = phase == Phase.Upload;
        Report(phase, 0, 0);
        Interlocked.Exchange(ref _transferred, 0);
        Interlocked.Exchange(ref _requestCount, 0);
        Interlocked.Exchange(ref _chunk, Chunk);
        _streamError = null;
        _phaseCts = CancellationTokenSource.CreateLinkedTokenSource(_testCts!.Token);
        var token = _phaseCts.Token;

        var workers = new List<Task>();
        for (int i = 0; i < (upload ? UploadStreams : DownloadStreams); i++)
            workers.Add(Task.Run(() => PumpAsync(upload, token)));

        double warmup = Seconds * 0.25;
        var window = new Queue<KeyValuePair<double, long>>();
        double from = -1, to = 0, live = 0;
        long fromBytes = 0, toBytes = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                Thread.Sleep(100);
                Check();
                double t = clock.Elapsed.TotalSeconds;
                long bytes = Interlocked.Read(ref _transferred);
                window.Enqueue(new KeyValuePair<double, long>(t, bytes));
                while (t - window.Peek().Key > 1.0) window.Dequeue();
                var first = window.Peek();
                if (t - first.Key > 0.05) live = Mbps(bytes - first.Value, t - first.Key);
                if (from < 0 && t >= warmup)
                {
                    from = t;
                    fromBytes = bytes;
                }
                to = t;
                toBytes = bytes;
                Report(phase, live, Math.Min(1, t / Seconds));
                if (t >= Seconds || workers.All(w => w.IsCompleted)) break;
            }
        }
        finally
        {
            _phaseCts.Cancel();
            try { Task.WaitAll(workers.ToArray(), 2000); } catch { /* cancelled */ }
            _phaseCts.Dispose();
            _phaseCts = null;
        }

        if (toBytes == 0)
            throw new TestException(_streamError ?? T("No data could be transferred.", "Veri aktarılamadı."));
        if (from < 0 || to - from < 0.5) return Mbps(toBytes, to); // every stream gave up early
        return Mbps(toBytes - fromBytes, to - from);
    }

    // One stream: requests back to back on a kept-alive connection until the phase ends.
    private async Task PumpAsync(bool upload, CancellationToken phaseToken)
    {
        var buffer = new byte[64 * 1024];
        int failures = 0;
        while (!phaseToken.IsCancellationRequested)
        {
            long size = Interlocked.Read(ref _chunk);
            Interlocked.Increment(ref _requestCount);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(phaseToken);
            idle.CancelAfter(15000); // like the original Timeout / ReadWriteTimeout
            try
            {
                if (upload)
                {
                    using var req = NewRequest(HttpMethod.Post, $"https://{Host}/__up");
                    req.Content = new PayloadContent(size, Payload(), n =>
                    {
                        Interlocked.Add(ref _transferred, n);
                        idle.CancelAfter(15000);
                    });
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                    if (!resp.IsSuccessStatusCode) throw new SpeedHttpException(resp);
                }
                else
                {
                    using var req = NewRequest(HttpMethod.Get, $"https://{Host}/__down?bytes={size}");
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                    if (!resp.IsSuccessStatusCode) throw new SpeedHttpException(resp);
                    await using var s = await resp.Content.ReadAsStreamAsync(idle.Token);
                    int count;
                    while ((count = await s.ReadAsync(buffer, idle.Token)) > 0)
                    {
                        Interlocked.Add(ref _transferred, count);
                        idle.CancelAfter(15000);
                    }
                }
                failures = 0;
            }
            catch (Exception ex)
            {
                if (phaseToken.IsCancellationRequested) break;
                // refused because of the size: all streams continue with smaller requests right away
                if (ex is SpeedHttpException { Code: 429 or 413 } && size > MinChunk)
                {
                    Interlocked.CompareExchange(ref _chunk, Math.Max(MinChunk, size / 4), size);
                    continue;
                }
                _streamError = Describe(ex);
                if (++failures >= 3) break;
                try { await Task.Delay(200, phaseToken); } catch { break; }
            }
        }
    }

    private static byte[] Payload()
    {
        // random, so nothing on the way can compress it
        if (_payload == null)
        {
            var p = new byte[64 * 1024];
            Random.Shared.NextBytes(p);
            _payload = p;
        }
        return _payload;
    }

    private static double Mbps(long bytes, double seconds) => seconds <= 0 ? 0 : bytes * 8 / seconds / 1e6;

    private static string Describe(Exception ex)
    {
        switch (ex)
        {
            case SpeedHttpException h when h.Code == 429:
                return h.RetryAfterMinutes > 0
                    ? T($"Cloudflare limits how often you can test. Try again in {h.RetryAfterMinutes} min.",
                        $"Cloudflare test sıklığını sınırlıyor. {h.RetryAfterMinutes} dk sonra tekrar dene.")
                    : T("Cloudflare limits how often you can test. Try again in a few minutes.",
                        "Cloudflare test sıklığını sınırlıyor. Birkaç dakika sonra tekrar dene.");
            case SpeedHttpException h:
                return T("The speed test server refused the request (HTTP ", "Hız testi sunucusu isteği reddetti (HTTP ") + h.Code + ").";
            case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }:
                return T("Could not find the speed test server. Are you online?", "Hız testi sunucusu bulunamadı. İnternete bağlı mısın?");
            case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }:
                return T("Could not connect to the speed test server.", "Hız testi sunucusuna bağlanılamadı.");
            case TaskCanceledException or OperationCanceledException or TimeoutException or IOException:
                return T("The speed test server stopped answering.", "Hız testi sunucusu yanıt vermeyi kesti.");
            default:
                return ex.Message;
        }
    }

    /// <summary>Streams <c>size</c> bytes of the random payload, reporting progress as it goes.</summary>
    private sealed class PayloadContent(long size, byte[] data, Action<int> written) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (long left = size; left > 0;)
            {
                int count = (int)Math.Min(left, data.Length);
                await stream.WriteAsync(data.AsMemory(0, count), cancellationToken);
                written(count);
                left -= count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = size;
            return true;
        }
    }

    private sealed class SpeedHttpException : Exception
    {
        public SpeedHttpException(HttpResponseMessage resp) : base($"HTTP {(int)resp.StatusCode}")
        {
            Code = (int)resp.StatusCode;
            var ra = resp.Headers.RetryAfter;
            double seconds = ra?.Delta?.TotalSeconds ?? (ra?.Date is { } d ? (d - DateTimeOffset.UtcNow).TotalSeconds : 0);
            RetryAfterMinutes = seconds > 0 ? (int)Math.Ceiling(seconds / 60) : 0;
        }

        public int Code { get; }
        public int RetryAfterMinutes { get; }
    }
}

/// <summary>A failure with a message meant for the user.</summary>
public sealed class TestException(string message) : Exception(message);

public static class NetworkInfo
{
    /// <summary>The adapter that carries the internet traffic: "Wi-Fi", "Ethernet", or its name.</summary>
    public static string Describe()
    {
        try
        {
            NetworkInterface? best = null;
            int bestScore = -1;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                string d = n.Description.ToLowerInvariant();
                bool virtualAdapter = d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("virtualbox");
                int score = virtualAdapter ? 0 : n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || IsEthernet(n) ? 2 : 1;
                if (score > bestScore)
                {
                    best = n;
                    bestScore = score;
                }
            }
            if (best == null) return "";
            if (best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
            if (IsEthernet(best)) return "Ethernet";
            return best.Name;
        }
        catch (NetworkInformationException)
        {
            return "";
        }
    }

    private static bool IsEthernet(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;
}
