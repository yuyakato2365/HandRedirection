public static class LatestColorPaletteTarget
{
    public static ColorPaletteTarget Current { get; private set; }

    public static void Set(ColorPaletteTarget target)
    {
        if (target != null)
            Current = target;
    }
}
