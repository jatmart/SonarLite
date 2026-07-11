using NAudio.Dsp;
using NAudio.Wave;

namespace SonarLite.Services;

/// <summary>10-band peaking-EQ chain applied per channel, with automatic negative makeup gain to prevent clipping.</summary>
internal sealed class EqChain(ISampleProvider source) : ISampleProvider
{
    private static readonly float[] Freqs = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private readonly object _sync = new();
    private BiQuadFilter[][]? _filters;
    private float _makeup = 1f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public void SetGains(double[] gains, bool enabled)
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
                    filters[c][b] = BiQuadFilter.PeakingEQ(sampleRate, Freqs[b], 1.0f, (float)gains[b]);
            }
            _filters = filters;
            _makeup = (float)Math.Pow(10, -Math.Max(0, gains.Max()) / 20.0);
        }
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
