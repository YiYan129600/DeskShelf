using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace DeskShelf
{
    public sealed class PreviewRow : INotifyPropertyChanged
    {
        public MovePlan Plan { get; private set; }
        public string Name { get { return Plan.Item.Name; } }
        public string Target { get; private set; }
        public bool Available { get; private set; }
        public string FullTarget { get { return Plan.Destination; } }
        bool marked;
        public bool Marked {
            get { return marked; }
            set { if (marked == value || !Available) return; marked = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Marked"));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        internal PreviewRow(MovePlan plan)
        {
            Plan = plan;
            Available = !File.Exists(plan.Destination) && !Directory.Exists(plan.Destination);
            marked = Available && plan.Item.Category != "应用";
            Target = "桌面归档  /  " + plan.Item.Category + (Available ? "" : "  ·  已有同名项目，跳过");
        }
    }

    internal sealed class ArchivePreview : Window
    {
        readonly List<PreviewRow> rows;
        readonly Button commit;
        readonly CheckBox all;
        bool bulk;
        public List<MovePlan> Chosen { get; private set; }

        public ArchivePreview(Window owner, string root, List<MovePlan> plan)
        {
            rows = plan.Select(p => new PreviewRow(p)).ToList(); Chosen = new List<MovePlan>();
            Owner = owner; Icon = owner.Icon; FontFamily = owner.FontFamily;
            Title = "归档预览 · 桌屿"; Width = 680; Height = 490;
            MinWidth = 470; MinHeight = 330; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (Brush)owner.FindResource("Paper"); Foreground = (Brush)owner.FindResource("Ink");
            var dock = new DockPanel { Margin = new Thickness(24) }; Content = dock;
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
            heading.Children.Add(new TextBlock { Text = "归档预览", FontSize = 25, FontWeight = FontWeights.SemiBold });
            string hint = "勾选要移动的文件，按分类存入下方位置。";
            if (plan.Any(p => p.Item.Category == "应用")) hint += " 快捷方式默认未勾选。";
            heading.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 5) });
            string destination = Path.Combine(root, "桌面归档");
            heading.Children.Add(new TextBlock { Text = destination, ToolTip = destination, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)owner.FindResource("Muted") });
            var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
            all = new CheckBox { Content = "全选", VerticalAlignment = VerticalAlignment.Center, IsThreeState = false };
            DockPanel.SetDock(all, Dock.Left); footer.Children.Add(all);
            commit = new Button { Style = (Style)owner.FindResource("PrimaryAction"), MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(commit, Dock.Right); footer.Children.Add(commit);
            var cancel = new Button { Content = "取消", IsCancel = true, Style = (Style)owner.FindResource("Action") };
            DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
            var list = new ListBox { BorderThickness = new Thickness(1), BorderBrush = (Brush)owner.FindResource("Line"), Background = (Brush)owner.FindResource("White"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var template = new DataTemplate(typeof(PreviewRow));
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));
            check.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Marked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(UIElement.IsEnabledProperty, new Binding("Available"));
            check.SetBinding(FrameworkElement.ToolTipProperty, new Binding("FullTarget"));
            var labels = new FrameworkElementFactory(typeof(StackPanel));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); name.SetValue(TextBlock.FontSizeProperty, 14.0);
            var target = new FrameworkElementFactory(typeof(TextBlock));
            target.SetBinding(TextBlock.TextProperty, new Binding("Target"));
            target.SetValue(TextBlock.ForegroundProperty, owner.FindResource("Muted")); target.SetValue(TextBlock.FontSizeProperty, 12.0);
            labels.AppendChild(name); labels.AppendChild(target); check.AppendChild(labels); template.VisualTree = check;
            list.ItemTemplate = template; list.ItemsSource = rows; dock.Children.Add(list);
            foreach (var row in rows) row.PropertyChanged += delegate { if (!bulk) UpdateCount(); };
            all.Click += delegate {
                bool select = all.IsChecked == true;
                bulk = true; foreach (var row in rows) row.Marked = select; bulk = false; UpdateCount();
            };
            commit.Click += delegate { Chosen = rows.Where(r => r.Marked).Select(r => r.Plan).ToList(); DialogResult = true; };
            UpdateCount();
        }

        void UpdateCount()
        {
            int count = rows.Count(r => r.Marked), available = rows.Count(r => r.Available);
            all.IsEnabled = available > 0;
            all.IsChecked = count > 0 && count == available;
            commit.IsEnabled = count > 0;
            commit.Content = string.Format("移动 {0} 项", count);
        }
    }

    internal static class Dialogs
    {
        public static void ShowDetails(Window owner, string title, List<string> details)
        {
            var box = new Window { Owner = owner, Icon = owner.Icon, Title = title, Width = 570, Height = 340,
                MinWidth = 320, MinHeight = 200, FontFamily = owner.FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var dock = new DockPanel { Margin = new Thickness(16) }; box.Content = dock;
            var done = new Button { Content = "关闭", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Style = (Style)owner.FindResource("Action") };
            DockPanel.SetDock(done, Dock.Bottom); dock.Children.Add(done);
            dock.Children.Add(new TextBox { Text = string.Join(Environment.NewLine + Environment.NewLine, details),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            box.ShowDialog();
        }
    }
}
