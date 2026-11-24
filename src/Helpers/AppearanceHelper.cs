using AITranslator.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AITranslator.Helpers;

public static class AppearanceHelper
{
    private const double MinimumTextSize = 12;

    public static void Apply(FrameworkElement root, AppSettings settings)
    {
        root.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var bodySize = Math.Clamp(settings.FontSize, MinimumTextSize, 22);
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
                if (control is ComboBox comboBox)
                {
                    comboBox.ItemContainerStyle = CreateComboBoxItemStyle(fontFamily, bodySize);
                }

                break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            ApplyTypography(VisualTreeHelper.GetChild(element, index), fontFamily, bodySize);
        }
    }

    // 下拉列表项位于独立弹层，遍历可视树时访问不到，改由容器样式设置字体。
    private static Style CreateComboBoxItemStyle(FontFamily fontFamily, double fontSize) =>
        new(typeof(ComboBoxItem))
        {
            BasedOn = (Style)Application.Current.Resources["DefaultComboBoxItemStyle"],
            Setters =
            {
                new Setter(Control.FontFamilyProperty, fontFamily),
                new Setter(Control.FontSizeProperty, fontSize)
            }
        };

    private static double ResolveTextSize(TextBlock textBlock, double bodySize)
    {
        if (UsesStyle(textBlock, "PageTitleStyle"))
        {
            return bodySize + 6;
        }

        if (UsesStyle(textBlock, "SectionTitleStyle"))
        {
            return bodySize;
        }

        if (UsesStyle(textBlock, "CaptionStyle"))
        {
            return Math.Max(MinimumTextSize, bodySize - 2);
        }

        return string.Equals(textBlock.Name, "QueryText", StringComparison.Ordinal) ? bodySize + 2 : bodySize;
    }

    private static bool UsesStyle(TextBlock textBlock, string resourceKey) =>
        ReferenceEquals(textBlock.Style, Application.Current.Resources[resourceKey]);
}
