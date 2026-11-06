using AuraFlyout.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AuraFlyout.Audio;

/// <summary>
/// Captures what the speakers play (WASAPI loopback), runs an FFT and exposes
/// smoothed, log-spaced band levels in the 0..1 range.
/// </summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    private const int FftSize = 2048;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private int _newSamples;
    private int _sampleRate = 48000;
    private float[] _bands = new float[10];
    private WasapiLoopbackCapture? _capture;
    private long _lastDataTicks;

    public bool IsRunning => _capture != null;

    /// <summary>True while audio arrived in the last ~250 ms.</summary>
    public bool HasSignal => Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks) < 250;

    public void Start(MMDevice? device)
    {
        Stop();
        try
        {
            _capture = device != null ? new WasapiLoopbackCapture(device) : new WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Warn("Loopback capture stopped", e.Exception);
            };
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Warn("Loopback capture could not start", ex);
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try
        {
            c.DataAvailable -= OnData;
            c.StopRecording();
        }
        catch
        {
            // ignore
        }
        c.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture == null || e.BytesRecorded == 0) return;
        var fmt = capture.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        // the shared-mode mix format is 32-bit float in practice (often wrapped as "extensible")
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample <= 0) return;
        int frameBytes = bytesPerSample * channels;
        int frames = e.BytesRecorded / frameBytes;

        bool any = false;
        lock (_gate)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int baseOffset = f * frameBytes;
                for (int ch = 0; ch < channels; ch++)
                {
                    int o = baseOffset + ch * bytesPerSample;
                    float v = isFloat && bytesPerSample == 4
                        ? BitConverter.ToSingle(e.Buffer, o)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f
                        : bytesPerSample == 4 ? BitConverter.ToInt32(e.Buffer, o) / 2147483648f
                        : 0;
                    sum += v;
                }
                float mono = sum / channels;
                if (mono != 0) any = true;
                _ring[_ringPos] = mono;
                _ringPos = (_ringPos + 1) % FftSize;
            }
            _newSamples += frames;
        }

        if (any) Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
    }

    /// <summary>
    /// Computes the latest band levels. Call from the UI tick; cheap when no new data arrived.
    /// </summary>
    public float[] GetBands(int count, int sensitivity)
    {
        if (_bands.Length != count) _bands = new float[count];

        float[] samples = new float[FftSize];
        lock (_gate)
        {
            if (_newSamples == 0 || !HasSignal)
            {
                for (int i = 0; i < _bands.Length; i++) _bands[i] *= 0.82f; // fall to silence
                return _bands;
            }
            _newSamples = 0;
            for (int i = 0; i < FftSize; i++)
                samples[i] = _ring[(_ringPos + i) % FftSize];
        }

        var re = new double[FftSize];
        var im = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))); // Hann
            re[i] = samples[i] * w;
        }
        Fft(re, im);

        double minF = 45, maxF = Math.Min(16000, _sampleRate / 2.0);
        double binHz = (double)_sampleRate / FftSize;
        double gainDb = sensitivity switch { 0 => 0, 2 => 12, _ => 6 };

        for (int b = 0; b < count; b++)
        {
            double f0 = minF * Math.Pow(maxF / minF, (double)b / count);
            double f1 = minF * Math.Pow(maxF / minF, (double)(b + 1) / count);
            int i0 = Math.Max(1, (int)(f0 / binHz));
            int i1 = Math.Max(i0 + 1, (int)(f1 / binHz));
            double peak = 0;
            for (int i = i0; i < i1 && i < FftSize / 2; i++)
            {
                double mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
                if (mag > peak) peak = mag;
            }

            // tilt: high bands carry less energy, lift them a bit
            double tilt = 1 + (double)b / count * 2.5;
            double db = 20 * Math.Log10(peak * tilt / (FftSize / 4.0) + 1e-9) + gainDb;
            float level = (float)Math.Clamp((db + 54) / 54, 0, 1);

            // fast attack, slower release
            float prev = _bands[b];
            _bands[b] = level > prev ? prev + (level - prev) * 0.65f : prev + (level - prev) * 0.22f;
        }
        return _bands;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
