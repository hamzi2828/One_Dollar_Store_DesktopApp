using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    internal static class Program
    {
        // One copy per Windows session: two would fight over the reader.
        // Settings restarts the app, so a new copy waits a moment for the old one.
        public const string SingleInstanceName = @"Local\DollarCoAttendance.SingleInstance";
        private const int InstanceWaitMs = 5000;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log.Error("Unhandled exception on the UI thread", e.Exception);
                ShowCrashMessage(e.Exception, false);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                Log.Error("Unhandled exception" + (e.IsTerminating ? " (the app has to close)" : string.Empty), ex);
                ShowCrashMessage(ex, e.IsTerminating);
            };

            // Developer checks, no window: DollarCoPos.exe /selftest
            // or /apitest <server> <email> <password> (see SelfTest.cs).
            if (args.Length > 0 && (args[0] == "/selftest" || args[0] == "/apitest")) return SelfTest.Run(args);
            if (args.Length > 1 && args[0] == "/render") return Preview.Run(args[1]);

            using (var instance = new Mutex(false, SingleInstanceName))
            {
                bool owned;
                try { owned = instance.WaitOne(InstanceWaitMs, false); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    Log.Info("Another copy is already running; this one exits");
                    MessageBox.Show(AppInfo.Name + " is already open on this PC." + Environment.NewLine
                        + "Look for it on the taskbar — only one copy can use the fingerprint reader.",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                try
                {
                    Log.Info("Starting " + AppInfo.ClientId + " (built " + AppInfo.BuildStamp + ") on "
                        + Environment.OSVersion + " as " + Environment.UserName + "@" + Environment.MachineName);
                    ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 10);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new AppController());
                    Log.Info("Exited");
                }
                finally
                {
                    try { instance.ReleaseMutex(); } catch { }
                }
            }
            return 0;
        }

        private static void ShowCrashMessage(Exception ex, bool terminating)
        {
            try
            {
                MessageBox.Show(AppInfo.Name + " hit an unexpected error"
                    + (terminating ? " and has to close. Start it again from the shortcut." : ". If it misbehaves, close it and start it again.")
                    + Environment.NewLine + Environment.NewLine
                    + (ex != null ? ex.GetType().Name + ": " + ex.Message : "(no detail)")
                    + Environment.NewLine + Environment.NewLine + "Details were written to:" + Environment.NewLine + Log.FilePath,
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }

    // Switches between the sign-in window and the counter window, keeping one
    // ApiClient (and its token) alive across both.
    internal class AppController : ApplicationContext
    {
        private const int StartupProbeMs = 15000;

        private readonly ApiClient api;
        private readonly AppConfig config;
        private LoginForm loginForm;
        private MainForm mainForm;

        private class Probe
        {
            public SessionUser User;
            public Dictionary<string, object> Result;
        }

        public AppController()
        {
            config = AppConfig.Load();
            if (config.LoadError != null) Log.Error("Config: " + config.LoadError.Replace(Environment.NewLine, " "));
            Log.Info("Server " + config.ApiBaseUrl + ", this PC is \"" + config.DeviceLabel + "\"");

            api = new ApiClient(config.ApiBaseUrl);
            TokenStore.UseScope(config.ApiBaseUrl);
            OfflineStore.UseScope(config.ApiBaseUrl);

            StoredToken saved = TokenStore.Load();
            if (saved == null || config.LoadError != null || config.SignInBlocker != null)
            {
                ShowLogin(null, false);
                return;
            }

            api.Token = saved.Token;
            Probe probe = ProbeSession(StartupProbeMs);
            if (probe.User != null)
            {
                ShowMain(probe.User, false);
                return;
            }
            // No connection: the counter opens on the account saved last time and works offline.
            if (Json.IsOutage(probe.Result) && TryOpenOffline()) return;
            ShowLogin(null, false);
            ReportProblem(probe);
        }

        private Probe ProbeSession(int timeoutMs)
        {
            var probe = new Probe();
            try
            {
                probe.Result = api.Get("api/desktop/session", timeoutMs);
                SessionUser user = Json.Ok(probe.Result) ? SessionUser.From(Json.Data(probe.Result)) : null;
                if (user != null && user.CanRunDesk)
                {
                    probe.User = user;
                    OfflineStore.SaveSession(user);
                }
                else if (user != null)
                {
                    // Signed in, but the role no longer has the desk permission.
                    probe.Result = new Dictionary<string, object>();
                    probe.Result["http_status"] = 403;
                    probe.Result["message"] = "This account is no longer allowed to run the attendance app";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Session check failed", ex);
                probe.Result = new Dictionary<string, object>();
                probe.Result["network_error"] = true;
                probe.Result["message"] = ex.Message;
            }
            return probe;
        }

        private bool TryOpenOffline()
        {
            SessionUser cached = OfflineStore.LoadSession();
            if (cached == null) return false;
            Log.Info("Server unreachable at start-up; opening offline as " + (cached.Email ?? "(unknown)"));
            ShowMain(cached, true);
            return true;
        }

        // The saved token could not be turned into a session. Only an explicit
        // refusal (401 / 403) forgets it; no connection keeps it for Retry.
        private void ReportProblem(Probe probe)
        {
            Dictionary<string, object> r = probe.Result;
            int status = Json.HttpStatus(r);
            string message;
            bool canRetry = true;
            if (Json.IsOutage(r))
            {
                message = "Can't reach the server (" + config.ServerLabel + "). The sign-in is kept: press Retry when the internet is back.";
            }
            else if (status == 401 || status == 403)
            {
                TokenStore.Clear();
                OfflineStore.ClearCache();
                api.Token = null;
                canRetry = false;
                message = status == 401 ? "The sign-in has expired. Please sign in again."
                    : (Json.Message(r) ?? "This account can't use the attendance app") + ". Sign in with a manager account.";
            }
            else
            {
                message = "The server could not confirm the saved sign-in (HTTP " + status + "). "
                    + (Json.Message(r) ?? string.Empty) + " Retry, or sign in below.";
            }
            Log.Info("Saved sign-in not resumed: " + message);
            if (loginForm != null) loginForm.ShowSessionProblem(message, canRetry && api.Token != null);
        }

        private void ShowLogin(string message, bool clearedToken)
        {
            StoredToken saved = TokenStore.Load();
            SessionUser last = OfflineStore.LoadSession();
            loginForm = new LoginForm(api, config, last != null ? last.Email : null);
            loginForm.LoggedIn += LoginForm_LoggedIn;
            loginForm.RetryRequested += LoginForm_RetryRequested;
            loginForm.FormClosed += delegate { if (mainForm == null) ExitThread(); };
            loginForm.Show();
            if (message != null) loginForm.ShowSessionProblem(message, !clearedToken && saved != null);
        }

        private void LoginForm_LoggedIn(object sender, LoginSuccessEventArgs e)
        {
            TokenStore.Save(e.Token, e.ExpiresAtUtc);
            api.Token = e.Token;
            OfflineStore.SaveSession(e.User);
            LoginForm closing = loginForm;
            loginForm = null;
            ShowMain(e.User, false);
            try { if (closing != null && !closing.IsDisposed) closing.Close(); } catch { }
        }

        private void LoginForm_RetryRequested(object sender, EventArgs e)
        {
            LoginForm form = loginForm;
            if (form == null || api.Token == null) return;
            form.SetBusy(true, "Checking the saved sign-in...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Probe probe = ProbeSession(ApiClient.DefaultTimeoutMs);
                try
                {
                    if (form.IsDisposed) return;
                    form.BeginInvoke(new Action(delegate
                    {
                        if (form.IsDisposed || loginForm != form) return;
                        form.SetBusy(false, null);
                        if (probe.User != null)
                        {
                            loginForm = null;
                            ShowMain(probe.User, false);
                            try { form.Close(); } catch { }
                        }
                        else ReportProblem(probe);
                    }));
                }
                catch (Exception ex)
                {
                    Log.Error("Retry result could not be shown", ex);
                }
            });
        }

        private void ShowMain(SessionUser user, bool offline)
        {
            mainForm = new MainForm(api, user, config, offline);
            mainForm.LoggedOut += MainForm_LoggedOut;
            mainForm.FormClosed += delegate
            {
                mainForm = null;
                if (loginForm == null || loginForm.IsDisposed) ExitThread();
            };
            mainForm.Show();
        }

        private void MainForm_LoggedOut(object sender, SessionEndedEventArgs e)
        {
            if (e.ClearToken)
            {
                TokenStore.Clear();
                OfflineStore.ClearCache();
                api.Token = null;
            }
            MainForm closing = mainForm;
            ShowLogin(e.Message, e.ClearToken);
            mainForm = null;
            try { if (closing != null && !closing.IsDisposed) closing.Close(); } catch { }
        }
    }
}
