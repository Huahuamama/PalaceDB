namespace PalaceDB.Core.Metadata;

/// <summary>A screen-space rectangle in device-independent pixels.</summary>
public readonly record struct PlacementRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// Rules that turn the remembered window geometry of a file into a rectangle that is
/// guaranteed to be reachable (F17). Monitors get unplugged and resolutions change, so a
/// stored position can point outside the desktop; the window must never be restored into
/// a place where the user cannot grab it.
/// <para>
/// The arithmetic is kept free of WPF types so it can be unit tested without a window.
/// </para>
/// </summary>
public static class WindowPlacement
{
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 800;

    /// <summary>Smallest window the shell allows (mirrors MinWidth/MinHeight of the XAML).</summary>
    public const double MinimumWidth = 640;
    public const double MinimumHeight = 400;

    /// <summary>How much of the window must remain on screen for a position to be kept.</summary>
    public const double MinimumVisible = 120;

    /// <summary>
    /// Resolves a stored state against the current desktop. The result always fits inside
    /// <paramref name="screen"/> and always overlaps it by at least <see cref="MinimumVisible"/>
    /// pixels on both axes. A state without a position is centred.
    /// </summary>
    public static PlacementRect Fit(WindowState state, PlacementRect screen)
    {
        var width = Clamp(Sanitize(state.Width, DefaultWidth), MinimumWidth, Math.Max(MinimumWidth, screen.Width));
        var height = Clamp(Sanitize(state.Height, DefaultHeight), MinimumHeight, Math.Max(MinimumHeight, screen.Height));

        if (!state.HasPosition)
            return Centre(screen, width, height);

        var left = state.X!.Value;
        var top = state.Y!.Value;

        // A window that hangs off the desktop (monitor removed, resolution lowered) is
        // brought back to the centre instead of being pushed against an edge.
        if (Visible(left, top, width, height, screen) < MinimumVisible)
            return Centre(screen, width, height);

        // Otherwise only the vertical/horizontal edges are pulled back inside.
        return new PlacementRect(
            Clamp(left, screen.Left - width + MinimumVisible, screen.Right - MinimumVisible),
            Clamp(top, screen.Top - height + MinimumVisible, screen.Bottom - MinimumVisible),
            width,
            height);
    }

    /// <summary>
    /// Builds the state to store from a window rectangle. When the window is maximised the
    /// caller passes the restore bounds, so un-maximising later returns the old size.
    /// </summary>
    public static WindowState Capture(PlacementRect window, bool maximized) => new()
    {
        X = Math.Round(window.Left, MidpointRounding.AwayFromZero),
        Y = Math.Round(window.Top, MidpointRounding.AwayFromZero),
        Width = Math.Max(MinimumWidth, Math.Round(window.Width, MidpointRounding.AwayFromZero)),
        Height = Math.Max(MinimumHeight, Math.Round(window.Height, MidpointRounding.AwayFromZero)),
        Maximized = maximized,
    };

    private static PlacementRect Centre(PlacementRect screen, double width, double height) => new(
        screen.Left + (screen.Width - width) / 2,
        screen.Top + (screen.Height - height) / 2,
        width,
        height);

    /// <summary>Length of the smaller side of the intersection with the screen.</summary>
    private static double Visible(double left, double top, double width, double height, PlacementRect screen)
    {
        var horizontal = Math.Min(left + width, screen.Right) - Math.Max(left, screen.Left);
        var vertical = Math.Min(top + height, screen.Bottom) - Math.Max(top, screen.Top);
        return Math.Min(horizontal, vertical);
    }

    private static double Sanitize(double value, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
