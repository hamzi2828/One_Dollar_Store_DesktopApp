using System;
using System.Collections.Generic;
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
    // First a quick POST /api/desktop/pos-link (FetchSignInCode, ~4 s at most)
    // for a one-time code, so the POS screen opens at /desktop-signin#code=...
    // and signs in as this counter. Any failure (offline, 403, timeout): the
    // configured posUrl opens as before -- with the internet down the POS screen
    // still opens from its offline copy. The code is never written to the log. Clicking again while it is open just
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

        public const int SignInCodeTimeoutMs = 4000;

        // A one-time sign-in code for the POS website, or null when the server
        // could not give one. Runs the HTTP call: never on the UI thread.
        public static string FetchSignInCode(ApiClient api)
        {
            if (api == null || string.IsNullOrEmpty(api.Token)) return null;
            Dictionary<string, object> result = api.Post("api/desktop/pos-link", new Dictionary<string, object>(), SignInCodeTimeoutMs);
            string code = Json.Ok(result) ? Json.Str(Json.Data(result), "code") : null;
            if (code == null) Log.Info("Sales: no sign-in code (HTTP " + Json.HttpStatus(result) + "), opening the POS screen as it is");
            return code;
        }

        // {scheme://host of posUrl}/desktop-signin#code=...&next={path+query of posUrl},
        // or null when posUrl is not a usable address.
        public static string SignInUrl(string posUrl, string code)
        {
            Uri pos;
            if (string.IsNullOrEmpty(code) || AppConfig.PosUrlProblem(posUrl) != null
                || !Uri.TryCreate(posUrl, UriKind.Absolute, out pos)) return null;
            string origin = pos.GetLeftPart(UriPartial.Authority);
            return origin + "/desktop-signin#code=" + Uri.EscapeDataString(code)
                + "&next=" + Uri.EscapeDataString(pos.PathAndQuery);
        }

        // The address without its #fragment (where the sign-in code travels), for the log.
        private static string ForLog(string url)
        {
            int hash = (url ?? string.Empty).IndexOf('#');
            return hash >= 0 ? url.Substring(0, hash) + "#..." : url;
        }

        // Returns null when a browser was started, otherwise why not (for the screen).
        // `url` is posUrl itself or a page on the same site (the sign-in link).
        public static string Open(string url, string posUrl)
        {
            string problem = AppConfig.PosUrlProblem(url) ?? AppConfig.SameSiteProblem(url, posUrl);
            if (problem != null) url = AppConfig.PosUrlProblem(posUrl) == null ? posUrl : AppConfig.LivePosUrl;

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
                    Log.Info("Sales: opened " + ForLog(url) + " with " + exe);
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
                Log.Info("Sales: opened " + ForLog(url) + " in the default browser (no Edge or Chrome found)");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Sales: could not open " + ForLog(url), ex);
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
