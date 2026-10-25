using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ScreenTranslator.UI
{
    public static class UiKit
    {
        public static Border CreateCard(UIElement child, Thickness? padding = null, Thickness? margin = null)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = padding ?? new Thickness(16),
                Margin = margin ?? new Thickness(0, 0, 0, 12),
                SnapsToDevicePixels = true,
                Child = child
            };
            border.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            return border;
        }

        public static StackPanel CreateSectionHeader(string title, string subtitle = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(titleBlock);

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var subBlock = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                subBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(subBlock);
            }
            return sp;
        }

        public static Grid CreateSettingRow(string title, string description, UIElement control, bool showBottomDivider = false)
        {
            var container = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            textStack.Children.Add(titleBlock);

            if (!string.IsNullOrWhiteSpace(description))
            {
                var descBlock = new TextBlock
                {
                    Text = description,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                textStack.Children.Add(descBlock);
            }

            Grid.SetColumn(textStack, 0);
            container.Children.Add(textStack);

            if (control != null)
            {
                if (control is FrameworkElement fe)
                {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                }
                Grid.SetColumn(control, 1);
                container.Children.Add(control);
            }

            return container;
        }

        public static Border CreatePrimaryButton(string label, string iconGeometry, Action onClick)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true
            };
            border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrEmpty(iconGeometry))
            {
                var icon = VectorIcons.CreateColoredIcon(iconGeometry, 15, Brushes.White);
                icon.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(icon);
            }

            var txt = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(txt);
            border.Child = row;

            border.MouseEnter += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "AccentHoverBrush");
            border.MouseLeave += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            border.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return border;
        }

        public static Border CreateSecondaryButton(string label, string iconGeometry, Action onClick, TextBlock labelRefOut = null)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 12, 7),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true
            };
            border.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrEmpty(iconGeometry))
            {
                var icon = VectorIcons.CreateIcon(iconGeometry, 14, "TextPrimaryBrush");
                icon.Margin = new Thickness(0, 0, 7, 0);
                row.Children.Add(icon);
            }

            var txt = labelRefOut ?? new TextBlock();
            txt.Text = label;
            txt.FontSize = 12.5;
            txt.FontWeight = FontWeights.Medium;
            txt.VerticalAlignment = VerticalAlignment.Center;
            txt.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            row.Children.Add(txt);
            border.Child = row;

            border.MouseEnter += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
            border.MouseLeave += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            border.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return border;
        }

        public static Border CreateIconButton(string iconGeometry, string tooltip, Action onClick, double size = 30)
        {
            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                SnapsToDevicePixels = true
            };

            var icon = VectorIcons.CreateIcon(iconGeometry, 15, "TextSecondaryBrush");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            border.Child = icon;

            border.MouseEnter += (s, e) =>
            {
                border.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
                icon.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
            };
            border.MouseLeave += (s, e) =>
            {
                border.Background = Brushes.Transparent;
                icon.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return border;
        }

        public static FrameworkElement CreateToggleSwitch(bool initialValue, Action<bool> onChanged)
        {
            bool state = initialValue;

            var track = new Border
            {
                Width = 40,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true
            };

            var canvas = new Canvas { Width = 38, Height = 20 };
            var thumb = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.White
            };
            Canvas.SetTop(thumb, 3);
            canvas.Children.Add(thumb);
            track.Child = canvas;

            void UpdateVisual(bool val, bool animate)
            {
                if (val)
                {
                    track.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                    track.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                }
                else
                {
                    track.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
                    track.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                }

                double targetLeft = val ? 21 : 3;
                if (animate)
                {
                    var anim = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(140))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    thumb.BeginAnimation(Canvas.LeftProperty, anim);
                }
                else
                {
                    thumb.BeginAnimation(Canvas.LeftProperty, null);
                    Canvas.SetLeft(thumb, targetLeft);
                }
            }

            UpdateVisual(state, animate: false);

            track.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                state = !state;
                UpdateVisual(state, animate: true);
                onChanged?.Invoke(state);
            };
            track.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return track;
        }

        public static FrameworkElement CreateSelectDropdown(
            IReadOnlyList<(string Value, string Label)> options,
            string initialValue,
            Action<string> onSelected,
            double minWidth = 200)
        {
            string currentValue = initialValue;

            var buttonBorder = new Border
            {
                MinWidth = minWidth,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 0, 10, 0),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true
            };
            buttonBorder.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            buttonBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectedText = new TextBlock
            {
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            selectedText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(selectedText, 0);
            grid.Children.Add(selectedText);

            var chevron = VectorIcons.CreateIcon("M 6 9 L 12 15 L 18 9", 13, "TextSecondaryBrush");
            chevron.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(chevron);

            buttonBorder.Child = grid;

            void RefreshLabel()
            {
                foreach (var opt in options)
                {
                    if (string.Equals(opt.Value, currentValue, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedText.Text = opt.Label;
                        return;
                    }
                }
                selectedText.Text = options.Count > 0 ? options[0].Label : currentValue;
            }
            RefreshLabel();

            var popup = new Popup
            {
                PlacementTarget = buttonBorder,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                VerticalOffset = 4
            };

            var popupBorder = new Border
            {
                MinWidth = minWidth,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                SnapsToDevicePixels = true
            };
            popupBorder.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            popupBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var itemsStack = new StackPanel();
            foreach (var opt in options)
            {
                var itemBorder = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 7, 10, 7),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent
                };

                var itemText = new TextBlock
                {
                    Text = opt.Label,
                    FontSize = 12.5
                };
                itemText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                itemBorder.Child = itemText;

                itemBorder.MouseEnter += (s, e) => itemBorder.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
                itemBorder.MouseLeave += (s, e) => itemBorder.Background = Brushes.Transparent;
                string capturedVal = opt.Value;
                itemBorder.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    currentValue = capturedVal;
                    RefreshLabel();
                    popup.IsOpen = false;
                    onSelected?.Invoke(currentValue);
                };
                itemBorder.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                };

                itemsStack.Children.Add(itemBorder);
            }

            popupBorder.Child = itemsStack;
            popup.Child = popupBorder;

            buttonBorder.MouseEnter += (s, e) => buttonBorder.SetResourceReference(Border.BorderBrushProperty, "BorderFocusBrush");
            buttonBorder.MouseLeave += (s, e) => buttonBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            buttonBorder.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                popupBorder.MinWidth = buttonBorder.ActualWidth;
                popup.IsOpen = !popup.IsOpen;
            };
            buttonBorder.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };

            return buttonBorder;
        }

        public static Border CreateTextInput(string initialValue, Action<string> onTextChanged, double width = 240, bool isSecret = false)
        {
            var border = new Border
            {
                Width = width,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 10, 0),
                SnapsToDevicePixels = true
            };
            border.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            if (isSecret)
            {
                var pb = new PasswordBox
                {
                    Password = initialValue ?? string.Empty,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 13,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                pb.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                pb.SetResourceReference(PasswordBox.CaretBrushProperty, "AccentBrush");
                pb.GotFocus += (s, e) => border.SetResourceReference(Border.BorderBrushProperty, "BorderFocusBrush");
                pb.LostFocus += (s, e) => border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                pb.PasswordChanged += (s, e) => onTextChanged?.Invoke(pb.Password);
                border.Child = pb;
            }
            else
            {
                var tb = new TextBox
                {
                    Text = initialValue ?? string.Empty,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 13,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                tb.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                tb.SetResourceReference(TextBoxBase.CaretBrushProperty, "AccentBrush");
                tb.GotFocus += (s, e) => border.SetResourceReference(Border.BorderBrushProperty, "BorderFocusBrush");
                tb.LostFocus += (s, e) => border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                tb.TextChanged += (s, e) => onTextChanged?.Invoke(tb.Text);
                border.Child = tb;
            }

            return border;
        }
    }
}
