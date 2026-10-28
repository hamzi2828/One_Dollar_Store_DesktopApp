using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DollarCoAttendance
{
    // desktopApp.config.json beside the exe: which POS server this counter
    // talks to, what the PC is called in the attendance records, and whether it
    // plays sounds and speaks. The Settings window edits it, so nobody needs a
    // text editor; a hand edit works too.
    //
    //   {
    //     "apiBaseUrl": "https://pos-one-dollar-store-backend.vercel.app/",
    //     "deviceName": "Counter PC",
    //     "posUrl": "https://pos-one-dollar-store-frontend.vercel.app/admin/pos",
    //     "sound": true,
    //     "voice": true
    //   }
    //
    // Every key may be missing (a config from an older build has no posUrl):
    // a missing key takes its default.
    //
    // "readerPriority": "low" may be added by hand (see the README): it asks
    // the reader driver for events even while another window is in front.
    public class AppConfig
    {
        // The live POS server (the shipped default, and the fallback when the
        // file cannot be read).
        public const string LiveServer = "https://pos-one-dollar-store-backend.vercel.app/";

        // The POS screen the Sales button opens (the website, in an Edge app window).
        public const string LivePosUrl = "https://pos-one-dollar-store-frontend.vercel.app/admin/pos";

        public string ApiBaseUrl;
        public string PosUrl = LivePosUrl;
        public string DeviceName;
        public bool SoundOn = true;
        public bool VoiceOn = true;
        public bool ReaderLowPriority;

        // Where the config was looked for, for messages that name the file.
        public string ConfigPath;

        // Set when the file is missing, unreadable or names an unsafe address.
        // The app still starts (on the defaults) and the sign-in window says why.
        public string LoadError;

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktopApp.config.json"); }
        }

        // The name punches are filed under ("fingerprint · Counter PC").
        public string DeviceLabel
        {
            get { return string.IsNullOrEmpty(DeviceName) ? SafeMachineName() : DeviceName; }
        }

        public static string SafeMachineName()
        {
            try { return Environment.MachineName; }
            catch { return "Counter PC"; }
        }

        public bool IsLocalServer
        {
            get
            {
                try { return IsLocalHost(new Uri(ApiBaseUrl).Host); }
                catch { return false; }
            }
        }

        public static bool IsLocalHost(string host)
        {
            string h = (host ?? string.Empty).ToLowerInvariant();
            return h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "[::1]";
        }

        // Why `url` may not be used as the server address, or null when it may.
        // Plain http:// is allowed only for a server on this PC: anywhere else
        // the password, the sign-in token and every fingerprint template would
        // cross the network unencrypted. Asked by the loader, the Settings
        // window and ApiClient (which sends nothing to such an address).
        public static string InsecureAddressProblem(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return null;
            if (parsed.Scheme != Uri.UriSchemeHttp || IsLocalHost(parsed.Host)) return null;
            return "The server address must start with https://. Plain http:// is allowed only for a server "
                + "on this PC, because passwords and fingerprints would cross the network unprotected.";
        }

        // Why nobody may sign in on this configuration, or null.
        public string SignInBlocker
        {
            get
            {
                string insecure = InsecureAddressProblem(ApiBaseUrl);
                return insecure != null ? insecure + " Fix it under Settings." : null;
            }
        }

        // Why `url` may not be used as the POS screen's address, or null when it
        // may: a full http(s) address, https unless the website runs on this PC.
        public static string PosUrlProblem(string url)
        {
            Uri parsed;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || parsed.Host.Length == 0)
                return "The POS screen address must be a full web address, e.g. " + LivePosUrl;
            if (parsed.Scheme == Uri.UriSchemeHttp && !IsLocalHost(parsed.Host))
                return "The POS screen address must start with https:// (plain http:// only for a website on this PC).";
            return null;
        }

        public static string NormalizeBaseUrl(string url)
        {
            string value = (url ?? string.Empty).Trim();
            if (value.Length > 0 && !value.EndsWith("/")) value += "/";
            return value;
        }

        public string ServerLabel
        {
            get
            {
                try { return new Uri(ApiBaseUrl).Host; }
                catch { return ApiBaseUrl; }
            }
        }

        // Writes the settings, keeping any other keys already in the file.
        // Returns null on success, otherwise why it could not be written.
        public static string Save(string apiBaseUrl, string deviceName, string posUrl, bool soundOn, bool voiceOn)
        {
            string path = FilePath;
            try
            {
                var serializer = new JavaScriptSerializer();
                var data = new Dictionary<string, object>();
                if (File.Exists(path))
                {
                    try { data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) ?? data; }
                    catch { }
                }

                data["apiBaseUrl"] = apiBaseUrl ?? string.Empty;
                data["deviceName"] = deviceName ?? string.Empty;
                data["posUrl"] = posUrl ?? LivePosUrl;
                data["sound"] = soundOn;
                data["voice"] = voiceOn;

                // One key per line, like the shipped file, so it stays easy to edit.
                var json = new StringBuilder();
                json.Append("{").Append(Environment.NewLine);
                int written = 0;
                foreach (KeyValuePair<string, object> entry in data)
                {
                    json.Append("    ").Append(serializer.Serialize(entry.Key))
                        .Append(": ").Append(serializer.Serialize(entry.Value));
                    if (++written < data.Count) json.Append(",");
                    json.Append(Environment.NewLine);
                }
                json.Append("}").Append(Environment.NewLine);

                File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows would not let this app write " + path
                    + ". Run the installer again, or edit that file as an administrator.";
            }
            catch (Exception ex)
            {
                return "Could not save " + path + ": " + ex.Message;
            }
        }

        public static AppConfig Load()
        {
            var config = new AppConfig();
            config.ApiBaseUrl = LiveServer;
            config.DeviceName = SafeMachineName();

            string path = FilePath;
            config.ConfigPath = path;
            string fallbackNote = "Using " + LiveServer + " until it is fixed.";

            if (!File.Exists(path))
            {
                config.LoadError = "Settings file not found: " + path + Environment.NewLine + fallbackNote;
                return config;
            }

            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (data == null) throw new FormatException("the file does not contain a JSON object");

                string url = Text(data, "apiBaseUrl");
                if (!string.IsNullOrEmpty(url)) config.ApiBaseUrl = url;
                string device = Text(data, "deviceName");
                if (!string.IsNullOrEmpty(device)) config.DeviceName = device;
                // Older configs have no posUrl, and a bad one must not stop the
                // Sales button: both fall back to the live POS screen.
                string pos = Text(data, "posUrl");
                if (!string.IsNullOrEmpty(pos) && PosUrlProblem(pos) == null) config.PosUrl = pos;
                else if (!string.IsNullOrEmpty(pos)) Log.Info("posUrl in the settings is not usable, using " + LivePosUrl + ": " + pos);
                config.SoundOn = Flag(data, "sound", true);
                config.VoiceOn = Flag(data, "voice", true);
                config.ReaderLowPriority = string.Equals(Text(data, "readerPriority"), "low", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                config.ApiBaseUrl = LiveServer;
                config.LoadError = "Settings file could not be read: " + path + Environment.NewLine
                    + "(" + ex.Message + ")" + Environment.NewLine + fallbackNote;
                return config;
            }

            config.ApiBaseUrl = NormalizeBaseUrl(config.ApiBaseUrl);

            Uri parsed;
            if (!Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                config.LoadError = "apiBaseUrl in " + path + " is not a valid web address: \"" + config.ApiBaseUrl + "\"";
            }
            else if (InsecureAddressProblem(config.ApiBaseUrl) != null)
            {
                config.LoadError = config.SignInBlocker;
            }
            return config;
        }

        private static string Text(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value).Trim();
        }

        private static bool Flag(Dictionary<string, object> data, string key, bool otherwise)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return otherwise;
            if (value is bool) return (bool)value;
            string s = Convert.ToString(value).Trim().ToLowerInvariant();
            if (s == "false" || s == "0" || s == "off" || s == "no") return false;
            if (s == "true" || s == "1" || s == "on" || s == "yes") return true;
            return otherwise;
        }
    }
}
