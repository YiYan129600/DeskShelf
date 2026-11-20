using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskShelf
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                Window window;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml"))
                    window = (Window)XamlReader.Load(stream);
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskShelf.ico"))
                    window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                new Organizer(window, args.Length == 1 ? args[0] : null);
                app.Run(window);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.GetBaseException().Message, "桌屿 · 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public sealed class GroupRow
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Glyph { get; set; }
        public int Count { get; set; }
    }

    internal sealed class Organizer
    {
        const string All = "全部";
        readonly string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        readonly string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        readonly Window window;
        readonly ListBox nav, files;
        readonly TextBox search;
        readonly TextBlock title, path, empty, status;
        readonly Button archive, undo, openArchive, pin, reload, choose, desktopButton;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly Dictionary<ShelfWindow, PinnedGroup> shelves = new Dictionary<ShelfWindow, PinnedGroup>();
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        List<FileRow> rows = new List<FileRow>();
        List<FileRow> visible = new List<FileRow>();
        string root;
        string group = All;
        int generation;
        bool mutating, scanning, updating, closing, undoAvailable;
        string notice;

        T Get<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }

        public Organizer(Window window, string initialFolder)
        {
            this.window = window;
            nav = Get<ListBox>("NavList"); files = Get<ListBox>("FilesList"); search = Get<TextBox>("SearchBox");
            title = Get<TextBlock>("SectionTitle"); path = Get<TextBlock>("RootLabel");
            empty = Get<TextBlock>("EmptyLabel"); status = Get<TextBlock>("StatusLabel");
            archive = Get<Button>("ArchiveButton"); undo = Get<Button>("UndoButton");
            openArchive = Get<Button>("OpenArchiveButton"); pin = Get<Button>("PinButton");
            reload = Get<Button>("RefreshButton"); choose = Get<Button>("ChooseRootButton");
            desktopButton = Get<Button>("DesktopButton");
            Get<Button>("ZonesButton").Click += delegate {
                try { Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "桌屿.exe")) { UseShellExecute = true }); }
                catch (Exception ex) { ShowError(ex); }
            };
            choose.Click += delegate { ChooseFolder(); };
            desktopButton.Click += delegate { ChangeRoot(desktop); };
            reload.Click += delegate { notice = null; Refresh(); };
            openArchive.Click += delegate { OpenPath(Path.Combine(root, "桌面归档")); };
            pin.Click += delegate { OpenShelf(new PinnedGroup { Root = root, Category = group }); };
            archive.Click += delegate { PreviewArchive(); };
            undo.Click += delegate { RunOperation(true, null); };
            nav.SelectionChanged += delegate {
                var item = nav.SelectedItem as GroupRow;
                if (!updating && item != null) { group = item.Key; notice = null; Filter(false); }
            };
            search.TextChanged += delegate { if (!updating) { notice = null; Filter(false); } };
            files.SelectionChanged += delegate { if (!updating) UpdateActions(); };
            files.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e) {
                var hit = ItemsControl.ContainerFromElement(files, e.OriginalSource as DependencyObject) as ListBoxItem;
                var row = hit == null ? null : hit.DataContext as FileRow;
                if (row != null && e.ChangedButton == MouseButton.Left) { OpenPath(row.Item.FullPath); e.Handled = true; }
            };
            files.KeyDown += delegate(object s, KeyEventArgs e) {
                var row = files.SelectedItem as FileRow;
                if (e.Key == Key.Enter && row != null) { OpenPath(row.Item.FullPath); e.Handled = true; }
            };
            window.PreviewKeyDown += delegate(object s, KeyEventArgs e) {
                if (e.Key == Key.F5 && !mutating) { notice = null; Refresh(); e.Handled = true; }
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
            };
            AttachContextMenu();
            debounce.Tick += delegate { debounce.Stop(); if (!mutating) Refresh(); };
            var settings = Preferences.Load();
            var initial = Directory.Exists(initialFolder) ? initialFolder :
                Directory.Exists(settings.Root) ? settings.Root : desktop;
            window.Loaded += delegate {
                ChangeRoot(initial);
                if (initialFolder == null)
                    foreach (var shelf in settings.Shelves.Where(p => Directory.Exists(p.Root) &&
                        (p.Category == All || FileEngine.Categories.Contains(p.Category)))) OpenShelf(shelf);
            };
            window.Closing += delegate(object s, CancelEventArgs e) {
                if (mutating) { e.Cancel = true; status.Text = "正在处理文件，完成后即可关闭。"; return; }
                closing = true;
                var live = shelves.Select(pair => PinnedGroup.Capture(pair.Key, pair.Value)).ToList();
                Preferences.Save(root, live);
                debounce.Stop(); DisposeWatchers();
            };
        }

        void AttachContextMenu()
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "打开" }; var locate = new MenuItem { Header = "在资源管理器中显示" };
            menu.Items.Add(open); menu.Items.Add(locate); files.ContextMenu = menu;
            files.PreviewMouseRightButtonDown += delegate(object s, MouseButtonEventArgs e) {
                var hit = ItemsControl.ContainerFromElement(files, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (hit == null) files.SelectedIndex = -1;
                else if (!hit.IsSelected) files.SelectedItem = hit.DataContext;
            };
            files.ContextMenuOpening += delegate(object s, ContextMenuEventArgs e) { if (files.SelectedItem == null) e.Handled = true; };
            open.Click += delegate { var row = files.SelectedItem as FileRow; if (row != null) OpenPath(row.Item.FullPath); };
            locate.Click += delegate {
                var row = files.SelectedItem as FileRow;
                if (row == null) return;
                try { Process.Start("explorer.exe", "/select,\"" + row.Item.FullPath + "\""); }
                catch (Exception ex) { ShowError(ex); }
            };
        }

        void ChooseFolder()
        {
            using (var dlg = new Forms.FolderBrowserDialog { Description = "选择要整理的文件夹", ShowNewFolderButton = false, SelectedPath = root })
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (dlg.ShowDialog(new DialogOwner(hwnd)) == Forms.DialogResult.OK) ChangeRoot(dlg.SelectedPath);
            }
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        void ChangeRoot(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) { status.Text = "未找到桌面路径，请使用「选择文件夹」。"; return; }
            root = new DirectoryInfo(folder).FullName;
            path.Text = root; path.ToolTip = root; notice = null; group = All;
            updating = true; search.Clear(); updating = false;
            rows.Clear(); undoAvailable = false;
            SetGroups(); Filter(false);
            DisposeWatchers();
            Watch(root);
            if (Same(root, desktop) && !Same(root, common) && Directory.Exists(common)) Watch(common);
            Refresh();
        }

        List<DeskItem> ScanFolder(string folder)
        {
            var items = FileEngine.Scan(folder);
            if (Same(folder, desktop) && !Same(folder, common) && Directory.Exists(common))
            {
                var names = new HashSet<string>(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
                try {
                    foreach (var item in FileEngine.Scan(common))
                    {
                        if (!names.Add(item.Name)) continue;
                        item.CanMove = false;
                        items.Add(item);
                    }
                } catch (UnauthorizedAccessException) { } catch (IOException) { }
            }
            return items;
        }

        async void Refresh()
        {
            if (closing || mutating || root == null) return;
            int version = ++generation;
            string folder = root;
            scanning = true; status.Text = "正在读取文件…"; UpdateActions();
            try
            {
                var result = await Task.Run(() => new {
                    Rows = ScanFolder(folder).Select(i => new FileRow(i, Same(Path.GetDirectoryName(i.FullPath), common))).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    Undo = FileEngine.CanUndo(folder)
                });
                if (version != generation || closing) return;
                rows = result.Rows; undoAvailable = result.Undo; SetGroups();
            }
            catch (Exception ex)
            {
                if (version != generation || closing) return;
                rows = new List<FileRow>(); undoAvailable = false;
                notice = "读取失败：" + ex.GetBaseException().Message; SetGroups();
            }
            if (version != generation || closing) return;
            scanning = false; Filter(true);
        }

        void SetGroups()
        {
            var groups = new[] { All }.Concat(FileEngine.Categories).Select(k =>
                new GroupRow { Key = k, Label = k == All ? "全部文件" : k, Glyph = FileArt.Glyph(k),
                    Count = k == All ? rows.Count : rows.Count(r => r.Category == k) }).ToList();
            updating = true;
            nav.ItemsSource = groups; nav.SelectedItem = groups.First(g => g.Key == group);
            updating = false;
        }

        void Filter(bool preserveSelection)
        {
            var selected = preserveSelection ? new HashSet<string>(files.SelectedItems.Cast<FileRow>().Select(r => r.Item.FullPath), StringComparer.OrdinalIgnoreCase) : null;
            var query = search.Text.Trim();
            visible = rows.Where(r => (group == All || r.Category == group) &&
                r.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            updating = true;
            files.ItemsSource = visible;
            if (selected != null && selected.Count > 0)
                foreach (var row in visible.Where(r => selected.Contains(r.Item.FullPath))) files.SelectedItems.Add(row);
            updating = false;
            title.Text = group == All ? "全部文件" : group;
            empty.Text = query.Length > 0 ? "没有匹配的文件" : (group == All ? "这个位置还没有可显示的文件" : "这个分组暂时没有文件");
            empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActions();
        }

        List<DeskItem> Candidates()
        {
            var selected = files.SelectedItems.Count > 0 ? files.SelectedItems.Cast<FileRow>() : visible;
            return selected.Where(r => r.Item.CanMove).Select(r => r.Item).ToList();
        }

        void UpdateActions()
        {
            bool available = !mutating && !scanning && root != null;
            var count = Candidates().Count;
            archive.IsEnabled = available && count > 0;
            archive.ToolTip = string.Format("预览{0}的 {1} 个可归档文件", files.SelectedItems.Count > 0 ? "选中" : "当前列表", count);
            undo.IsEnabled = available && undoAvailable;
            openArchive.IsEnabled = available && Directory.Exists(Path.Combine(root ?? "", "桌面归档"));
            choose.IsEnabled = desktopButton.IsEnabled = reload.IsEnabled = !mutating;
            pin.IsEnabled = available;
            if (mutating || scanning) return;
            status.ToolTip = notice;
            status.Text = notice ?? (files.SelectedItems.Count > 0 ?
                string.Format("已选 {0} 项  ·  {1} 项可归档", files.SelectedItems.Count, count) :
                string.Format("共 {0} 项  ·  {1} 项可归档", visible.Count, count));
        }

        void PreviewArchive()
        {
            var folder = root;
            try
            {
                var plan = FileEngine.Plan(folder, Candidates());
                if (plan.Count == 0) { notice = "当前选择中没有可归档的文件。"; UpdateActions(); return; }
                var dialog = new ArchivePreview(window, folder, plan);
                if (dialog.ShowDialog() == true && dialog.Chosen.Count > 0) RunOperation(false, dialog.Chosen);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        async void RunOperation(bool reverting, List<MovePlan> plan)
        {
            if (mutating) return;
            string folder = root;
            ++generation; scanning = false; mutating = true;
            debounce.Stop(); UpdateActions();
            status.Text = reverting ? "正在撤销上次归档…" : "正在归档所选文件…";
            try
            {
                var result = await Task.Run(() => reverting ? FileEngine.Undo(folder) : FileEngine.Apply(folder, plan));
                notice = string.Format(reverting ? "已还原 {0} 项" : "已归档 {0} 项", result.Succeeded);
                if (result.Issues != null && result.Issues.Count > 0)
                {
                    notice += string.Format("，有 {0} 条提示", result.Issues.Count);
                    Dialogs.ShowDetails(window, notice, result.Issues);
                }
            }
            catch (Exception ex) { notice = "操作未完成：" + ex.GetBaseException().Message; ShowError(ex); }
            finally {
                mutating = false; debounce.Stop(); Refresh();
                foreach (var shelf in shelves.Where(p => Same(p.Value.Root, folder)).Select(p => p.Key).ToList()) shelf.RefreshItems();
            }
        }

        void Watch(string folder)
        {
            if (!Directory.Exists(folder)) return;
            FileSystemWatcher watcher = null;
            try {
                watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += QueueRefresh; watcher.Created += QueueRefresh; watcher.Deleted += QueueRefresh;
                watcher.Renamed += QueueRefresh; watcher.Error += delegate { ScheduleRefresh(); };
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            } catch (Exception) { if (watcher != null) watcher.Dispose(); }
        }

        void QueueRefresh(object sender, FileSystemEventArgs e) { ScheduleRefresh(); }
        void ScheduleRefresh()
        {
            if (!window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(new Action(() => {
                    if (closing || mutating) return;
                    debounce.Stop(); debounce.Start();
                }));
        }
        void DisposeWatchers() { foreach (var w in watchers) w.Dispose(); watchers.Clear(); }

        void OpenShelf(PinnedGroup info)
        {
            var existing = shelves.FirstOrDefault(p => Same(p.Value.Root, info.Root) && p.Value.Category == info.Category).Key;
            if (existing != null) { existing.WindowState = WindowState.Normal; existing.Activate(); return; }
            var shelf = new ShelfWindow(info.Root, info.Category, () => ScanFolder(info.Root), i => OpenPath(i.FullPath));
            shelf.Icon = window.Icon;
            if (info.HasBounds) info.Restore(shelf);
            shelves.Add(shelf, info);
            shelf.Closed += delegate { shelves.Remove(shelf); };
            shelf.Show();
        }

        void OpenPath(string value)
        {
            try {
                if (!File.Exists(value) && !Directory.Exists(value)) throw new FileNotFoundException("此项目已移动或删除，请刷新列表。");
                Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(value) });
            }
            catch (Exception ex) { ShowError(ex); }
        }
        void ShowError(Exception ex) { MessageBox.Show(window, ex.GetBaseException().Message, "桌屿", MessageBoxButton.OK, MessageBoxImage.Information); }
        sealed class DialogOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal DialogOwner(IntPtr handle) { Handle = handle; }
        }
    }
}
