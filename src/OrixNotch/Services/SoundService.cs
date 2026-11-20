using System.IO;
using System.Media;

namespace OrixNotch.Services;

/// <summary>The alert sounds every feature picks from (reminders, focus sessions, timers), and how they play.</summary>
public static class SoundService
{
    public const string None = "No Sound";

    public static readonly string[] Names = ["Calendar", "Soft Chime", "Bell", "Ping", None];

    /// <summary>Older settings saved reminders as "Chime".</summary>
    public static string Normalize(string? name) => name == "Chime" ? "Soft Chime" : name ?? None;

    public static int IndexOf(string? name) => Math.Max(0, Array.IndexOf(Names, Normalize(name)));

    public static void Play(string? name)
    {
        try
        {
            switch (Normalize(name))
            {
                case "Calendar":
                    var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav");
                    if (File.Exists(wav)) new SoundPlayer(wav).Play();
                    else SystemSounds.Asterisk.Play();
                    break;
                case "Soft Chime": SystemSounds.Asterisk.Play(); break;
                case "Bell": SystemSounds.Exclamation.Play(); break;
                case "Ping": SystemSounds.Beep.Play(); break;
            }
        }
        catch
        {
            // Sound is a nicety; never let it break the alert.
        }
    }
}
