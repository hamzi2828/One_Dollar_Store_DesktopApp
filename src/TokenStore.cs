using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DollarCoAttendance
{
    public class StoredToken
    {
        public string Token;
        public DateTime ExpiresAtUtc;
        public DateTime SavedAtUtc;

        // Renewed once it is a day old (MainForm), so a counter used every day
        // never reaches the 7-day expiry.
        public bool DueForRenewal
        {
            get { return DateTime.UtcNow - SavedAtUtc > TimeSpan.FromHours(20); }
        }
    }

    // The sign-in token, saved encrypted with Windows DPAPI for the Windows
    // account running the app (CurrentUser), so the counter stays signed in
    // across restarts. One file per server (token-<hash>.dat), so a test build
    // and the shop's build never sign each other out.
    //
    // The server stays the authority on expiry: this only avoids sending a
    // token that is already known to be stale.
    public static class TokenStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DollarCo.Attendance.Token.v1");
        private static string fileName = "token.dat";
        private static readonly object gate = new object();

        private static string FilePath
        {
            get
            {
                string dir = AppInfo.DataFolder;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, fileName);
            }
        }

        public static void UseScope(string apiBaseUrl)
        {
            fileName = "token-" + ScopeHash(apiBaseUrl) + ".dat";
        }

        // Six bytes of SHA-256 of the normalised server address.
        public static string ScopeHash(string apiBaseUrl)
        {
            string key = AppConfig.NormalizeBaseUrl(apiBaseUrl).ToLowerInvariant();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var hex = new StringBuilder();
                for (int i = 0; i < 6; i++) hex.Append(digest[i].ToString("x2"));
                return hex.ToString();
            }
        }

        public static void Save(string token, DateTime expiresAtUtc)
        {
            lock (gate)
            {
                try
                {
                    var payload = new Dictionary<string, object>();
                    payload["token"] = token;
                    payload["expiresAt"] = ShopTime.ToIso(expiresAtUtc);
                    payload["savedAt"] = ShopTime.ToIso(DateTime.UtcNow);
                    byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(payload));
                    File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
                }
                catch (Exception ex)
                {
                    // Only means signing in again next time.
                    Log.Error("The sign-in could not be saved on this PC", ex);
                }
            }
        }

        public static StoredToken Load()
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return null;
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(plain));
                    if (data == null) return null;

                    var stored = new StoredToken();
                    stored.Token = Json.Str(data, "token");
                    DateTime? expires = ShopTime.ParseUtc(Json.Str(data, "expiresAt"));
                    DateTime? saved = ShopTime.ParseUtc(Json.Str(data, "savedAt"));
                    if (string.IsNullOrEmpty(stored.Token) || !expires.HasValue) return null;
                    stored.ExpiresAtUtc = expires.Value;
                    stored.SavedAtUtc = saved ?? DateTime.UtcNow.AddDays(-7);
                    if (stored.ExpiresAtUtc <= DateTime.UtcNow) return null;
                    return stored;
                }
                catch
                {
                    // Corrupt, or written by another Windows account: sign in again.
                    return null;
                }
            }
        }

        public static void Clear()
        {
            lock (gate)
            {
                try { if (File.Exists(FilePath)) File.Delete(FilePath); }
                catch { }
            }
        }
    }
}
