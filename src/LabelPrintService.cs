using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Printing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DollarCoAttendance
{
    // Sticker printing for the web admin. Chrome's print dialog gets the label
    // printer (Xprinter XP-246B, 48 mm, 203 dpi) wrong -- paper size, blank
    // labels -- so the website builds TSPL on the server and hands it to this
    // tiny HTTP service, which sends the bytes RAW to the printer's spooler.
    //
    //   http://127.0.0.1:47321/status   GET   app, version, chosen printer, installed printers
    //   http://127.0.0.1:47321/print    POST  {"data":"<TSPL>","printer":"<optional>"}
    //
    // Loopback only, on a TcpListener (HttpListener would need a URL ACL, i.e.
    // admin rights). Browsers may call it only from the POS website (CORS +
    // Chrome's Private Network Access); a request with no Origin (curl) is fine.
    // Never throws into the app: a busy port or a bad request is logged and the
    // counter carries on.
    public static class LabelPrintService
    {
        public const int Port = 47321;
        private const int MaxBodyBytes = 2 * 1024 * 1024;
        private const int MaxHeaderBytes = 64 * 1024;
        private const int SocketTimeoutMs = 10000;

        private static readonly string[] AllowedOrigins =
        {
            "https://pos.dollarandco.org",
            "https://pos-one-dollar-store-frontend.vercel.app",
            "http://localhost:3000",
            "http://localhost:3001",
        };

        private static readonly object printGate = new object();
        private static TcpListener listener;
        private static Thread thread;
        private static volatile bool running;
        private static string configuredPrinter;

        // `labelPrinter` is the config's choice (null / empty = automatic).
        public static void Start(string labelPrinter)
        {
            try
            {
                if (running) return;
                configuredPrinter = string.IsNullOrEmpty(labelPrinter) ? null : labelPrinter.Trim();
                var l = new TcpListener(IPAddress.Loopback, Port);
                l.ExclusiveAddressUse = true;
                l.Start();
                NotInherited(l.Server);
                listener = l;
                running = true;
                thread = new Thread(AcceptLoop);
                thread.IsBackground = true;
                thread.Name = "LabelPrintService";
                thread.Start();
                Log.Info("Label print service listening on http://127.0.0.1:" + Port + "/"
                    + (configuredPrinter != null ? " (label printer \"" + configuredPrinter + "\")" : " (label printer: automatic)"));
            }
            catch (SocketException ex)
            {
                running = false;
                Log.Error("Label print service not started: port " + Port + " is busy or blocked (" + ex.Message + ")");
            }
            catch (Exception ex)
            {
                running = false;
                Log.Error("Label print service not started", ex);
            }
        }

        public static void Stop()
        {
            try
            {
                running = false;
                TcpListener l = listener;
                listener = null;
                if (l != null) l.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Label print service did not stop cleanly", ex);
            }
        }

        private static void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    TcpListener l = listener;
                    if (l == null) break;
                    client = l.AcceptTcpClient();
                    NotInherited(client.Client);
                }
                catch (Exception ex)
                {
                    if (running) Log.Error("Label print service: accept failed", ex);
                    if (!running) break;
                    Thread.Sleep(200);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
            }
        }

        // .NET Framework sockets are inheritable, and the Sales button starts Edge
        // with UseShellExecute = false: Edge would keep the port open after this
        // app exits, and the next start (e.g. the Settings restart) could not
        // listen. Best effort.
        private static void NotInherited(Socket socket)
        {
            try { SetHandleInformation(socket.Handle, HandleFlagInherit, 0); }
            catch (Exception ex) { Log.Error("Label print service: socket left inheritable", ex); }
        }

        private const int HandleFlagInherit = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

        // ---------------------------------------------------------------
        // HTTP
        // ---------------------------------------------------------------

        private class Request
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];

            public string Header(string name)
            {
                string v;
                return Headers.TryGetValue(name, out v) ? v : null;
            }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = SocketTimeoutMs;
                    client.SendTimeout = SocketTimeoutMs;
                    NetworkStream stream = client.GetStream();
                    Request req;
                    string problem = ReadRequest(stream, out req);
                    if (problem != null)
                    {
                        WriteJson(stream, problem == "too large" ? 413 : 400, null, Error(problem));
                        return;
                    }

                    string origin = req.Header("Origin");
                    string allowed = null;
                    if (!string.IsNullOrEmpty(origin))
                    {
                        foreach (string o in AllowedOrigins)
                            if (string.Equals(o, origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) allowed = o;
                        if (allowed == null)
                        {
                            Log.Info("Label print service: refused a request from " + origin);
                            WriteJson(stream, 403, null, Error("Origin not allowed"));
                            return;
                        }
                    }

                    if (req.Method == "OPTIONS")
                    {
                        WriteResponse(stream, 204, allowed, null, null);
                        return;
                    }

                    string path = req.Path;
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);
                    path = path.TrimEnd('/').ToLowerInvariant();

                    if (path == "/status" && req.Method == "GET") WriteJson(stream, 200, allowed, Status());
                    else if (path == "/print" && req.Method == "POST")
                    {
                        int code;
                        Dictionary<string, object> result = Print(req.Body, out code);
                        WriteJson(stream, code, allowed, result);
                    }
                    else if (path == "/status" || path == "/print") WriteJson(stream, 405, allowed, Error("Method not allowed"));
                    else WriteJson(stream, 404, allowed, Error("Not found"));
                }
            }
            catch (IOException)
            {
                // The browser hung up early: nothing to answer.
            }
            catch (Exception ex)
            {
                Log.Error("Label print service: request failed", ex);
            }
        }

        // Null when `req` was read, otherwise why not.
        private static string ReadRequest(Stream stream, out Request req)
        {
            req = null;
            var head = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) return "incomplete request";
                head.WriteByte((byte)b);
                if (head.Length > MaxHeaderBytes) return "too large";
                if ((matched == 0 || matched == 2) && b == '\r') matched++;
                else if ((matched == 1 || matched == 3) && b == '\n') matched++;
                else matched = b == '\r' ? 1 : 0;
            }

            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return "bad request line";
            var r = new Request();
            r.Method = first[0].ToUpperInvariant();
            r.Path = first[1];
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                r.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            string lengthText = r.Header("Content-Length");
            if (!string.IsNullOrEmpty(lengthText))
            {
                long length;
                if (!long.TryParse(lengthText, out length) || length < 0) return "bad Content-Length";
                if (length > MaxBodyBytes) return "too large";
                var body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(body, read, (int)length - read);
                    if (n <= 0) return "incomplete body";
                    read += n;
                }
                r.Body = body;
            }
            req = r;
            return null;
        }

        private static void WriteJson(Stream stream, int status, string origin, Dictionary<string, object> body)
        {
            WriteResponse(stream, status, origin, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body)));
        }

        private static void WriteResponse(Stream stream, int status, string origin, string contentType, byte[] body)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            if (origin != null)
            {
                head.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
                head.Append("Vary: Origin\r\n");
                head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                head.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                head.Append("Access-Control-Allow-Private-Network: true\r\n");
                head.Append("Access-Control-Max-Age: 600\r\n");
            }
            if (contentType != null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Content-Length: ").Append(body != null ? body.Length : 0).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] h = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(h, 0, h.Length);
            if (body != null && body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 413: return "Payload Too Large";
                default: return status >= 500 ? "Internal Server Error" : "Error";
            }
        }

        private static Dictionary<string, object> Error(string message)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message;
            return d;
        }

        // ---------------------------------------------------------------
        // Endpoints
        // ---------------------------------------------------------------

        private static Dictionary<string, object> Status()
        {
            List<string> printers = InstalledPrinters();
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            d["app"] = AppInfo.Name;
            d["version"] = AppInfo.Version;
            d["printer"] = ChoosePrinter(null, printers);
            d["printers"] = printers;
            return d;
        }

        private static Dictionary<string, object> Print(byte[] body, out int status)
        {
            status = 500;
            Dictionary<string, object> json;
            try
            {
                json = new JavaScriptSerializer { MaxJsonLength = MaxBodyBytes * 2 }
                    .Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
            }
            catch (Exception)
            {
                json = null;
            }
            object dataValue = null;
            if (json == null || !json.TryGetValue("data", out dataValue) || !(dataValue is string) || ((string)dataValue).Length == 0)
            {
                status = 400;
                return Error("Send JSON {\"data\": \"<TSPL text>\"}");
            }
            object printerValue;
            string wanted = json.TryGetValue("printer", out printerValue) && printerValue is string ? (string)printerValue : null;

            string printer = ChoosePrinter(wanted, InstalledPrinters());
            if (printer == null) return Error("No label printer found. Install the Xprinter driver.");

            byte[] bytes = Encoding.GetEncoding(1252).GetBytes((string)dataValue);
            string error;
            lock (printGate) error = SendRaw(printer, bytes);
            if (error != null)
            {
                Log.Error("Label print to \"" + printer + "\" failed: " + error);
                return Error(error);
            }
            Log.Info("Label print: " + bytes.Length + " bytes to \"" + printer + "\"");
            status = 200;
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            d["printer"] = printer;
            d["bytes"] = bytes.Length;
            return d;
        }

        private static List<string> InstalledPrinters()
        {
            var list = new List<string>();
            try
            {
                foreach (string name in PrinterSettings.InstalledPrinters) list.Add(name);
            }
            catch (Exception ex)
            {
                Log.Error("Could not list the installed printers", ex);
            }
            return list;
        }

        // The request's printer if installed, else the configured one, else the
        // first that looks like a label printer; null when there is none.
        private static string ChoosePrinter(string wanted, List<string> installed)
        {
            string match = FindInstalled(wanted, installed);
            if (match != null) return match;
            match = FindInstalled(configuredPrinter, installed);
            if (match != null) return match;
            foreach (string name in installed)
            {
                string n = name.ToUpperInvariant();
                if (n.Contains("XPRINTER") || n.Contains("XP-") || n.Contains("LABEL")) return name;
            }
            return null;
        }

        private static string FindInstalled(string name, List<string> installed)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (string p in installed)
                if (string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // ---------------------------------------------------------------
        // RAW printing through the spooler (winspool.drv)
        // ---------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class DOC_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 pDocInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        // Null on success, otherwise what failed with Windows' own reason.
        private static string SendRaw(string printer, byte[] bytes)
        {
            IntPtr handle;
            if (!OpenPrinter(printer, out handle, IntPtr.Zero)) return Win32Problem("OpenPrinter");
            IntPtr buffer = IntPtr.Zero;
            try
            {
                var doc = new DOC_INFO_1 { pDocName = "DollarCoPos labels", pDatatype = "RAW" };
                if (StartDocPrinter(handle, 1, doc) == 0) return Win32Problem("StartDocPrinter");
                try
                {
                    if (!StartPagePrinter(handle)) return Win32Problem("StartPagePrinter");
                    try
                    {
                        buffer = Marshal.AllocHGlobal(bytes.Length);
                        Marshal.Copy(bytes, 0, buffer, bytes.Length);
                        int sent = 0;
                        while (sent < bytes.Length)
                        {
                            int written;
                            if (!WritePrinter(handle, IntPtr.Add(buffer, sent), bytes.Length - sent, out written))
                                return Win32Problem("WritePrinter");
                            if (written <= 0) return "WritePrinter wrote nothing (" + sent + " of " + bytes.Length + " bytes sent)";
                            sent += written;
                        }
                    }
                    finally
                    {
                        EndPagePrinter(handle);
                    }
                }
                finally
                {
                    EndDocPrinter(handle);
                }
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                ClosePrinter(handle);
            }
        }

        private static string Win32Problem(string call)
        {
            int code = Marshal.GetLastWin32Error();
            return call + " failed: " + new Win32Exception(code).Message + " (error " + code + ")";
        }
    }
}
