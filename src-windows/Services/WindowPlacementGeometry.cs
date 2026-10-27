using System;

namespace Mihomo.Desktop.Services;

/// <summary>Window and monitor geometry in physical screen pixels, including negative monitor origins.</summary>
internal readonly record struct WindowPixelBounds(int Left, int Top, int Width, int Height)
{
    internal int Right => Left + Width;
    internal int Bottom => Top + Height;
}

internal static class WindowPlacementGeometry
{
    internal static WindowPixelBounds Fit(WindowPixelBounds bounds, WindowPixelBounds workArea, bool center)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        int width = Math.Clamp(bounds.Width, 1, workArea.Width);
        int height = Math.Clamp(bounds.Height, 1, workArea.Height);
        int left = center ? workArea.Left + (workArea.Width - width) / 2 : Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        int top = center ? workArea.Top + (workArea.Height - height) / 2 : Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new(left, top, width, height);
    }

    internal static int Pixels(double dips, double scale, int maximum)
        => (int)Math.Clamp(Math.Round(dips * scale), 1, maximum);

    internal static double Minimum(double preferredDips, int availablePixels, double scale)
        => Math.Min(preferredDips, availablePixels / scale);

    internal static (int Left, int Top, int Right, int Bottom) VisibleInsets(WindowPixelBounds client, WindowPixelBounds workArea)
        => (Math.Max(0, workArea.Left - client.Left), Math.Max(0, workArea.Top - client.Top),
            Math.Max(0, client.Right - workArea.Right), Math.Max(0, client.Bottom - workArea.Bottom));
}
