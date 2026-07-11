namespace SonarLite.Models;

public sealed class SessionPrefs
{
    public SessionClass Classification { get; set; } = SessionClass.Game;
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
}
