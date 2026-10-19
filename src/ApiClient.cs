using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace DollarCoAttendance
{
    // Small JSON client for the POS server (pos_backend): POST /user/login and
    // /api/desktop/*. Callers never deal with HTTP exceptions: every call comes
    // back as one dictionary — the server's JSON body plus
    //
    //   http_status    the HTTP status (0 when the server was not reached)
    //   network_error  true when the server could not be reached at all
    //
    // so "the server said no" and "there is no internet" are told apart with
    // Json.HttpStatus / Json.IsNetworkError rather than with try/catch.
    public class ApiClient
    {
        public const int DefaultTimeoutMs = 30000;

        private readonly string baseUrl;
        private readonly string refusedBecause;

        // The bearer token (set after sign-in). volatile: read on worker threads.
        private volatile string token;
        public string Token
        {
            get { return token; }
            set { token = value; }
        }

        // False from the first call that could not reach the server (or that a
        // gateway answered with 502/503/504 for it) until the next real answer.
        private volatile bool unreachable;
        public bool ServerReachable { get { return !unreachable; } }

        // Raised when ServerReachable changes, on whichever thread made the
        // call. A window that listens must marshal itself.
        public event EventHandler ReachabilityChanged;

        static ApiClient()
        {
            // .NET Framework does not offer TLS 1.2 by default; modern hosts
            // (Vercel) need it. 3072 = Tls12, cast because older reference
            // assemblies do not name it.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch (NotSupportedException) { }
            // No "Expect: 100-continue" handshake: one round trip less per punch.
            ServicePointManager.Expect100Continue = false;
        }

        public ApiClient(string baseUrl)
        {
            this.baseUrl = AppConfig.NormalizeBaseUrl(baseUrl);
            refusedBecause = AppConfig.InsecureAddressProblem(this.baseUrl);
        }

        public string BaseUrl { get { return baseUrl; } }

        // A copy pointed at the same server with another account's token (the
        // manager who signs in to enrol fingerprints). It shares nothing else.
        public ApiClient WithToken(string otherToken)
        {
            var copy = new ApiClient(baseUrl);
            copy.Token = otherToken;
            return copy;
        }

        public Dictionary<string, object> Get(string path) { return Send("GET", path, null, true, DefaultTimeoutMs); }
        public Dictionary<string, object> Get(string path, int timeoutMs) { return Send("GET", path, null, true, timeoutMs); }
        public Dictionary<string, object> Post(string path, Dictionary<string, object> body) { return Send("POST", path, body, true, DefaultTimeoutMs); }
        public Dictionary<string, object> Post(string path, Dictionary<string, object> body, int timeoutMs) { return Send("POST", path, body, true, timeoutMs); }
        public Dictionary<string, object> Delete(string path) { return Send("DELETE", path, null, true, DefaultTimeoutMs); }

        // An absolute address for a server path such as "/uploads/x.jpg"
        // (employee photos); absolute addresses are returned as they are.
        public string Resolve(string pathOrUrl)
        {
            if (string.IsNullOrEmpty(pathOrUrl)) return null;
            Uri absolute;
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out absolute)
                && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                return absolute.ToString();
            return baseUrl + pathOrUrl.TrimStart('/');
        }

        public Dictionary<string, object> Send(string method, string path, Dictionary<string, object> body, bool authenticated, int timeoutMs)
        {
            if (refusedBecause != null)
            {
                Log.Error(method + " " + path + " not sent: http:// address for a server that is not on this PC");
                var refused = new Dictionary<string, object>();
                refused["success"] = false;
                refused["http_status"] = 0;
                refused["message"] = refusedBecause;
                return refused;
            }

            HttpWebRequest request;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(baseUrl + path.TrimStart('/'));
            }
            catch (Exception ex)
            {
                SetReachable(false);
                return NetworkError(method, path, "The server address in the settings is not valid: " + ex.Message);
            }

            request.Method = method;
            request.Accept = "application/json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = AppInfo.ClientId;
            request.KeepAlive = true;
            string bearer = token;
            if (authenticated && !string.IsNullOrEmpty(bearer)) request.Headers["Authorization"] = "Bearer " + bearer;

            byte[] payload = null;
            if (body != null)
            {
                try
                {
                    payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                }
                catch (Exception ex)
                {
                    return NetworkError(method, path, "Could not encode the request: " + ex.Message);
                }
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = payload.Length;
            }

            try
            {
                if (payload != null)
                {
                    using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return Answer(response, method, path);
                }
            }
            catch (WebException ex)
            {
                var errorResponse = ex.Response as HttpWebResponse;
                if (errorResponse == null)
                {
                    SetReachable(false);
                    return NetworkError(method, path, "Could not reach the server: " + ex.Message + " (" + ex.Status + ")");
                }
                using (errorResponse)
                {
                    Dictionary<string, object> parsed = Answer(errorResponse, method, path);
                    // The path, the status and the server's wording -- never the
                    // body, which may carry a password or a fingerprint template.
                    string code = Json.Code(parsed);
                    Log.Error(method + " " + path + " -> HTTP " + Json.HttpStatus(parsed)
                        + (code != null ? " " + code : string.Empty) + ": " + (Json.Str(parsed, "message") ?? "(no message)"));
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                SetReachable(false);
                return NetworkError(method, path, "Could not reach the server: " + ex.Message);
            }
        }

        private Dictionary<string, object> Answer(HttpWebResponse response, string method, string path)
        {
            int status = (int)response.StatusCode;
            // A gateway answering for a server that is down (Vercel, a proxy):
            // as good as no answer for deciding whether to work offline.
            bool gateway = status == 502 || status == 503 || status == 504;
            SetReachable(!gateway);
            if (!gateway) NoteServerClock(response);

            Dictionary<string, object> result = ReadJson(response, method, path);
            result["http_status"] = status;
            if (!result.ContainsKey("success")) result["success"] = status >= 200 && status < 300;
            return result;
        }

        // Every answer carries the server's clock in its Date header; ShopTime
        // uses it so a PC with a wrong clock still shows the right time.
        private static void NoteServerClock(HttpWebResponse response)
        {
            try
            {
                string date = response.Headers["Date"];
                DateTime serverTime;
                if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out serverTime))
                {
                    ShopTime.SetServerTime(DateTime.SpecifyKind(serverTime, DateTimeKind.Utc));
                }
            }
            catch
            {
                // Cosmetic.
            }
        }

        private void SetReachable(bool reachable)
        {
            if (unreachable == !reachable) return;
            unreachable = !reachable;
            Log.Info(reachable ? "The server is answering again" : "The server cannot be reached");
            EventHandler handler = ReachabilityChanged;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("Connection state could not be reported", ex); }
        }

        private static Dictionary<string, object> NetworkError(string method, string path, string message)
        {
            Log.Error(method + " " + path + " failed: " + message);
            var result = new Dictionary<string, object>();
            result["success"] = false;
            result["network_error"] = true;
            result["http_status"] = 0;
            result["message"] = message;
            return result;
        }

        private static Dictionary<string, object> ReadJson(HttpWebResponse response, string method, string path)
        {
            string json;
            using (Stream stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                json = reader.ReadToEnd();

            var serializer = new JavaScriptSerializer();
            // The template list is one JSON body; the default 2 MB cap is too small.
            serializer.MaxJsonLength = 64 * 1024 * 1024;
            try
            {
                object parsed = serializer.DeserializeObject(json);
                var asObject = parsed as Dictionary<string, object>;
                if (asObject != null) return asObject;
                var wrapped = new Dictionary<string, object>();
                wrapped["data"] = parsed;
                return wrapped;
            }
            catch
            {
                Log.Error(method + " " + path + " -> HTTP " + (int)response.StatusCode + ": non-JSON body (" + json.Length + " chars)");
                var fallback = new Dictionary<string, object>();
                fallback["success"] = false;
                fallback["message"] = "The server sent an unexpected response.";
                return fallback;
            }
        }
    }

    // Every answer is a loosely-typed dictionary in which any field can be
    // missing. These read one without a null check at each call site.
    public static class Json
    {
        public static string Str(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return null;
            string text = value is DateTime ? ShopTime.ToIso((DateTime)value) : Convert.ToString(value, CultureInfo.InvariantCulture);
            return text.Length == 0 ? null : text;
        }

        public static int Int(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static int? NullableInt(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        public static bool Bool(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            string s = Convert.ToString(value, CultureInfo.InvariantCulture).ToLowerInvariant();
            return s == "true" || s == "1";
        }

        public static DateTime? Time(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return null;
            if (value is DateTime) return ((DateTime)value).ToUniversalTime();
            return ShopTime.ParseUtc(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static List<Dictionary<string, object>> Rows(Dictionary<string, object> data, string key)
        {
            var rows = new List<Dictionary<string, object>>();
            object value;
            if (data == null || !data.TryGetValue(key, out value)) return rows;
            var list = value as IEnumerable;
            if (list == null || value is string) return rows;
            foreach (object entry in list)
            {
                var row = entry as Dictionary<string, object>;
                if (row != null) rows.Add(row);
            }
            return rows;
        }

        public static List<string> Strings(Dictionary<string, object> data, string key)
        {
            var values = new List<string>();
            object value;
            if (data == null || !data.TryGetValue(key, out value)) return values;
            var list = value as IEnumerable;
            if (list == null || value is string) return values;
            foreach (object entry in list)
            {
                if (entry == null) continue;
                string text = Convert.ToString(entry, CultureInfo.InvariantCulture);
                if (text.Length > 0) values.Add(text);
            }
            return values;
        }

        // The "data" object of a { success, data } answer.
        public static Dictionary<string, object> Data(Dictionary<string, object> result)
        {
            return Obj(result, "data");
        }

        public static int HttpStatus(Dictionary<string, object> result) { return Int(result, "http_status"); }

        public static string Code(Dictionary<string, object> result) { return Str(result, "code"); }

        public static string Message(Dictionary<string, object> result) { return Str(result, "message"); }

        public static bool IsNetworkError(Dictionary<string, object> result) { return Bool(result, "network_error"); }

        public static bool Ok(Dictionary<string, object> result)
        {
            int status = HttpStatus(result);
            return status >= 200 && status < 300;
        }

        // No answer worth trusting: no connection, or a gateway / server failure.
        // A punch in this state is saved on the PC and sent again later.
        public static bool IsOutage(Dictionary<string, object> result)
        {
            int status = HttpStatus(result);
            return IsNetworkError(result) || status == 0 || status >= 500;
        }

        // The server refused the sign-in itself (expired / disabled account / no permission).
        public static bool IsRejection(Dictionary<string, object> result)
        {
            int status = HttpStatus(result);
            return status == 401 || status == 403;
        }
    }
}
