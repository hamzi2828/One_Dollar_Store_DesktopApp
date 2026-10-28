using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DollarCoAttendance
{
    // The Sales button: opens the POS screen (config posUrl, the website's
    // /admin/pos) in its own Microsoft Edge "app" window -- no tabs, no address
    // bar, maximised -- with its own browser profile under
    // %LOCALAPPDATA%\DollarCo\PosBrowser, so the POS keeps its own saved sign-in
    // and its offline copy (the website's service worker + IndexedDB) apart
    // from anyone's everyday browsing.
    //
    // No server call happens first: with the internet down the POS screen
    // still opens from its offline copy. Clicking again while it is open just
    // starts Edge again with the same profile, and Edge brings the window
    // forward. No Edge: Chrome with the same switches. Neither: the default
    // browser (an ordinary tab).
    internal static class PosLauncher
    {
        public static string ProfileFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DollarCo", "PosBrowser");
            }
        }

        // Returns null when a browser was started, otherwise why not (for the screen).
        public static string Open(string url)
        {
            string problem = AppConfig.PosUrlProblem(url);
            if (problem != null) url = AppConfig.LivePosUrl;

            string profile = ProfileFolder;
            try { Directory.CreateDirectory(profile); }
            catch (Exception ex) { Log.Error("Could not create " + profile, ex); }

            string args = "--app=" + Quote(url) + " --user-data-dir=" + Quote(profile)
                + " --start-maximized --no-first-run --no-default-browser-check";

            foreach (string exe in new[] { FindBrowser("msedge.exe", @"Microsoft\Edge\Application"),
                                           FindBrowser("chrome.exe", @"Google\Chrome\Application") })
            {
                if (exe == null) continue;
                try
                {
                    var start = new ProcessStartInfo(exe, args);
                    start.UseShellExecute = false;
                    start.WorkingDirectory = Path.GetDirectoryName(exe);
                    Process.Start(start);
                    Log.Info("Sales: opened " + url + " with " + exe);
                    return null;
                }
                catch (Exception ex)
                {
                    Log.Error("Sales: could not start " + exe, ex);
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log.Info("Sales: opened " + url + " in the default browser (no Edge or Chrome found)");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Sales: could not open " + url, ex);
                return "The POS screen could not be opened: no web browser was found. Install Microsoft Edge.";
            }
        }

        // The browser's exe: Windows' App Paths registration first (where the
        // installer put it), then the usual install folders.
        private static string FindBrowser(string exeName, string relativeFolder)
        {
            string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName;
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey entry = root.OpenSubKey(key))
                        {
                            string path = entry != null ? entry.GetValue(null) as string : null;
                            if (!string.IsNullOrEmpty(path))
                            {
                                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                                if (File.Exists(path)) return path;
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }

            foreach (string root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    string path = Path.Combine(Path.Combine(root, relativeFolder), exeName);
                    if (File.Exists(path)) return path;
                }
                catch (Exception) { }
            }
            return null;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "%22") + "\"";
        }
    }
}
