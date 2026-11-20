using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;

namespace DeskShelf
{
    internal sealed class PinnedGroup
    {
        public string Root, Category;
        public bool Topmost, HasBounds;
        public Rect Bounds;

        public static PinnedGroup Capture(Window window, PinnedGroup info)
        {
            Rect bounds = window.RestoreBounds;
            if (window.WindowState == WindowState.Normal) bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            return new PinnedGroup { Root = info.Root, Category = info.Category, Bounds = bounds, HasBounds = true, Topmost = window.Topmost };
        }
        public void Restore(Window window)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            window.Width = Math.Max(window.MinWidth, Math.Min(Bounds.Width, screen.Width));
            window.Height = Math.Max(window.MinHeight, Math.Min(Bounds.Height, screen.Height));
            var intersect = Rect.Intersect(screen, Bounds);
            if (!intersect.IsEmpty && intersect.Width >= 100 && intersect.Height >= 40)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = Math.Max(screen.Left, Math.Min(Bounds.Left, screen.Right - window.Width));
                window.Top = Math.Max(screen.Top, Math.Min(Bounds.Top, screen.Bottom - window.Height));
            }
            window.Topmost = Topmost;
        }
    }

    internal sealed class Preferences
    {
        static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskShelf", "layout.xml");
        public string Root;
        public readonly List<PinnedGroup> Shelves = new List<PinnedGroup>();
        public static Preferences Load()
        {
            var prefs = new Preferences();
            try {
                if (!File.Exists(file)) return prefs;
                var data = XDocument.Load(file).Root; prefs.Root = (string)data.Attribute("folder");
                foreach (var e in data.Elements("shelf"))
                {
                    var b = new Rect((double)e.Attribute("x"), (double)e.Attribute("y"), (double)e.Attribute("w"), (double)e.Attribute("h"));
                    if (!Valid(b.X) || !Valid(b.Y) || !Valid(b.Width) || !Valid(b.Height)) continue;
                    prefs.Shelves.Add(new PinnedGroup { Root = (string)e.Attribute("folder"), Category = (string)e.Attribute("group"),
                        Topmost = (bool)e.Attribute("top"), Bounds = b, HasBounds = true });
                }
            } catch (Exception) { /* A missing or obsolete preference can use the default layout. */ }
            return prefs;
        }

        static bool Valid(double n) { return !double.IsInfinity(n) && !double.IsNaN(n); }

        public static void Save(string root, IEnumerable<PinnedGroup> shelves)
        {
            if (string.IsNullOrEmpty(root)) return;
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var doc = new XElement("layout", new XAttribute("folder", root),
                    shelves.Where(s => !s.Bounds.IsEmpty).Select(s => new XElement("shelf", new XAttribute("folder", s.Root), new XAttribute("group", s.Category),
                        new XAttribute("x", s.Bounds.X), new XAttribute("y", s.Bounds.Y), new XAttribute("w", s.Bounds.Width), new XAttribute("h", s.Bounds.Height),
                        new XAttribute("top", s.Topmost))));
                doc.Save(temp);
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
