using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskShelf
{
    public class ShelfWindow : Window
    {
        private static readonly Brush Ink = MakeBrush(0x23, 0x3F, 0x36);
        private static readonly Brush Muted = MakeBrush(0x4F, 0x68, 0x5F);
        private static readonly Brush Accent = MakeBrush(0x21, 0x73, 0x61);
        private static readonly Brush Paper = MakeBrush(0xF7, 0xF5, 0xED);
        private static readonly Brush Surface = MakeBrush(0xFF, 0xFE, 0xF9);
        private static readonly Brush Line = MakeBrush(0xD7, 0xE0, 0xD6);
        private static readonly Brush Error = MakeBrush(0x93, 0x39, 0x29);

        private readonly string category;
        private readonly Func<List<DeskItem>> getItems;
        private readonly Action<DeskItem> openItem;
        private readonly ListBox list;
        private readonly TextBlock emptyState;
        private readonly TextBlock status;
        private readonly Button reveal;

        private sealed class ShelfRow
        {
            public ShelfRow(DeskItem item) { Item = item; }
            public DeskItem Item { get; private set; }
            public string Name { get { return Item.Name; } }
            public string FullPath { get { return Item.FullPath; } }
        }

        public ShelfWindow(string root, string category, Func<List<DeskItem>> getItems, Action<DeskItem> openItem)
        {
            this.category = category;
            this.getItems = getItems;
            this.openItem = openItem;

            Title = category + " · 桌面分组";
            Width = 410;
            Height = 460;
            MinWidth = 330;
            MinHeight = 290;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Paper;
            Foreground = Ink;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            UseLayoutRounding = true;

            var layout = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = layout;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            layout.Children.Add(header);

            var pin = new CheckBox
            {
                Content = "置顶",
                Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "把此分组保持在其他窗口上方"
            };
            pin.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("Topmost") { Source = this, Mode = BindingMode.TwoWay });
            DockPanel.SetDock(pin, Dock.Right);
            header.Children.Add(pin);

            var heading = new StackPanel();
            header.Children.Add(heading);
            heading.Children.Add(new TextBlock
            {
                Text = category,
                ToolTip = category,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
                FontSize = 23
            });
            heading.Children.Add(new TextBlock
            {
                Text = root,
                ToolTip = root,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Muted,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var content = new Border
            {
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Background = Surface,
                Padding = new Thickness(5)
            };
            Grid.SetRow(content, 1);
            layout.Children.Add(content);
            var contentGrid = new Grid();
            content.Child = contentGrid;

            list = new ListBox
            {
                BorderThickness = new Thickness(0),
                Foreground = Ink,
                Background = Surface,
                ItemTemplate = MakeItemTemplate()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 5, 5, 5)));
            list.ItemContainerStyle = itemStyle;
            contentGrid.Children.Add(list);

            emptyState = new TextBlock
            {
                Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false,
                Text = "此分组暂无项目"
            };
            contentGrid.Children.Add(emptyState);

            var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);

            status = new TextBlock
            {
                Foreground = Muted,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            footer.Children.Add(status);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(actions, 1);
            footer.Children.Add(actions);
            reveal = MakeButton("定位", "在资源管理器中显示所选项目");
            reveal.IsEnabled = false;
            reveal.Click += delegate { RevealSelectedItem(); };
            actions.Children.Add(reveal);
            var refresh = MakeButton("刷新", "重新读取此分组");
            refresh.Click += delegate { RefreshItems(); };
            actions.Children.Add(refresh);

            list.SelectionChanged += delegate { reveal.IsEnabled = list.SelectedItem is ShelfRow; };
            list.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs args)
            {
                var source = args.OriginalSource as DependencyObject;
                if (args.ChangedButton != MouseButton.Left || source == null)
                    return;

                var container = ItemsControl.ContainerFromElement(list, source) as ListBoxItem;
                var row = container == null ? null : container.Content as ShelfRow;
                if (row != null)
                {
                    OpenItem(row);
                    args.Handled = true;
                }
            };
            list.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    OpenItem(list.SelectedItem as ShelfRow);
                    args.Handled = true;
                }
            };
            Activated += delegate { RefreshItems(); };
        }

        public void RefreshItems()
        {
            var selected = list.SelectedItem as ShelfRow;
            List<ShelfRow> visible;
            try
            {
                visible = getItems()
                    .Where(item => category == "全部" || item.Category == category)
                    .Select(item => new ShelfRow(item))
                    .ToList();
            }
            catch (Exception exception)
            {
                if (list.Items.Count == 0)
                    emptyState.Text = "无法读取分组，请重试";
                ShowStatus("读取失败：" + exception.Message, Error);
                return;
            }

            list.ItemsSource = visible;
            if (selected != null)
                list.SelectedItem = visible.FirstOrDefault(item => string.Equals(item.FullPath, selected.FullPath, StringComparison.OrdinalIgnoreCase));
            emptyState.Text = "此分组暂无项目";
            emptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus(visible.Count == 0 ? "0 项" : visible.Count + " 项 · 双击打开", Muted);
        }

        private void OpenItem(ShelfRow row)
        {
            if (row == null)
                return;

            try
            {
                openItem(row.Item);
            }
            catch (Exception exception)
            {
                ShowStatus("打开失败：" + exception.Message, Error);
            }
        }

        private void RevealSelectedItem()
        {
            var row = list.SelectedItem as ShelfRow;
            if (row == null)
                return;

            try
            {
                var arguments = "/select,\"" + row.FullPath.TrimEnd('\\') + "\"";
                using (Process.Start("explorer.exe", arguments)) { }
            }
            catch (Exception exception)
            {
                ShowStatus("定位失败：" + exception.Message, Error);
            }
        }

        private void ShowStatus(string message, Brush color)
        {
            status.Text = message;
            status.ToolTip = message;
            status.Foreground = color;
        }

        private static DataTemplate MakeItemTemplate()
        {
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetBinding(FrameworkElement.ToolTipProperty, new Binding("FullPath"));
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            return new DataTemplate(typeof(ShelfRow)) { VisualTree = name };
        }

        private static Button MakeButton(string label, string tooltip)
        {
            return new Button
            {
                Content = label,
                ToolTip = tooltip,
                MinWidth = 49,
                MinHeight = 27,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Foreground = Accent,
                BorderBrush = Line,
                Background = Surface
            };
        }

        private static Brush MakeBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
