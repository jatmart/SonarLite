namespace SonarLite.Models;

/// <summary>
/// Both an app's routing bucket and its EQ profile key. Each bus gets its own in-process DSP EQ
/// chain (see EqChain); a bus is only actually tapped onto the cable while its curve is non-flat,
/// otherwise its apps play straight to the headset at zero added latency. No Equalizer APO is
/// involved anywhere -- see AudioEngine's doc comment for why.
/// </summary>
public enum SessionClass
{
    Game,
    Chat,
    Media
}

/// <summary>
/// Enum.GetValues&lt;SessionClass&gt;() allocates a fresh array every call; this enum only ever has
/// these 3 values, so every one of the ~9 call sites across the app (several on event-driven paths
/// that fire repeatedly during normal use, not just at startup) can share one cached array instead.
/// </summary>
public static class SessionClasses
{
    public static readonly SessionClass[] All = Enum.GetValues<SessionClass>();
}
