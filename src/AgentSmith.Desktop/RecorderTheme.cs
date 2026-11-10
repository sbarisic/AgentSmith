using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgentSmith.Desktop;

internal static class RecorderTheme
{
    internal static void Apply(bool? dark = null)
    {
        var app = System.Windows.Application.Current;
        if (dark is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        var palette = (ResourceDictionary)app.FindResource(dark.Value ? "DarkPalette" : "LightPalette");
        foreach (string name in palette.Keys) app.Resources[name] = palette[name];
        if (SystemParameters.HighContrast)
        {
            foreach (string name in palette.Keys)
                app.Resources[name] = name switch
                {
                    "Recorder.Background" or "Recorder.Surface" or "Recorder.Input" => SystemColors.WindowBrush,
                    "Recorder.Selection" or "Recorder.Hover" or "Recorder.SpeakerCallBackground" => SystemColors.WindowBrush,
                    "Recorder.Border" => SystemColors.WindowTextBrush,
                    _ => SystemColors.WindowTextBrush
                };
        }
        // Keep native Fluent controls and documents in the same palette as our shell.
        foreach (var (native, semantic) in new[] {
            ("TextFillColorPrimaryBrush", "Recorder.Text"), ("TextFillColorSecondaryBrush", "Recorder.Muted"),
            ("AccentTextFillColorPrimaryBrush", "Recorder.Accent"), ("ApplicationBackgroundBrush", "Recorder.Background"),
            ("CardBackgroundFillColorDefaultBrush", "Recorder.Surface"), ("CardStrokeColorDefaultBrush", "Recorder.Border") })
            app.Resources[native] = app.Resources[semantic];
        foreach (var name in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush" })
            app.Resources[name] = SystemParameters.HighContrast ? SystemColors.HighlightBrush : app.Resources["Recorder.Accent"];
        app.Resources["AccentFillColorSelectedTextBackgroundBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightBrush : app.Resources["Recorder.Selection"];
        app.Resources["TextOnAccentFillColorSelectedTextBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : app.Resources["Recorder.Text"];
    }
}
