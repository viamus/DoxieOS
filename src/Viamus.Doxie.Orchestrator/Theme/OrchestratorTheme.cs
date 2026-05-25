using MudBlazor;

namespace Viamus.Doxie.Orchestrator.Theme;

public static class OrchestratorTheme
{
    private const string Tigerlily = "#D97757";
    private const string Copper = "#B8694D";
    private const string Ink = "#0E0F0D";
    private const string Panel = "#171816";
    private const string PanelRaised = "#20211E";
    private const string Text = "#F2EFE7";
    private const string TextMuted = "#A9A39A";
    private const string Line = "#34352F";

    private static readonly string[] FontStack =
    [
        "Segoe UI Variable",
        "Segoe UI",
        "-apple-system",
        "BlinkMacSystemFont",
        "Inter",
        "Roboto",
        "Helvetica Neue",
        "Arial",
        "sans-serif",
    ];

    public static readonly MudTheme Instance = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Tigerlily,
            Secondary = Copper,
            Info = "#7AA7D9",
            Success = "#65B891",
            Warning = "#E1A34A",
            Error = "#E06A5F",
            Background = Ink,
            Surface = Panel,
            AppbarBackground = Ink,
            AppbarText = Text,
            DrawerBackground = "#121311",
            DrawerText = Text,
            DrawerIcon = TextMuted,
            TextPrimary = Text,
            TextSecondary = TextMuted,
            ActionDefault = TextMuted,
            DividerLight = Line,
            Divider = Line,
        },
        PaletteDark = new PaletteDark
        {
            Primary = Tigerlily,
            Secondary = Copper,
            Info = "#7AA7D9",
            Success = "#65B891",
            Warning = "#E1A34A",
            Error = "#E06A5F",
            Background = Ink,
            Surface = Panel,
            AppbarBackground = Ink,
            AppbarText = Text,
            DrawerBackground = "#121311",
            DrawerText = Text,
            DrawerIcon = TextMuted,
            TextPrimary = Text,
            TextSecondary = TextMuted,
            ActionDefault = TextMuted,
            DividerLight = Line,
            Divider = Line,
            BackgroundGray = PanelRaised,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = FontStack,
                FontSize = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = "normal",
            },
            H1 = new H1Typography
            {
                FontFamily = FontStack,
                FontSize = "1.875rem",
                FontWeight = "600",
                LineHeight = "1.2",
                LetterSpacing = "0",
            },
            H2 = new H2Typography
            {
                FontFamily = FontStack,
                FontSize = "1.625rem",
                FontWeight = "600",
                LineHeight = "1.25",
                LetterSpacing = "0",
            },
            H3 = new H3Typography
            {
                FontFamily = FontStack,
                FontSize = "1.375rem",
                FontWeight = "600",
                LineHeight = "1.3",
                LetterSpacing = "0",
            },
            H4 = new H4Typography
            {
                FontFamily = FontStack,
                FontSize = "1.125rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H5 = new H5Typography
            {
                FontFamily = FontStack,
                FontSize = "1rem",
                FontWeight = "600",
                LineHeight = "1.5",
            },
            H6 = new H6Typography
            {
                FontFamily = FontStack,
                FontSize = "0.9375rem",
                FontWeight = "600",
                LineHeight = "1.5",
            },
            Subtitle1 = new Subtitle1Typography
            {
                FontFamily = FontStack,
                FontSize = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Subtitle2 = new Subtitle2Typography
            {
                FontFamily = FontStack,
                FontSize = "0.8125rem",
                FontWeight = "500",
                LineHeight = "1.5",
            },
            Body1 = new Body1Typography
            {
                FontFamily = FontStack,
                FontSize = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Body2 = new Body2Typography
            {
                FontFamily = FontStack,
                FontSize = "0.8125rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Button = new ButtonTypography
            {
                FontFamily = FontStack,
                FontSize = "0.8125rem",
                FontWeight = "500",
                LineHeight = "1.6",
                LetterSpacing = "0",
                TextTransform = "none",
            },
            Caption = new CaptionTypography
            {
                FontFamily = FontStack,
                FontSize = "0.75rem",
                FontWeight = "400",
                LineHeight = "1.4",
            },
            Overline = new OverlineTypography
            {
                FontFamily = FontStack,
                FontSize = "0.6875rem",
                FontWeight = "500",
                LineHeight = "1.4",
                LetterSpacing = "0",
                TextTransform = "uppercase",
            },
        },
    };
}
