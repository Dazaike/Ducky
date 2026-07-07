using System.Runtime.InteropServices;

namespace Ducky.Core.Audio;

/// <summary>
/// Sends the system media play/pause key to the app Windows considers the current media session.
/// </summary>
public static class MediaKeySender
{
    private const int KeyeventfKeyup = 0x0002;
    private const byte VkMediaPlayPause = 0xB3;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    public static void SendMediaPlayPause()
    {
        keybd_event(VkMediaPlayPause, 0, 0, 0);
        keybd_event(VkMediaPlayPause, 0, KeyeventfKeyup, 0);
    }
}
