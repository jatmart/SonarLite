using NAudio.Wave;

namespace SonarLite.Services;

/// <summary>
/// Last stage before the render device: guarantees nothing leaves the engine above full scale.
///
/// Per-bus EQ makeup already keeps each bus at unity or below, but the master mixer sums three of
/// them (Game + Chat + Media), and three sources each peaking near 0 dBFS add up well past 1.0.
/// WasapiOut hands floats straight to the Windows shared mixer, which hard-clips anything over the
/// ceiling -- audible as distortion, and the extra RMS a clipped waveform carries is enough to trip
/// the protection cutout on a powered speaker. So this rides gain down instead of letting that
/// happen: fast attack so no overshoot gets through, slow release so the gain reduction isn't
/// audible as pumping.
/// </summary>
internal sealed class SafetyLimiter(ISampleProvider source, float ceiling = 0.99f) : ISampleProvider
{
    private float _gain = 1f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        int channels = WaveFormat.Channels;

        // ~1.5 ms attack / ~150 ms release, expressed per frame so they hold at any sample rate.
        float attack = Coefficient(0.0015f);
        float release = Coefficient(0.150f);

        for (int i = 0; i < read; i += channels)
        {
            // One gain for every channel in the frame, or the stereo image would wander as the
            // limiter engages on one side only.
            float peak = 0f;
            for (int c = 0; c < channels && i + c < read; c++)
            {
                float mag = Math.Abs(buffer[offset + i + c]);
                if (mag > peak) peak = mag;
            }

            float target = peak > ceiling ? ceiling / peak : 1f;
            _gain = target < _gain
                ? target + (_gain - target) * attack     // clamp down immediately
                : target + (_gain - target) * release;   // ease back up

            for (int c = 0; c < channels && i + c < read; c++)
            {
                // The smoothed gain can still trail a very fast transient by a sample or two, so
                // clamp on the way out -- the engine's contract is that nothing exceeds full scale.
                float s = buffer[offset + i + c] * _gain;
                buffer[offset + i + c] = Math.Clamp(s, -ceiling, ceiling);
            }
        }

        return read;
    }

    private float Coefficient(float seconds) =>
        (float)Math.Exp(-1.0 / (seconds * WaveFormat.SampleRate));
}
