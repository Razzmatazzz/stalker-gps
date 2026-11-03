using MudBlazor;

namespace StalkerGPS;

/// <summary>
/// MudBlazor theme matching the stalker-vodka website (app/globals.css): olive-black ground, square
/// corners, acid green and ember accents, DM Sans body text. Keep the colors in sync with wwwroot/css/app.css.
/// </summary>
public static class SiteTheme
{
    public const string Ink = "#e9e6da";
    public const string Muted = "#9b9c8d";
    public const string Dim = "#65695c";
    public const string Ground = "#11130f";
    public const string Panel = "#191c16";
    public const string Line = "#303329";
    public const string Acid = "#c2d25b";
    public const string Ember = "#e0883c";

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = Acid,
            PrimaryContrastText = Ground,
            Secondary = Ember,
            SecondaryContrastText = Ground,
            Success = Acid,
            SuccessContrastText = Ground,
            Warning = Ember,
            WarningContrastText = Ground,
            Info = "#83a9a3",
            Error = "#d9644a",
            Background = Ground,
            Surface = Panel,
            AppbarBackground = Ground,
            AppbarText = Ink,
            DrawerBackground = Panel,
            TextPrimary = Ink,
            TextSecondary = Muted,
            TextDisabled = Dim,
            ActionDefault = Muted,
            LinesDefault = Line,
            LinesInputs = "#414438",
            TableLines = Line,
            Divider = Line,
            OverlayLight = "rgba(17,19,15,0.6)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["DM Sans", "sans-serif"] },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "0px" },
    };
}
