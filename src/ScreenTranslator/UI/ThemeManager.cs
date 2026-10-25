using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using ScreenTranslator.Models;

namespace ScreenTranslator.UI
{
    public static class ThemeManager
    {
        public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
        private static bool _templatesInstalled;

        public static event Action<AppTheme> ThemeChanged;

        public static void ApplyTheme(AppTheme theme)
        {
            CurrentTheme = theme;
            var res = Application.Current?.Resources;
            if (res == null) return;

            if (theme == AppTheme.Dark)
            {
                SetBrush(res, "WindowBgBrush", "#111318");
                SetBrush(res, "SidebarBgBrush", "#15181F");
                SetBrush(res, "CardBgBrush", "#1A1E27");
                SetBrush(res, "CardHoverBrush", "#222733");
                SetBrush(res, "InputBgBrush", "#13161C");
                SetBrush(res, "BorderSubtleBrush", "#272D3A");
                SetBrush(res, "BorderFocusBrush", "#3B82F6");
                SetBrush(res, "TextPrimaryBrush", "#F1F5F9");
                SetBrush(res, "TextSecondaryBrush", "#94A3B8");
                SetBrush(res, "TextMutedBrush", "#64748B");
                SetBrush(res, "AccentBrush", "#3B82F6");
                SetBrush(res, "AccentHoverBrush", "#2563EB");
                SetBrush(res, "AccentSubtleBrush", "#1E293B");
                SetBrush(res, "SuccessBrush", "#10B981");
                SetBrush(res, "WarningBrush", "#F59E0B");
                SetBrush(res, "DangerBrush", "#EF4444");
                SetBrush(res, "ScrollThumbBrush", "#333C4D");
                SetBrush(res, "ScrollThumbHoverBrush", "#4B5568");
            }
            else
            {
                SetBrush(res, "WindowBgBrush", "#F8FAFC");
                SetBrush(res, "SidebarBgBrush", "#F1F5F9");
                SetBrush(res, "CardBgBrush", "#FFFFFF");
                SetBrush(res, "CardHoverBrush", "#E2E8F0");
                SetBrush(res, "InputBgBrush", "#F8FAFC");
                SetBrush(res, "BorderSubtleBrush", "#CBD5E1");
                SetBrush(res, "BorderFocusBrush", "#2563EB");
                SetBrush(res, "TextPrimaryBrush", "#0F172A");
                SetBrush(res, "TextSecondaryBrush", "#475569");
                SetBrush(res, "TextMutedBrush", "#64748B");
                SetBrush(res, "AccentBrush", "#2563EB");
                SetBrush(res, "AccentHoverBrush", "#1D4ED8");
                SetBrush(res, "AccentSubtleBrush", "#DBEAFE");
                SetBrush(res, "SuccessBrush", "#059669");
                SetBrush(res, "WarningBrush", "#D97706");
                SetBrush(res, "DangerBrush", "#DC2626");
                SetBrush(res, "ScrollThumbBrush", "#CBD5E1");
                SetBrush(res, "ScrollThumbHoverBrush", "#94A3B8");
            }

            if (!_templatesInstalled)
            {
                InstallModernScrollBarAndSliderStyles(res);
                _templatesInstalled = true;
            }

            ThemeChanged?.Invoke(theme);
        }

        private static void InstallModernScrollBarAndSliderStyles(ResourceDictionary res)
        {
            const string xaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

    <Style x:Key=""ScrollBarThumbStyle"" TargetType=""{x:Type Thumb}"">
        <Setter Property=""IsTabStop"" Value=""False""/>
        <Setter Property=""Focusable"" Value=""False""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Thumb}"">
                    <Border x:Name=""ThumbBorder""
                            Background=""{DynamicResource ScrollThumbBrush}""
                            CornerRadius=""3""
                            Margin=""2,1,2,1""/>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""ThumbBorder"" Property=""Background"" Value=""{DynamicResource ScrollThumbHoverBrush}""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType=""{x:Type ScrollBar}"">
        <Setter Property=""Stylus.IsFlicksEnabled"" Value=""False""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Width"" Value=""10""/>
        <Setter Property=""MinWidth"" Value=""10""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ScrollBar}"">
                    <Grid Background=""Transparent"">
                        <Track x:Name=""PART_Track"" IsDirectionReversed=""true"">
                            <Track.Thumb>
                                <Thumb Style=""{StaticResource ScrollBarThumbStyle}""/>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property=""Orientation"" Value=""Horizontal"">
                <Setter Property=""Width"" Value=""Auto""/>
                <Setter Property=""Height"" Value=""10""/>
                <Setter Property=""MinHeight"" Value=""10""/>
                <Setter Property=""Template"">
                    <Setter.Value>
                        <ControlTemplate TargetType=""{x:Type ScrollBar}"">
                            <Grid Background=""Transparent"">
                                <Track x:Name=""PART_Track"" IsDirectionReversed=""False"">
                                    <Track.Thumb>
                                        <Thumb Style=""{StaticResource ScrollBarThumbStyle}""/>
                                    </Track.Thumb>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key=""SliderThumbStyle"" TargetType=""{x:Type Thumb}"">
        <Setter Property=""Width"" Value=""16""/>
        <Setter Property=""Height"" Value=""16""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Thumb}"">
                    <Border Width=""16"" Height=""16"" CornerRadius=""8""
                            Background=""{DynamicResource AccentBrush}""
                            BorderBrush=""#FFFFFF"" BorderThickness=""2""
                            Cursor=""Hand""/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType=""{x:Type Slider}"">
        <Setter Property=""Stylus.IsPressAndHoldEnabled"" Value=""False""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Slider}"">
                    <Grid VerticalAlignment=""Center"">
                        <Border Height=""4"" CornerRadius=""2""
                                Background=""{DynamicResource BorderSubtleBrush}""
                                VerticalAlignment=""Center""/>
                        <Track x:Name=""PART_Track"">
                            <Track.Thumb>
                                <Thumb Style=""{StaticResource SliderThumbStyle}""/>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>";

            try
            {
                if (XamlReader.Parse(xaml) is ResourceDictionary customStyles)
                {
                    res.MergedDictionaries.Add(customStyles);
                }
            }
            catch
            {
                // Ignore if style merge fails
            }
        }

        private static void SetBrush(ResourceDictionary dict, string key, string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dict[key] = brush;
        }

        public static Brush GetBrush(string key)
        {
            if (Application.Current?.Resources != null && Application.Current.Resources.Contains(key))
            {
                return (Brush)Application.Current.Resources[key];
            }
            return Brushes.Transparent;
        }
    }
}
