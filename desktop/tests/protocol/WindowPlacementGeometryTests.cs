using Mihomo.Desktop.Services;

internal static class WindowPlacementGeometryTests
{
    internal static void Run()
    {
        var fullHd = new WindowPixelBounds(0, 0, 1920, 1040);
        // Default 1240 x 820 DIP on 150%: the old centered top was negative.
        Equal(new WindowPixelBounds(30, 0, 1860, 1040),
            WindowPlacementGeometry.Fit(new(0, -95, 1860, 1230), fullHd, center: true));
        Equal(new WindowPixelBounds(63, 0, 1240, 728),
            WindowPlacementGeometry.Fit(new(0, -46, 1240, 820), new(0, 0, 1366, 728), center: true));

        // Preserve a user's valid placement; clamp only the edges that escaped.
        var user = new WindowPixelBounds(213, 147, 900, 650);
        Equal(user, WindowPlacementGeometry.Fit(user, fullHd, center: false));
        Equal(new WindowPixelBounds(1020, 0, 900, 650),
            WindowPlacementGeometry.Fit(new(1800, -48, 900, 650), fullHd, center: false));
        Equal(new WindowPixelBounds(1020, 390, 900, 650),
            WindowPlacementGeometry.Fit(new(5000, 1800, 900, 650), fullHd, center: false));

        // Negative coordinates are valid on left/upper monitors; taskbars can
        // reduce the work area from any edge, not just the bottom.
        var upperLeft = new WindowPixelBounds(-1872, -1040, 1872, 1000);
        Equal(new WindowPixelBounds(-1556, -950, 1240, 820),
            WindowPlacementGeometry.Fit(new(-1920, -1080, 1240, 820), upperLeft, center: true));
        Equal(new WindowPixelBounds(-1872, -1040, 1872, 1000),
            WindowPlacementGeometry.Fit(new(-2000, -1200, 2480, 1640), upperLeft, center: false));

        // The 560 DIP minimum must yield on high-DPI small work areas, and the
        // preferred minimum must be usable again after moving to a larger one.
        Equal(364.0, WindowPlacementGeometry.Minimum(560, 728, 2));
        Equal(560.0, WindowPlacementGeometry.Minimum(560, 1440, 2));
        Equal(500.0, WindowPlacementGeometry.Minimum(500, 1366, 2));
        Equal(400.0, WindowPlacementGeometry.Minimum(500, 800, 2));

        // Native maximization can leave the invisible frame outside the work
        // area. Count the real clipped pixels, including padded frame metrics.
        Equal((8, 8, 8, 8), WindowPlacementGeometry.VisibleInsets(new(-8, -8, 1936, 1056), fullHd));
        Equal((0, 0, 0, 0), WindowPlacementGeometry.VisibleInsets(fullHd, fullHd));
        Equal((0, 0, 0, 0), WindowPlacementGeometry.VisibleInsets(user, fullHd));
        Equal((8, 8, 8, 8), WindowPlacementGeometry.VisibleInsets(new(40, 32, 1888, 1016), new(48, 40, 1872, 1000)));
        Equal((8, 8, 8, 8), WindowPlacementGeometry.VisibleInsets(new(-1928, -1088, 1936, 1056), new(-1920, -1080, 1920, 1040)));
        Equal(8.0, WindowPlacementGeometry.VisibleInsets(new(-16, -16, 1952, 1072), fullHd).Top / 2.0);

        foreach (double scale in new[] { 1, 1.25, 1.5, 1.75, 2 })
        {
            int width = WindowPlacementGeometry.Pixels(1240, scale, fullHd.Width);
            int height = WindowPlacementGeometry.Pixels(820, scale, fullHd.Height);
            var fitted = WindowPlacementGeometry.Fit(new(0, 0, width, height), fullHd, center: true);
            if (fitted.Left < fullHd.Left || fitted.Top < fullHd.Top || fitted.Right > fullHd.Right || fitted.Bottom > fullHd.Bottom)
                throw new Exception($"Default geometry escaped its work area at DPI scale {scale}.");
            Equal(fitted, WindowPlacementGeometry.Fit(fitted, fullHd, center: false));
        }
        Console.WriteLine("Native window geometry: cold-start size, mixed-DPI minima, actual maximized insets, taskbar offsets, negative monitors and stable restore placement passed.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; received {actual}");
    }
}
