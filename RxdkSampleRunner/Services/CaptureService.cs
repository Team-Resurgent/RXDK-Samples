using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlashCap;

namespace RxdkSampleRunner.Services;

/// <summary>A selectable video-capture device (e.g. an Elgato game-capture card).</summary>
public sealed record CaptureDeviceItem(string Name, CaptureDeviceDescriptor Descriptor)
{
    public override string ToString() => Name;
}

/// <summary>
/// Live preview of a video-capture device via FlashCap (DirectShow/UVC — no vendor SDK).
/// Frames arrive on FlashCap's capture thread; we decode each to an Avalonia Bitmap and
/// marshal it to the UI. A single-slot busy gate drops frames that arrive while one is
/// still being decoded — a preview does not need every frame.
/// </summary>
public sealed class CaptureService : IAsyncDisposable
{
    private CaptureDevice? _device;
    private int _busy;

    /// <summary>Enumerate capture devices that expose at least one video format.</summary>
    public static IReadOnlyList<CaptureDeviceItem> ListDevices()
    {
        try
        {
            return new CaptureDevices().EnumerateDescriptors()
                .Where(d => d.Characteristics.Length > 0)
                .Select(d => new CaptureDeviceItem(d.Name, d))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public async Task StartAsync(CaptureDeviceDescriptor descriptor,
                                 Action<Bitmap> onFrame, Action<string> onStatus)
    {
        await StopAsync().ConfigureAwait(false);

        // Prefer MJPEG (cheap to decode), then the largest format at/under 720p for a
        // responsive preview, then the largest available overall.
        var pick = descriptor.Characteristics
            .OrderByDescending(c => c.PixelFormat == PixelFormats.JPEG)
            .ThenByDescending(c => c.Width * c.Height <= 1280 * 720)
            .ThenByDescending(c => c.Width * c.Height)
            .First();
        onStatus($"{pick.Width}×{pick.Height} {pick.PixelFormat}");   // device name is in the dropdown

        _device = await descriptor.OpenAsync(pick, bufferScope =>
        {
            // Drop this frame if we're still decoding the previous one.
            if (Interlocked.Exchange(ref _busy, 1) == 1) return Task.CompletedTask;
            try
            {
                // ExtractImage yields a decodable image (JPEG for MJPEG sources, otherwise a
                // BMP/DIB) — Avalonia's Bitmap decodes both.
                byte[] image = bufferScope.Buffer.ExtractImage();
                Bitmap bmp;
                using (var ms = new MemoryStream(image)) bmp = new Bitmap(ms);
                Dispatcher.UIThread.Post(() => onFrame(bmp));
            }
            catch { /* skip a bad/undecodable frame */ }
            finally { Interlocked.Exchange(ref _busy, 0); }
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await _device.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        var d = _device;
        _device = null;
        if (d is null) return;
        try { await d.StopAsync().ConfigureAwait(false); } catch { /* already stopping */ }
        try { await d.DisposeAsync().ConfigureAwait(false); } catch { /* already gone */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
