using NAudio.Dsp;
using NAudio.Wave;

namespace SonarLite.Services;

/// <summary>10-band peaking-EQ chain applied per channel, with automatic negative makeup gain to prevent clipping.</summary>
internal sealed class EqChain(ISampleProvider source) : ISampleProvider
{
    private static readonly float[] Freqs = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private const float Q = 1.0f;

    private readonly object _sync = new();
    private BiQuadFilter[][]? _filters;
    private float _makeup = 1f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public void SetGains(double[] gains, bool enabled, double preampDb = 0)
    {
        lock (_sync)
        {
            if (!enabled || gains.All(g => Math.Abs(g) < 0.01))
            {
                _filters = null;
                _makeup = 1f;
                return;
            }

            int channels = WaveFormat.Channels;
            int sampleRate = WaveFormat.SampleRate;
            var filters = new BiQuadFilter[channels][];
            for (int c = 0; c < channels; c++)
            {
                filters[c] = new BiQuadFilter[gains.Length];
                for (int b = 0; b < gains.Length; b++)
                    filters[c][b] = BiQuadFilter.PeakingEQ(sampleRate, Freqs[b], Q, (float)gains[b]);
            }
            _filters = filters;
            _makeup = MakeupFor(gains, sampleRate) * (float)Math.Pow(10, preampDb / 20.0);
        }
    }

    /// <summary>
    /// Attenuation that brings the cascade's loudest point back to unity. This has to be measured
    /// from the combined magnitude response, not from gains.Max(): the bands sit an octave apart at
    /// Q=1, which is wide enough that neighbours pile onto each other. Immersive's [8,7,5,...] bass
    /// bands sum to roughly +13 dB near 50 Hz, so trimming only the largest band's 8 dB still leaves
    /// the bus several dB over full scale -- which is what was hard-clipping in the Windows mixer
    /// (gross distortion, plus enough extra RMS to trip a powered speaker's protection cutout).
    /// </summary>
    private static float MakeupFor(double[] gains, int sampleRate)
    {
        // Log sweep from 10 Hz to just under Nyquist; fine enough to land on any peak these
        // fixed-Q bands can produce.
        const int points = 512;
        double lo = Math.Log(10.0), hi = Math.Log(sampleRate * 0.49);
        double peak = 1.0;

        for (int i = 0; i < points; i++)
        {
            double f = Math.Exp(lo + (hi - lo) * i / (points - 1));
            double mag = 1.0;
            for (int b = 0; b < gains.Length; b++)
                mag *= PeakingMagnitude(sampleRate, Freqs[b], Q, gains[b], f);
            if (mag > peak) peak = mag;
        }

        return (float)(1.0 / peak);
    }

    /// <summary>|H(e^jw)| of one RBJ peaking biquad -- the same design NAudio's PeakingEQ builds.</summary>
    private static double PeakingMagnitude(int sampleRate, double centre, double q, double dbGain, double freq)
    {
        double w0 = 2 * Math.PI * centre / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double a = Math.Pow(10, dbGain / 40);
        double cosw0 = Math.Cos(w0);

        double b0 = 1 + alpha * a, b1 = -2 * cosw0, b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a, a1 = -2 * cosw0, a2 = 1 - alpha / a;

        double w = 2 * Math.PI * freq / sampleRate;
        double cw = Math.Cos(w), sw = Math.Sin(w);
        double c2w = Math.Cos(2 * w), s2w = Math.Sin(2 * w);

        // z^-1 = e^-jw, z^-2 = e^-j2w
        double numRe = b0 + b1 * cw + b2 * c2w, numIm = -(b1 * sw + b2 * s2w);
        double denRe = a0 + a1 * cw + a2 * c2w, denIm = -(a1 * sw + a2 * s2w);

        return Math.Sqrt((numRe * numRe + numIm * numIm) / (denRe * denRe + denIm * denIm));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        lock (_sync)
        {
            var filters = _filters;
            if (filters is null) return read;

            int channels = WaveFormat.Channels;
            for (int i = 0; i < read; i++)
            {
                var chain = filters[i % channels];
                float sample = buffer[offset + i];
                for (int k = 0; k < chain.Length; k++)
                    sample = chain[k].Transform(sample);
                buffer[offset + i] = sample * _makeup;
            }
        }
        return read;
    }
}
