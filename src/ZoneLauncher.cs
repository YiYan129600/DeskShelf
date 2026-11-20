using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace DeskShelf
{
    internal static class ZoneLauncher
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                string home = AppDomain.CurrentDomain.BaseDirectory;
                if (args.Length == 1 && (args[0] == "--archive" || Directory.Exists(args[0])))
                {
                    string folder = args[0] == "--archive" ? "" : Quote(Path.GetFullPath(args[0]));
                    Launch(Path.Combine(home, "文件归档.exe"), folder);
                    return;
                }

                bool arrange = args.Length == 1 && args[0] == "--arrange";
                if (args.Length != 0 && !arrange)
                    throw new ArgumentException("请传入现有文件夹，或使用 --archive / --arrange。");

                using (var mutex = new Mutex(false, @"Local\DeskShelf.ZoneLauncher"))
                {
                    bool locked;
                    try { locked = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (locked)
                    {
                        try { OpenZones(Path.Combine(home, "分区"), arrange); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.GetBaseException().Message, "桌屿 · 桌面分区", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        static void OpenZones(string folder, bool arrange)
        {
            string engine = Path.Combine(folder, "PecoFence");
            string program = Path.Combine(engine, "pecofence.exe");
            string cli = Path.Combine(engine, "pecofence-cli.exe");
            string configFolder = Path.Combine(engine, "config");
            string configFile = Path.Combine(configFolder, "config.json");
            string pending = Path.Combine(configFolder, "deskshelf-setup.pending");
            string ready = Path.Combine(configFolder, "deskshelf-setup.ready");
            string recent = Path.Combine(folder, "最近文档.ps1");
            const string statusCommand = "--ascii --compact --timeout 2500 --fields configPath status";
            string answer;
            int status = RunCli(cli, statusCommand, out answer);
            bool starting = status == 3;
            bool initialize = File.Exists(pending) && !File.Exists(ready);
            if (starting)
            {
                if (!File.Exists(program)) throw new FileNotFoundException("未找到分区引擎：" + program);
                if (!initialize && !File.Exists(ready) && !HasSavedConfig(configFolder, configFile))
                {
                    Directory.CreateDirectory(configFolder);
                    File.WriteAllText(pending, "");
                    initialize = true;
                }
                using (var process = Process.Start(CreateProcess(program, "--portable")))
                {
                    var waiting = Stopwatch.StartNew();
                    do
                    {
                        Thread.Sleep(200);
                        status = RunCli(cli, statusCommand, out answer);
                    } while ((status == 3 || status == 4) && !process.HasExited &&
                        waiting.Elapsed < TimeSpan.FromSeconds(22));
                }
            }
            if (status != 0)
                throw new InvalidOperationException("分区引擎暂未就绪。请稍后重试，并确认已解压完整目录。");

            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(answer);
            object activeConfig;
            if (data == null || !data.TryGetValue("configPath", out activeConfig) ||
                !SamePath(activeConfig as string, configFile))
                throw new InvalidOperationException("当前用户已启动其他位置的 PecoFence。请从通知区域退出该实例，再启动此处的桌屿。");

            if (initialize)
            {
                RunSetup(folder, configFile, "-UseStarterLayout -InstallStarterRules");
                File.WriteAllText(pending, recent);
                File.Move(pending, ready);
            }
            else if (File.Exists(ready))
            {
                string previousRecent = File.ReadAllText(ready);
                if (!SamePath(previousRecent, recent))
                {
                    RunSetup(folder, configFile, "-PreviousRecentPath " + Quote(previousRecent));
                    File.WriteAllText(ready, recent);
                }
            }

            if (arrange)
            {
                if (RunCli(cli, "--ascii --compact --timeout 5000 --fields crowdedMonitors fence arrange", out answer) != 0)
                    throw new InvalidOperationException("这次自动排列未完成，请稍后重试。");
                data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(answer);
                if (data != null && data.ContainsKey("crowdedMonitors") && Convert.ToInt32(data["crowdedMonitors"]) > 0)
                    MessageBox.Show("部分屏幕空间不足，保留了该屏原布局。请缩小分区后再排列。", "桌屿 · 自动排列");
                return;
            }
            if (starting || initialize || RunCli(cli, "--timeout 3500 settings open-ui", out answer) == 0) return;
            throw new InvalidOperationException("暂时无法打开分区设置。可以从任务栏通知区域的 PecoFence 图标进入设置。");
        }

        static bool HasSavedConfig(string folder, string config)
        {
            if (File.Exists(config) || File.Exists(Path.Combine(folder, "config.bak"))) return true;
            string backups = Path.Combine(folder, "backups");
            return Directory.Exists(backups) && Directory.GetFiles(backups, "*.json").Length > 0;
        }

        static bool SamePath(string one, string two)
        {
            return !string.IsNullOrWhiteSpace(one) &&
                string.Equals(Path.GetFullPath(one), Path.GetFullPath(two), StringComparison.OrdinalIgnoreCase);
        }

        static string Quote(string value)
        {
            return "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
        }

        static ProcessStartInfo CreateProcess(string program, string arguments)
        {
            if (!File.Exists(program)) throw new FileNotFoundException("未找到程序：" + program);
            return new ProcessStartInfo(program, arguments) { WorkingDirectory = Path.GetDirectoryName(program), UseShellExecute = false };
        }

        static void Launch(string program, string arguments)
        {
            using (Process.Start(CreateProcess(program, arguments))) { }
        }

        static void RunSetup(string folder, string config, string flags)
        {
            string script = Path.Combine(folder, "初始化分区.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("未找到分区初始化脚本：" + script);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = CreateProcess(powershell, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                Quote(script) + " -ExpectedConfigPath " + Quote(config) + " " + flags);
            info.WorkingDirectory = folder;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var process = Process.Start(info))
            {
                var read = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("分区初始化未完成，重新打开桌屿可重试。" + Environment.NewLine +
                        read.GetAwaiter().GetResult().Trim());
            }
        }

        static int RunCli(string program, string arguments, out string output)
        {
            output = "";
            var info = CreateProcess(program, arguments);
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            using (var proc = Process.Start(info))
            {
                var read = proc.StandardOutput.ReadToEndAsync();
                if (!proc.WaitForExit(6500))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    return 4;
                }
                output = read.GetAwaiter().GetResult();
                return proc.ExitCode;
            }
        }
    }
}
