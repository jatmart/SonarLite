using NAudio.Dsp;
using NAudio.Wave;

namespace SonarLite.Services;

/// <summary>10-band peaking-EQ chain applied per channel, with automatic makeup gain that holds the bus
/// at the loudness it had before the EQ.</summary>
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
    /// Attenuation that brings the cascade back to the loudness it had before the EQ, measured as
    /// the energy the curve adds to broadband material rather than the height of its tallest peak.
    ///
    /// It has to be measured from the combined magnitude response, not from gains.Max(): the bands
    /// sit an octave apart at Q=1, which is wide enough that neighbours pile onto each other, so no
    /// single slider tells you what the chain does.
    ///
    /// This used to normalize the response *peak* to unity, which assumes the signal carries
    /// full-scale content at the exact frequency where those bands pile up. Music does not -- its
    /// energy is spread across the spectrum. Pebble V2 peaks +7.7 dB at 125 Hz but adds only
    /// +3.3 dB to pink noise, and the 4.4 dB difference came off every sample whether or not
    /// anything was playing at 125 Hz. Across the shipped presets that cost 5 to 9 dB, which is
    /// what "everything is at 100% and it is still quiet" was.
    ///
    /// Peaks that do overshoot are caught by <see cref="SafetyLimiter"/>, which clamps the final
    /// mix at 0.99 and cannot be exceeded. That is what makes measuring loudness here safe: the
    /// clip protection this method used to provide is already guaranteed one stage downstream.
    /// </summary>
    private static float MakeupFor(double[] gains, int sampleRate)
    {
        // Log sweep over the audible band, averaging |H|^2. Log spacing is the weighting: it gives
        // every octave the same share of the result, which is how program material spreads its
        // energy. Sweeping to Nyquist instead would average in ultrasonic bands that no source
        // plays into and no headset reproduces, dragging the answer toward whatever the top band
        // happens to be doing.
        const int points = 1024;
        double lo = Math.Log(20.0), hi = Math.Log(Math.Min(20000.0, sampleRate * 0.49));
        double energy = 0;

        for (int i = 0; i < points; i++)
        {
            double f = Math.Exp(lo + (hi - lo) * i / (points - 1));
            double mag = 1.0;
            for (int b = 0; b < gains.Length; b++)
                mag *= PeakingMagnitude(sampleRate, Freqs[b], Q, gains[b], f);
            energy += mag * mag;
        }

        // Never louder than flat. A curve that cuts more than it boosts stays where the user put
        // it rather than being pushed back up -- quieter is the point of those presets.
        double rms = Math.Sqrt(energy / points);
        return rms <= 1.0 ? 1f : (float)(1.0 / rms);
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
