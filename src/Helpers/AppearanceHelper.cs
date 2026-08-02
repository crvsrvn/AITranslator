using AITranslator.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AITranslator.Helpers;

public static class AppearanceHelper
{
    public static void Apply(FrameworkElement root, AppSettings settings)
    {
        root.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var bodySize = Math.Clamp(settings.FontSize, 12, 22);
        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily)
            ? "Microsoft YaHei UI"
            : settings.FontFamily);
        ApplyTypography(root, fontFamily, bodySize);
    }

    private static void ApplyTypography(DependencyObject element, FontFamily fontFamily, double bodySize)
    {
        switch (element)
        {
            case TextBlock textBlock:
                textBlock.FontFamily = fontFamily;
                textBlock.FontSize = ResolveTextSize(textBlock, bodySize);
                break;
            case Control control:
                control.FontFamily = fontFamily;
                control.FontSize = bodySize;
                break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            ApplyTypography(VisualTreeHelper.GetChild(element, index), fontFamily, bodySize);
        }
    }

    private static double ResolveTextSize(TextBlock textBlock, double bodySize)
    {
        if (UsesStyle(textBlock, "PageTitleStyle"))
        {
            return bodySize + 9;
        }

        if (UsesStyle(textBlock, "SectionTitleStyle"))
        {
            return bodySize + 1;
        }

        return string.Equals(textBlock.Name, "QueryText", StringComparison.Ordinal) ? bodySize + 2 : bodySize;
    }

    private static bool UsesStyle(TextBlock textBlock, string resourceKey) =>
        ReferenceEquals(textBlock.Style, Application.Current.Resources[resourceKey]);
}
