using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskShelf
{
    public class DeskItem
    {
        public string Name, FullPath, Category;
        public bool CanMove;
    }

    public class MovePlan
    {
        public DeskItem Item;
        public string Destination;
    }

    public class OperationResult
    {
        public int Succeeded;
        public List<string> Issues = new List<string>();
    }

    public static class FileEngine
    {
        public static readonly string[] Categories =
            { "文档", "图片", "影音", "压缩包", "应用", "文件夹", "其他" };

        private const string ArchiveName = "桌面归档";
        private const string HistoryName = ".deskshelf-undo-v1";
        private const FileAttributes Skipped = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary;
        private const FileAttributes Unmovable = Skipped | FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device;
        private const uint ReadAttributes = 0x80;
        private const uint DeleteAccess = 0x10000;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;

        private static readonly HashSet<string>[] Extensions =
        {
            ExtensionSet("txt md markdown rtf pdf doc docx docm dot dotx odt wps wpt xls xlsx xlsm xlsb csv tsv ods et ett ppt pptx pptm pps ppsx odp dps dpt epub mobi azw3 xmind vsdx dwg dxf one log json xml yaml yml"),
            ExtensionSet("jpg jpeg jpe png gif bmp webp avif heic heif tif tiff svg ico psd ai dng cr2 cr3 nef arw raf orf rw2 raw"),
            ExtensionSet("mp3 wav flac aac m4a wma ogg opus mid midi ape aiff amr mp4 mkv avi mov wmv webm flv m4v mpeg mpg ts mts m2ts rm rmvb 3gp vob srt ass lrc"),
            ExtensionSet("zip rar 7z tar gz bz2 xz zst tgz tbz tbz2 txz cab iso zipx"),
            ExtensionSet("exe msi msix msixbundle appx appxbundle appref-ms lnk url website scr bat cmd com ps1 vbs js jar reg msc cpl")
        };

        public static List<DeskItem> Scan(string root)
        {
            root = NormalizeRoot(root);
            var items = new List<DeskItem>();
            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                string name = Path.GetFileName(path);
                if (SamePath(name, ArchiveName) || SamePath(name, "desktop.ini") || name.StartsWith("~$", StringComparison.Ordinal))
                    continue;

                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) { if (!IsFileFailure(ex)) throw; continue; }

                if ((attributes & Skipped) != 0)
                    continue;

                int category = Classify(name, attributes);
                items.Add(new DeskItem
                {
                    Name = name,
                    FullPath = path,
                    Category = Categories[category],
                    CanMove = IsMovableKind(name, category) && (attributes & Unmovable) == 0 && SafeName(name)
                });
            }

            items.Sort(delegate(DeskItem left, DeskItem right)
            {
                int group = Array.IndexOf(Categories, left.Category).CompareTo(Array.IndexOf(Categories, right.Category));
                return group == 0 ? StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name) : group;
            });
            return items;
        }

        public static List<MovePlan> Plan(string root, IEnumerable<DeskItem> selected)
        {
            root = NormalizeRoot(root);
            var plans = new List<MovePlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeskItem item in selected)
            {
                int category;
                if (EligibleItem(root, item, out category) && seen.Add(item.Name))
                    plans.Add(new MovePlan { Item = item, Destination = ArchivePath(root, category, item.Name) });
            }
            return plans;
        }

        public static OperationResult Apply(string root, IEnumerable<MovePlan> plan)
        {
            var result = new OperationResult();
            Journal journal = null;
            string batch = Guid.NewGuid().ToString("N");
            try
            {
                root = NormalizeRoot(root);
                using (var directories = new DirectoryPins(root))
                {
                    try
                    {
                        foreach (MovePlan move in plan)
                        {
                            DeskItem item = move == null ? null : move.Item;
                            int category;
                            if (!EligibleItem(root, item, out category)
                                || !SamePath(move.Destination, ArchivePath(root, category, item.Name)))
                            {
                                result.Issues.Add(DisplayName(item) + "：归档路径无效或该项目不能移动。");
                                continue;
                            }

                            SafeFileHandle file = null;
                            JournalEntry entry;
                            try
                            {
                                file = OpenFile(Path.Combine(root, item.Name), ReadAttributes | DeleteAccess);
                                NativeFileInfo info = FileInfo(file);
                                if (((FileAttributes)info.Attributes & Unmovable) != 0 || info.LinkCount != 1)
                                    throw new IOException("该文件当前无法安全移动。");
                                entry = new JournalEntry(batch, Guid.NewGuid().ToString("N"), category, item.Name, Identity(info));
                            }
                            catch (Exception ex)
                            {
                                if (file != null) file.Dispose();
                                if (!IsFileFailure(ex)) throw;
                                result.Issues.Add(item.Name + "：" + ex.Message);
                                continue;
                            }

                            using (file)
                            {
                                string archive = Path.Combine(root, ArchiveName);
                                if (journal == null)
                                {
                                    directories.Add(archive, true);
                                    journal = Journal.Open(archive, true, true);
                                }

                                SafeFileHandle targetDirectory;
                                try { targetDirectory = PinDirectory(Path.Combine(archive, Categories[category]), true); }
                                catch (Exception ex)
                                {
                                    if (!IsFileFailure(ex)) throw;
                                    result.Issues.Add(item.Name + "：" + ex.Message);
                                    continue;
                                }

                                using (targetDirectory)
                                {
                                    // Persist the intent before the atomic rename so the next process can recover it.
                                    journal.Record(entry);
                                    try
                                    {
                                        Rename(file, ArchivePath(root, category, item.Name));
                                        result.Succeeded++;
                                    }
                                    catch (Exception ex)
                                    {
                                        if (!IsFileFailure(ex)) throw;
                                        result.Issues.Add(item.Name + "：" + ex.Message);
                                        journal.Resolve(entry);
                                    }
                                }
                            }
                        }
                    }
                    finally { if (journal != null) journal.Dispose(); }
                }
            }
            catch (Exception ex) { if (!IsFileFailure(ex)) throw; result.Issues.Add("归档中止：" + ex.Message); }
            return result;
        }

        public static bool CanUndo(string root)
        {
            try
            {
                root = NormalizeRoot(root);
                using (var directories = new DirectoryPins(root))
                {
                    string archive = Path.Combine(root, ArchiveName);
                    directories.Add(archive, false);
                    using (Journal journal = Journal.Open(archive, false, false))
                        return journal != null && journal.Latest.Count > 0;
                }
            }
            catch (Exception ex) { if (!IsFileFailure(ex)) throw; return false; }
        }

        public static OperationResult Undo(string root)
        {
            var result = new OperationResult();
            try
            {
                root = NormalizeRoot(root);
                using (var directories = new DirectoryPins(root))
                {
                    string archive = Path.Combine(root, ArchiveName);
                    try { directories.Add(archive, false); }
                    catch (Win32Exception ex) { if (!Missing(ex)) throw; return result; }

                    using (Journal journal = Journal.Open(archive, true, false))
                    {
                        if (journal == null) return result;
                        foreach (JournalEntry entry in journal.Latest)
                        {
                            try
                            {
                                if (Restore(root, entry)) result.Succeeded++;
                            }
                            catch (Exception ex)
                            {
                                if (!IsFileFailure(ex)) throw;
                                result.Issues.Add(entry.Name + "：" + ex.Message);
                                continue;
                            }
                            journal.Resolve(entry);
                        }
                    }
                }
            }
            catch (Exception ex) { if (!IsFileFailure(ex)) throw; result.Issues.Add("撤销中止：" + ex.Message); }
            return result;
        }

        private static bool Restore(string root, JournalEntry entry)
        {
            string source = Path.Combine(root, entry.Name);
            string destination = ArchivePath(root, entry.Category, entry.Name);
            try
            {
                using (PinDirectory(Path.GetDirectoryName(destination), false))
                using (var archived = OpenFile(destination, ReadAttributes | DeleteAccess))
                {
                    NativeFileInfo info = FileInfo(archived);
                    if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                        && info.LinkCount == 1 && Identity(info) == entry.FileIdentity)
                    {
                        Rename(archived, source);
                        return true;
                    }
                }
            }
            catch (Win32Exception ex) { if (!Missing(ex)) throw; }

            // Covers an interrupted archive before the rename and an interrupted undo after the rename.
            try
            {
                using (var original = OpenFile(source, ReadAttributes))
                {
                    NativeFileInfo info = FileInfo(original);
                    if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                        && Identity(info) == entry.FileIdentity)
                        return false;
                }
            }
            catch (Win32Exception ex) { if (!Missing(ex)) throw; }

            throw new IOException("归档位置的原文件已移动、删除或被替换，撤销记录已保留。");
        }

        private static string NormalizeRoot(string root)
        {
            string full = Path.GetFullPath(root);
            string volume = Path.GetPathRoot(full);
            return full.Length == volume.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ArchivePath(string root, int category, string name)
        {
            return Path.Combine(root, ArchiveName, Categories[category], name);
        }

        private static bool EligibleItem(string root, DeskItem item, out int category)
        {
            category = -1;
            if (item == null || !item.CanMove || !SafeName(item.Name) || !SamePath(item.FullPath, Path.Combine(root, item.Name)))
                return false;
            category = Classify(item.Name, FileAttributes.Normal);
            return IsMovableKind(item.Name, category) && item.Category == Categories[category]
                && !item.Name.StartsWith("~$", StringComparison.Ordinal);
        }

        private static bool IsMovableKind(string name, int category)
        {
            if (category < 4) return true;
            if (category != 4) return false;
            string extension = Path.GetExtension(name);
            return SamePath(extension, ".lnk") || SamePath(extension, ".url");
        }

        private static bool SafeName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                return false;
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$")
                return false;
            return stem.Length != 4 || (!stem.StartsWith("COM", StringComparison.Ordinal) && !stem.StartsWith("LPT", StringComparison.Ordinal))
                || "0123456789¹²³".IndexOf(stem[3]) < 0;
        }

        private static string DisplayName(DeskItem item)
        {
            return item == null || String.IsNullOrEmpty(item.Name) ? "未知项目" : item.Name;
        }

        private static bool SamePath(string left, string right)
        {
            return String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static int Classify(string name, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.Directory) != 0) return 5;
            string extension = Path.GetExtension(name).TrimStart('.');
            for (int index = 0; index < Extensions.Length; index++)
                if (Extensions[index].Contains(extension)) return index;
            return 6;
        }

        private static HashSet<string> ExtensionSet(string values)
        {
            return new HashSet<string>(values.Split(' '), StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsFileFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is ArgumentException;
        }

        private static bool Missing(Win32Exception ex)
        {
            return ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3;
        }

        private static SafeFileHandle PinDirectory(string path, bool create)
        {
            if (create) Directory.CreateDirectory(path);
            var handle = OpenNative(path, ReadAttributes, FileShare.ReadWrite, FileMode.Open, OpenReparsePoint | BackupSemantics);
            try
            {
                var attributes = (FileAttributes)FileInfo(handle).Attributes;
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("目录是链接或不可用（" + path + "）。");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private sealed class DirectoryPins : IDisposable
        {
            private readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();

            public DirectoryPins(string root)
            {
                try
                {
                    string current = Path.GetPathRoot(root);
                    Add(current, false);
                    foreach (string part in root.Substring(current.Length).Split(Path.DirectorySeparatorChar))
                    {
                        if (part.Length == 0) continue;
                        current = Path.Combine(current, part);
                        Add(current, false);
                    }
                }
                catch { Dispose(); throw; }
            }

            public void Add(string path, bool create) { handles.Add(PinDirectory(path, create)); }

            public void Dispose()
            {
                for (int index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
            }
        }

        private sealed class JournalEntry
        {
            public readonly string Batch, Id, Name, FileIdentity;
            public readonly int Category;

            public JournalEntry(string batch, string id, int category, string name, string fileIdentity)
            {
                Batch = batch; Id = id; Category = category; Name = name; FileIdentity = fileIdentity;
            }
        }

        private sealed class Journal : IDisposable
        {
            private const string Header = "DeskShelf-undo-v1\n";
            private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
            private readonly FileStream stream;
            public readonly List<JournalEntry> Latest;

            private Journal(FileStream stream, bool canWrite, bool created)
            {
                this.stream = stream;
                if (created) Append(Header);
                Latest = Read(canWrite);
            }

            public static Journal Open(string archive, bool canWrite, bool create)
            {
                string path = Path.Combine(archive, HistoryName);
                var share = canWrite ? FileShare.Read : FileShare.ReadWrite;
                uint access = GenericRead | (canWrite ? GenericWrite : 0);
                SafeFileHandle handle;
                bool created = false;
                try { handle = OpenNative(path, access, share, FileMode.Open, OpenReparsePoint); }
                catch (Win32Exception ex)
                {
                    if (!Missing(ex)) throw;
                    if (!create) return null;
                    try
                    {
                        handle = OpenNative(path, access, share, FileMode.CreateNew, OpenReparsePoint | (uint)FileAttributes.Hidden);
                        created = true;
                    }
                    catch (Win32Exception conflict)
                    {
                        if (conflict.NativeErrorCode != 80 && conflict.NativeErrorCode != 183) throw;
                        handle = OpenNative(path, access, share, FileMode.Open, OpenReparsePoint);
                    }
                }

                FileStream stream = null;
                try
                {
                    var info = FileInfo(handle);
                    if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.LinkCount != 1)
                        throw new IOException("撤销记录路径不可用。");
                    stream = new FileStream(handle, canWrite ? FileAccess.ReadWrite : FileAccess.Read);
                    return new Journal(stream, canWrite, created);
                }
                catch
                {
                    if (stream == null) handle.Dispose();
                    else stream.Dispose();
                    throw;
                }
            }

            public void Record(JournalEntry entry)
            {
                string name = Convert.ToBase64String(Utf8.GetBytes(entry.Name));
                Append("M\t" + entry.Batch + "\t" + entry.Id + "\t" + entry.Category.ToString(CultureInfo.InvariantCulture)
                    + "\t" + name + "\t" + entry.FileIdentity + "\n");
            }

            public void Resolve(JournalEntry entry) { Append("D\t" + entry.Id + "\n"); }

            private void Append(string value)
            {
                byte[] bytes = Utf8.GetBytes(value);
                stream.Position = stream.Length;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            private List<JournalEntry> Read(bool canWrite)
            {
                if (stream.Length > Int32.MaxValue) throw new IOException("撤销记录过大。");
                var bytes = new byte[(int)stream.Length];
                stream.Position = 0;
                int filled = 0;
                while (filled < bytes.Length)
                {
                    int read = stream.Read(bytes, filled, bytes.Length - filled);
                    if (read == 0) throw new EndOfStreamException("无法完整读取撤销记录。");
                    filled += read;
                }

                int complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
                string content = Utf8.GetString(bytes, 0, complete);
                if (!content.StartsWith(Header, StringComparison.Ordinal)) throw InvalidHistory();
                var entries = new List<JournalEntry>();
                var active = new HashSet<string>(StringComparer.Ordinal);
                using (var reader = new StringReader(content.Substring(Header.Length)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = line.Split('\t');
                        Guid guid;
                        int category;
                        if (parts.Length == 2 && parts[0] == "D" && active.Remove(parts[1])) continue;
                        if (parts.Length != 6 || parts[0] != "M" || !Guid.TryParseExact(parts[1], "N", out guid)
                            || !Guid.TryParseExact(parts[2], "N", out guid) || !active.Add(parts[2])
                            || !Int32.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out category)
                            || category >= Categories.Length || parts[5].Length != 40)
                            throw InvalidHistory();

                        string name;
                        try { name = Utf8.GetString(Convert.FromBase64String(parts[4])); }
                        catch (FormatException) { throw InvalidHistory(); }
                        if (!SafeName(name) || Classify(name, FileAttributes.Normal) != category || !IsMovableKind(name, category))
                            throw InvalidHistory();
                        entries.Add(new JournalEntry(parts[1], parts[2], category, name, parts[5]));
                    }
                }

                if (canWrite && complete < bytes.Length)
                {
                    stream.SetLength(complete);
                    stream.Flush(true);
                }

                var latest = new List<JournalEntry>();
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    JournalEntry entry = entries[index];
                    if (!active.Contains(entry.Id)) continue;
                    if (latest.Count > 0 && entry.Batch != latest[0].Batch) break;
                    latest.Add(entry);
                }
                return latest;
            }

            private static IOException InvalidHistory() { return new IOException("撤销记录格式无效，原记录已保留。"); }

            public void Dispose() { stream.Dispose(); }
        }

        private static SafeFileHandle OpenFile(string path, uint access)
        {
            return OpenNative(path, access, FileShare.ReadWrite, FileMode.Open, OpenReparsePoint | BackupSemantics);
        }

        private static SafeFileHandle OpenNative(string path, uint access, FileShare share, FileMode mode, uint flags)
        {
            SafeFileHandle handle = CreateFile(path, access, share, IntPtr.Zero, mode, flags, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        private static NativeFileInfo FileInfo(SafeFileHandle handle)
        {
            NativeFileInfo info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info;
        }

        private static string Identity(NativeFileInfo info)
        {
            if (info.IndexHigh == 0 && info.IndexLow == 0) throw new IOException("当前磁盘未提供可用于撤销的文件标识。");
            return String.Format(CultureInfo.InvariantCulture, "{0:X8}{1:X8}{2:X8}{3:X8}{4:X8}",
                info.Volume, info.IndexHigh, info.IndexLow, info.CreationHigh, info.CreationLow);
        }

        private static void Rename(SafeFileHandle file, string destination)
        {
            byte[] name = Encoding.Unicode.GetBytes(destination);
            int lengthOffset = IntPtr.Size * 2;
            int nameOffset = lengthOffset + sizeof(uint);
            var buffer = new byte[nameOffset + name.Length + sizeof(char)];
            BitConverter.GetBytes(name.Length).CopyTo(buffer, lengthOffset);
            name.CopyTo(buffer, nameOffset);
            if (SetFileInformationByHandle(file, 3, buffer, (uint)buffer.Length)) return;
            int error = Marshal.GetLastWin32Error();
            if (error == 80 || error == 183) throw new IOException("目标位置已有同名项目，原文件已保留。");
            throw new Win32Exception(error);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInfo
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
            IntPtr security, FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
            byte[] information, uint size);
    }
}
