using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DeskShelf
{
    public sealed class FileRow
    {
        public DeskItem Item { get; private set; }
        public string Name { get { return Item.Name; } }
        public string Category { get { return Item.Category; } }
        public string Detail { get; private set; }
        public ImageSource Icon { get { return FileArt.Icon(Category); } }

        public FileRow(DeskItem item, bool shared)
        {
            Item = item;
            string ext = Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
            if (item.Category == "文件夹") Detail = "文件夹";
            else if (ext == "LNK" || ext == "URL") Detail = "快捷方式";
            else Detail = ext.Length == 0 ? "文件" : ext;
            if (shared) { Detail += "  ·  公用桌面"; return; }
            if (item.Category == "文件夹") return;
            try { Detail += "  ·  " + FormatSize(new FileInfo(item.FullPath).Length); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes; int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return size.ToString(unit == 0 ? "0" : "0.#") + " " + units[unit];
        }
    }

    internal static class FileArt
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();
        public static string Glyph(string group)
        {
            switch (group)
            {
                case "全部": return "▦"; case "文档": return "▤"; case "图片": return "▧";
                case "影音": return "♪"; case "压缩包": return "▥"; case "应用": return "⌘";
                case "文件夹": return "⊟"; default: return "◇";
            }
        }

        public static ImageSource Icon(string group)
        {
            ImageSource found;
            if (cache.TryGetValue(group, out found)) return found;
            string shape, color;
            switch (group)
            {
                case "文档":
                    shape = "M5,2 L14,2 20,8 20,22 5,22 Z M14,2 L14,8 20,8 M8,12 H16 M8,16 H15"; color = "#387260"; break;
                case "图片":
                    shape = "M2,4 H22 V20 H2 Z M3,18 L9,12 12,15 16,10 22,16 M8,9 A1.25,1.25 0 1 1 5.5,9 A1.25,1.25 0 1 1 8,9"; color = "#8A563C"; break;
                case "影音":
                    shape = "M5,3 L21,12 5,21 Z"; color = "#76577D"; break;
                case "压缩包":
                    shape = "M3,6 H21 V21 H3 Z M2,3 H22 V7 H2 Z M9,11 H15"; color = "#906F32"; break;
                case "应用":
                    shape = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z"; color = "#4B6A90"; break;
                case "文件夹":
                    shape = "M2,6 L2,4 9,4 12,7 21,7 21,20 2,20 Z M2,10 H21"; color = "#9C7532"; break;
                default:
                    shape = "M5,2 H14 L20,8 V22 H5 Z M14,2 V8 H20"; color = "#60736A"; break;
            }
            var brush = (Brush)new BrushConverter().ConvertFromString(color);
            var pen = new Pen(brush, 1.65) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
                dc.DrawGeometry(null, pen, Geometry.Parse(shape));
            }
            var image = new DrawingImage(drawing); image.Freeze(); cache.Add(group, image); return image;
        }
    }
}
