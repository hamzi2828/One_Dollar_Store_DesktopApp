using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // Raised when the counter's sign-in is over: Sign out, or the server
    // refusing the token. ClearToken: forget the saved sign-in too.
    public class SessionEndedEventArgs : EventArgs
    {
        public bool ClearToken;
        public string Message;
    }

    // The counter window.
    //
    // LEFT is the reader: a pulsing fingerprint until someone puts a finger on
    // it, then that person's name and photo at once (matched on this PC), then
    // the server's answer, big: IN 9:58 am · on time / 12 min late, or OUT
    // 6:05 pm · worked 7 h 35 m. Back to waiting after 8 seconds.
    //
    // RIGHT is today's staff list (GET /api/desktop/board), reloaded after every
    // punch, every 3 minutes, when the connection comes back and at midnight
    // Pakistan time.
    //
    // Attendance is marked by FINGERPRINT ONLY. With no reader (or its software
    // missing) the whole window says so; there is no other way in.
    //
    // No internet: a recognised finger is saved on this PC (OfflineStore, with
    // its own clientId and time) and sent when the server answers again.
    //
    // Everything that talks to the server runs off the UI thread, so a slow or
    // absent server never freezes the window.
    public class MainForm : Form, IReaderEvents
    {
        private const int ResultHoldMs = 8000;
        private const int ErrorHoldMs = 5000;
        private const int BoardRefreshMs = 3 * 60 * 1000;
        private const int TemplateRefreshMs = 5 * 60 * 1000;
        private const int SessionCheckMs = 30 * 60 * 1000;
        private const int ConnectionRetryMs = 15000;
        private const int ReplayRetryMs = 30000;
        private const int ReaderGraceMs = 4000;
        private const int TouchGoneMs = 1500;
        private const int BreakArmSeconds = 15;

        [Flags]
        private enum LoadParts
        {
            None = 0,
            Session = 1,
            Staff = 2,
            Board = 4,
            Templates = 8,
            All = Session | Staff | Board | Templates,
        }

        private readonly ApiClient api;
        private readonly AppConfig config;
        private SessionUser session;
        private readonly TemplateStore templates = new TemplateStore();
        private volatile StaffDirectory directory = StaffDirectory.Empty;

        private BoardData board;
        private readonly object syncGate = new object();
        private readonly List<KeyValuePair<string, BoardRow>> syncProblems = new List<KeyValuePair<string, BoardRow>>(); // day key -> row
        private string boardDay;

        // ---- reader ----
        private FingerReader capturer;
        private ReaderBrand readerBrand = ReaderBrand.DigitalPersona;
        private FingerEngine engine = DigitalPersonaEngine.Instance;
        private bool engineAvailable;
        private string engineError;
        private volatile string readerDriverError;   // why the reader's driver cannot be used, or null
        private volatile bool readerConnected;
        private volatile bool readerChecked;
        private volatile bool touchPending;

        // ---- state ----
        private volatile bool busy;            // a scan is being processed
        // Owner, 2026-11-04: registering a finger is not clocking in. People just
        // enrolled are not punched for a while, and stray scans right after the
        // Enroll window closes are dropped.
        public const int JustEnrolledQuietSeconds = 120;
        private readonly Dictionary<string, DateTime> justEnrolledUntilUtc = new Dictionary<string, DateTime>();
        private DateTime scansQuietUntilUtc = DateTime.MinValue;
        private volatile bool showingDetail;   // a result is on the left panel
        private volatile bool sessionEnded;
        private volatile bool sessionConfirmed;
        private volatile bool templatesLoading;
        private volatile string templateError;
        private volatile bool forceFullTemplates;
        private bool serverDown;
        private bool blocked;
        private int loading;
        private LoadParts pendingParts;
        private readonly object pendingGate = new object();
        private int replaying;
        private bool skewNoted;

        // ---- break ----
        // The Break button arms the next matched scan as action=break, for
        // BreakArmSeconds or until that scan (pressed again = cancel).
        private volatile bool breakArmed;
        private DateTime breakDeadlineUtc;

        // ---- UI ----
        private HeaderBand header;
        private Label clockLabel;
        private Label dateLabel;
        private StatusDot readerDot;
        private Label readerLabel;
        private Label connectionLabel;
        private ReaderPanel reader;
        private BoardPanel boardPanel;
        private BlockedPanel blockedPanel;
        private RoundButton enrollButton;
        private RoundButton salesButton;
        private bool salesShown = true;   // features.sales (Control.Visible reads false until the form shows)
        private bool enrollShown = true;  // features.enroll
        private bool settingsShown = true; // features.settings
        private bool salesOpening;        // a sign-in code is being fetched for the POS screen
        private RoundButton refreshButton;
        private RoundButton settingsButton;
        private RoundButton pcNameButton;   // everyone signed in: renames this PC
        private RoundButton signOutButton;

        // WinForms timers (System.Threading has its own Timer).
        private System.Windows.Forms.Timer detailTimer, boardTimer, templateTimer, clockTimer, connectionTimer,
            replayTimer, sessionTimer, readerGraceTimer, touchTimer, breakTimer;

        public event EventHandler<SessionEndedEventArgs> LoggedOut;

        public MainForm(ApiClient api, SessionUser session, AppConfig config, bool startedOffline)
        {
            this.api = api;
            this.session = session;
            this.config = config;
            sessionConfirmed = !startedOffline;
            BuildUi();
            Sounds.Initialize(this, config.SoundOn, config.VoiceOn);
            api.ReachabilityChanged += Api_ReachabilityChanged;
            Shown += delegate { if (!PreviewOnly) StartUp(); };
        }

        // Developer preview (Preview.cs): no reader, no server, no timers.
        internal bool PreviewOnly;

        private static int P(int v) { return Theme.Px(v); }

        // ------------------------------------------------------------ layout

        private void BuildUi()
        {
            Theme.PrepareForDpi(this);
            SuspendLayout();
            Text = AppInfo.Name;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(P(1180), area.Width - 40), Math.Min(P(720), area.Height - 60));
            MinimumSize = new Size(Math.Min(P(960), area.Width), Math.Min(P(620), area.Height));
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            header = new HeaderBand("Attendance", string.Empty, true);
            clockLabel = Theme.Label(string.Empty, Theme.FontClock, Theme.TextPrimary);
            clockLabel.TextAlign = ContentAlignment.MiddleRight;
            dateLabel = Theme.Label(string.Empty, Theme.FontSmall, Theme.TextSecondary);
            dateLabel.TextAlign = ContentAlignment.MiddleRight;
            readerDot = new StatusDot();
            readerLabel = Theme.Label("READER", Theme.FontLabel, Theme.TextSecondary);
            readerLabel.TextAlign = ContentAlignment.MiddleLeft;
            connectionLabel = Theme.Label("ONLINE", Theme.FontPill, Theme.OnBrand);
            connectionLabel.BackColor = Theme.Warning;
            connectionLabel.TextAlign = ContentAlignment.MiddleCenter;
            connectionLabel.Visible = false;
            header.Controls.Add(clockLabel);
            header.Controls.Add(dateLabel);
            header.Controls.Add(readerDot);
            header.Controls.Add(readerLabel);
            header.Controls.Add(connectionLabel);

            reader = new ReaderPanel();
            reader.BreakClicked += Break_Click;
            boardPanel = new BoardPanel();
            blockedPanel = new BlockedPanel();
            blockedPanel.Visible = false;

            enrollButton = Theme.QuietButton("Enroll fingerprints", Theme.Brand);
            enrollButton.Click += Enroll_Click;
            // The POS screen in its own Edge window (PosLauncher). Works with no
            // reader and no internet: it asks the server nothing first.
            salesButton = Theme.QuietButton("Sales", Theme.BrandGreen);
            salesButton.Click += Sales_Click;
            refreshButton = Theme.QuietButton("Refresh", Theme.TextSecondary);
            refreshButton.Click += delegate
            {
                forceFullTemplates = true;
                BeginLoad(LoadParts.All, "Refreshing...");
            };
            settingsButton = Theme.QuietButton("Settings", Theme.TextSecondary);
            settingsButton.Click += delegate { if (settingsShown) SettingsForm.Edit(this, config); };
            pcNameButton = Theme.QuietButton("PC name", Theme.TextSecondary);
            pcNameButton.Click += delegate
            {
                if (PcNameForm.Edit(this, config)) ApplySession();
            };
            signOutButton = Theme.QuietButton("Sign out", Theme.Danger);
            signOutButton.Click += SignOut_Click;

            Controls.Add(header);
            Controls.Add(blockedPanel);
            Controls.Add(reader);
            Controls.Add(boardPanel);
            Controls.Add(enrollButton);
            Controls.Add(salesButton);
            Controls.Add(refreshButton);
            Controls.Add(settingsButton);
            Controls.Add(pcNameButton);
            Controls.Add(signOutButton);

            detailTimer = NewTimer(ResultHoldMs, delegate { detailTimer.Stop(); ReturnToWaiting(); });
            boardTimer = NewTimer(BoardRefreshMs, delegate { BeginLoad(LoadParts.Board, null); });
            templateTimer = NewTimer(TemplateRefreshMs, delegate { BeginLoad(LoadParts.Templates | LoadParts.Staff, null); });
            clockTimer = NewTimer(1000, delegate { Tick(); });
            connectionTimer = NewTimer(ConnectionRetryMs, delegate { BeginLoad(LoadParts.Board, null); });
            replayTimer = NewTimer(ReplayRetryMs, delegate { StartReplay(); });
            sessionTimer = NewTimer(SessionCheckMs, delegate { BeginLoad(LoadParts.Session, null); });
            readerGraceTimer = NewTimer(ReaderGraceMs, delegate
            {
                readerGraceTimer.Stop();
                readerChecked = true;
                if (!readerConnected) Log.Info("No fingerprint reader found");
                ShowReaderState();
            });
            touchTimer = NewTimer(TouchGoneMs, delegate
            {
                touchTimer.Stop();
                if (!touchPending) return;
                touchPending = false;
                if (showingDetail) ReturnToWaiting();
                else ShowReaderState();
            });

            breakTimer = NewTimer(500, delegate { BreakTick(); });

            FormClosing += MainForm_FormClosing;
            ApplySession();
            ResumeLayout(false);
            Relayout();
            Tick();
        }

        private System.Windows.Forms.Timer NewTimer(int interval, EventHandler tick)
        {
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = interval;
            timer.Tick += tick;
            return timer;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private void Relayout()
        {
            if (header == null) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            int margin = P(18);
            int headerHeight = P(84);
            header.SetBounds(0, 0, w, headerHeight);
            clockLabel.SetBounds(w - P(24) - P(220), P(10), P(220), P(36));
            dateLabel.SetBounds(w - P(24) - P(220), P(46), P(220), P(20));
            int statusX = clockLabel.Left - P(320);
            readerDot.SetBounds(statusX, P(20), P(14), P(14));
            readerLabel.SetBounds(statusX + P(22), P(16), P(230), P(22));
            connectionLabel.SetBounds(statusX, P(46), P(300), P(22));

            int buttonHeight = P(46);
            int buttonsTop = h - margin - buttonHeight;
            int contentTop = headerHeight + margin;
            int contentHeight = buttonsTop - margin - contentTop;
            int readerWidth = (w - margin * 3) * 44 / 100;
            reader.SetBounds(margin, contentTop, readerWidth, contentHeight);
            boardPanel.SetBounds(margin * 2 + readerWidth, contentTop, w - margin * 3 - readerWidth, contentHeight);
            blockedPanel.SetBounds(margin, contentTop, w - margin * 2, contentHeight);

            int bw = P(210);
            int next = margin;
            if (enrollShown)
            {
                enrollButton.SetBounds(next, buttonsTop, bw, buttonHeight);
                next = enrollButton.Right + P(12);
            }
            if (salesShown)
            {
                salesButton.SetBounds(next, buttonsTop, P(150), buttonHeight);
                next = salesButton.Right + P(12);
            }
            refreshButton.SetBounds(next, buttonsTop, P(150), buttonHeight);
            signOutButton.SetBounds(w - margin - P(150), buttonsTop, P(150), buttonHeight);
            int right = signOutButton.Left;
            if (settingsShown)
            {
                settingsButton.SetBounds(right - P(12) - P(150), buttonsTop, P(150), buttonHeight);
                right = settingsButton.Left;
            }
            pcNameButton.SetBounds(right - P(12) - P(150), buttonsTop, P(150), buttonHeight);
            header.Invalidate();
        }

        private void ApplySession()
        {
            header.Subtitle = (session != null ? session.HeaderLine() : string.Empty) + "   ·   " + config.DeviceLabel;
            enrollButton.Enabled = !blocked;
            bool sales = session == null || session.SalesEnabled;
            bool enroll = session == null || session.EnrollEnabled;
            bool settings = session == null || session.SettingsEnabled;
            if (salesShown != sales || enrollShown != enroll || settingsShown != settings)
            {
                salesShown = sales;
                salesButton.Visible = sales;
                enrollShown = enroll;
                enrollButton.Visible = enroll;
                settingsShown = settings;
                settingsButton.Visible = settings;
                Relayout();
            }
        }

        // The header clock (shop time, from the server's clock) and the
        // midnight roll-over of the board.
        private void Tick()
        {
            DateTime now = ShopTime.NowUtc;
            clockLabel.Text = ShopTime.Clock(now);
            dateLabel.Text = ShopTime.ToShop(now).ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture);
            string today = ShopTime.DayKey(now);
            if (boardDay != null && boardDay != today && !sessionEnded)
            {
                Log.Info("New shop day " + today + ": reloading the board");
                boardDay = today;
                lock (syncGate) syncProblems.RemoveAll(p => p.Key != today);
                BeginLoad(LoadParts.Board | LoadParts.Staff, null);
            }
        }

        // ------------------------------------------------------------ start-up

        private void StartUp()
        {
            boardDay = ShopTime.TodayKey;
            reader.ShowIdle(PulseState.Off, "Starting...", "Looking for the fingerprint reader.", Theme.TextSecondary);

            readerBrand = ReaderSetup.Choose(config);
            engine = FingerEngine.For(readerBrand);
            Log.Info("Fingerprint reader: " + ReaderSetup.Label(readerBrand) + (config.Reader != "auto" ? " (set in the settings file)" : string.Empty));
            engineError = engine.SelfTest();
            engineAvailable = engineError == null;
            if (!engineAvailable)
            {
                Log.Error("Fingerprint engine unavailable: " + engineError);
                readerChecked = true;
            }
            else
            {
                StartReader();
                readerGraceTimer.Start();
            }

            // This PC's copies first, so the reader matches at once -- and still
            // does if the server never answers.
            templates.LoadCached();
            StaffDirectory cached = OfflineStore.LoadStaff();
            if (cached != null) directory = cached;
            ShowReaderState();
            ShowBoard();

            KeepAwake(true);
            clockTimer.Start();
            boardTimer.Start();
            templateTimer.Start();
            sessionTimer.Start();

            serverDown = !api.ServerReachable;
            ShowConnectionState(true);
            if (api.ServerReachable)
            {
                LoadParts parts = LoadParts.Staff | LoadParts.Board | LoadParts.Templates;
                if (!sessionConfirmed || TokenDueForRenewal()) parts |= LoadParts.Session;
                BeginLoad(parts, null);
                StartReplay();
            }
        }

        private void StartReader()
        {
            try
            {
                capturer = FingerReader.Create(readerBrand, this, config.ReaderLowPriority, SecuGenEngine.VerifyQuality);
                capturer.Start();
            }
            catch (Exception ex)
            {
                // The DigitalPersona runtime (driver) is missing. (SecuGen reports
                // its driver problems through OnReaderProblem instead.)
                readerDriverError = ReaderSetup.DriverMissingBody(readerBrand);
                readerChecked = true;
                Log.Error("The fingerprint reader software could not be opened", ex);
            }
        }

        // ------------------------------------------------------------ the left panel at rest

        private void ShowReaderState()
        {
            if (InvokeRequired) { RunOnUi(ShowReaderState); return; }
            if (sessionEnded || IsDisposed) return;

            string blockTitle = null, blockBody = null;
            if (!engineAvailable)
            {
                blockTitle = "Fingerprint files missing";
                blockBody = ReaderSetup.EngineMissingBody(readerBrand, engineError);
            }
            else if (readerDriverError != null)
            {
                blockTitle = "Fingerprint reader software not installed";
                blockBody = readerDriverError;
            }
            else if (!readerConnected && readerChecked)
            {
                blockTitle = "Fingerprint reader not connected";
                blockBody = "Attendance can't be marked until it is back. Plug the reader into a USB port on this PC (not a hub) "
                    + "— this message goes away by itself as soon as the reader is connected.";
            }
            SetBlocked(blockTitle, blockBody);

            readerDot.DotColor = readerConnected ? Theme.Success : (readerChecked ? Theme.Danger : Theme.Neutral);
            readerLabel.Text = readerConnected ? "READER READY" : (readerChecked ? "READER NOT CONNECTED" : "LOOKING FOR READER...");
            readerLabel.ForeColor = readerConnected ? Theme.Success : (readerChecked ? Theme.Danger : Theme.TextSecondary);

            bool breakReady = blockTitle == null && readerConnected && templates.Loaded && engine.UsableCount(templates.Current) > 0
                && session != null && session.CanPunch && session.AttendanceEnabled;
            reader.SetBreakAvailable(breakReady);
            if (!breakReady && breakArmed) DisarmBreak();

            if (showingDetail) return;
            if (!readerConnected)
            {
                reader.ShowIdle(PulseState.Off, "Looking for the fingerprint reader...", string.Empty, Theme.TextSecondary);
                return;
            }
            if (!templates.Loaded)
            {
                if (templatesLoading || templateError == null)
                    reader.ShowIdle(PulseState.Off, "Loading fingerprints...", "One moment.", Theme.TextSecondary);
                else
                    reader.ShowIdleAction("Fingerprints could not be loaded", templateError, Theme.Warning, "Retry",
                        delegate { forceFullTemplates = true; BeginLoad(LoadParts.Templates, null); });
                return;
            }
            if (engine.UsableCount(templates.Current) == 0)
            {
                if (templates.Current.Count > 0)
                    reader.ShowIdle(PulseState.Off, "Enroll fingers again on this reader",
                        "The fingers on file were scanned on a different make of reader. A manager re-scans each one with \"Enroll fingerprints\" below.", Theme.Warning);
                else
                    reader.ShowIdle(PulseState.Off, "No fingerprints enrolled yet",
                        "A manager enrolls staff with \"Enroll fingerprints\" below.", Theme.Warning);
                return;
            }
            if (breakArmed)
                reader.ShowIdle(PulseState.Ready, "Break: scan your finger", "to start your break, or to end it", Theme.Warning);
            else
                reader.ShowIdle(PulseState.Ready, "Place your finger on the reader", "to clock in or clock out", Theme.TextPrimary);
        }

        // ------------------------------------------------------------ break

        // UI thread. Arms break mode, or cancels it.
        private void Break_Click(object sender, EventArgs e)
        {
            if (sessionEnded || blocked) return;
            if (breakArmed)
            {
                Log.Info("Break cancelled");
                DisarmBreak();
                if (!showingDetail) ShowReaderState();
                return;
            }
            if (busy) return;
            breakArmed = true;
            breakDeadlineUtc = DateTime.UtcNow.AddSeconds(BreakArmSeconds);
            reader.SetBreakArmed(true, BreakArmSeconds);
            breakTimer.Start();
            Sounds.Say("Break. Scan your finger.");
            // A result still on show gives way: the next scan is the break.
            detailTimer.Stop();
            showingDetail = false;
            ShowReaderState();
        }

        private void BreakTick()
        {
            if (!breakArmed) { breakTimer.Stop(); return; }
            int left = (int)Math.Ceiling((breakDeadlineUtc - DateTime.UtcNow).TotalSeconds);
            if (left > 0)
            {
                reader.SetBreakArmed(true, left);
                return;
            }
            Log.Info("Break mode timed out");
            DisarmBreak();
            if (!showingDetail) ShowReaderState();
        }

        // Any thread.
        private void DisarmBreak()
        {
            breakArmed = false;
            if (InvokeRequired) { RunOnUi(DisarmBreak); return; }
            breakTimer.Stop();
            reader.SetBreakArmed(false, 0);
        }

        private void SetBlocked(string title, string body)
        {
            bool nowBlocked = title != null;
            if (nowBlocked) blockedPanel.ShowProblem(title, body);
            if (nowBlocked == blocked) return;
            blocked = nowBlocked;
            blockedPanel.Visible = blocked;
            reader.Visible = !blocked;
            boardPanel.Visible = !blocked;
            if (blocked) blockedPanel.BringToFront();
            enrollButton.Enabled = !blocked;
            if (blocked)
            {
                Log.Info("Counter blocked: " + title);
                Sounds.PlayProblem(null);
            }
            else Log.Info("Counter ready again");
        }

        // ------------------------------------------------------------ the server's half

        private void Api_ReachabilityChanged(object sender, EventArgs e)
        {
            RunOnUi(delegate { ShowConnectionState(false); });
        }

        private void ShowConnectionState(bool initial)
        {
            if (sessionEnded || IsDisposed) return;
            bool down = !api.ServerReachable;
            bool changed = down != serverDown;
            serverDown = down;
            UpdateOfflineNotice();
            if (!changed && !initial) return;

            if (down)
            {
                connectionTimer.Start();
                return;
            }
            connectionTimer.Stop();
            if (initial) return;
            // Back online: whatever is still this PC's copy is fetched fresh,
            // the saved punches go first.
            LoadParts missed = LoadParts.Board | LoadParts.Staff | LoadParts.Templates;
            if (!sessionConfirmed) missed |= LoadParts.Session;
            BeginLoad(missed, null);
            StartReplay();
        }

        private void UpdateOfflineNotice()
        {
            if (InvokeRequired) { RunOnUi(UpdateOfflineNotice); return; }
            if (sessionEnded || IsDisposed) return;
            int waiting = OfflineStore.QueueCount;
            if (serverDown)
            {
                connectionLabel.Text = waiting > 0 ? "OFFLINE  ·  " + waiting + " WAITING TO SEND" : "OFFLINE  ·  NO INTERNET";
                connectionLabel.BackColor = Theme.Warning;
                connectionLabel.Visible = true;
                reader.SetNotice("No connection to the server. Fingerprints still work: each punch is saved on this PC"
                    + (waiting > 0 ? " (" + waiting + " waiting)" : string.Empty) + " and sent when the internet is back.", Theme.Warning);
            }
            else if (waiting > 0)
            {
                connectionLabel.Text = "ONLINE  ·  SENDING " + waiting + " SAVED PUNCH" + (waiting == 1 ? string.Empty : "ES");
                connectionLabel.BackColor = Theme.Info;
                connectionLabel.Visible = true;
                reader.SetNotice("Sending " + waiting + " punch" + (waiting == 1 ? string.Empty : "es") + " saved while offline...", Theme.Warning);
            }
            else
            {
                // Always on show, so the counter can see at a glance that the
                // server is reachable (green) or not (amber, above).
                connectionLabel.Text = "ONLINE";
                connectionLabel.BackColor = Theme.Success;
                connectionLabel.Visible = true;
                reader.SetNotice(null, Theme.Neutral);
            }
        }

        // ------------------------------------------------------------ loading

        // One background load at a time; anything asked for meanwhile is
        // queued and run next, so nothing asked for is lost.
        private void BeginLoad(LoadParts parts, string progressNote)
        {
            if (InvokeRequired) { RunOnUi(delegate { BeginLoad(parts, progressNote); }); return; }
            if (sessionEnded || IsDisposed || parts == LoadParts.None) return;
            if (Interlocked.CompareExchange(ref loading, 1, 0) != 0)
            {
                lock (pendingGate) pendingParts |= parts;
                return;
            }

            bool announce = progressNote != null;
            if (announce)
            {
                refreshButton.Enabled = false;
                boardPanel.ShowNote(progressNote, Theme.Neutral);
            }
            bool wantTemplates = (parts & LoadParts.Templates) != 0 && engineAvailable;
            if (wantTemplates)
            {
                templatesLoading = true;
                ShowReaderState();
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    if ((parts & LoadParts.Session) != 0 && !sessionEnded) error = error ?? RefreshSession();
                    if ((parts & LoadParts.Staff) != 0 && !sessionEnded) error = error ?? LoadStaff();
                    if ((parts & LoadParts.Board) != 0 && !sessionEnded) error = error ?? LoadBoard();
                    if (wantTemplates && !sessionEnded) error = error ?? LoadTemplates();
                }
                catch (Exception ex)
                {
                    Log.Error("Background load failed", ex);
                    error = error ?? ex.Message;
                }
                finally
                {
                    templatesLoading = false;
                    Interlocked.Exchange(ref loading, 0);
                }
                RunOnUi(delegate { FinishLoad(announce, error); });
            });
        }

        private void FinishLoad(bool announce, string error)
        {
            if (sessionEnded || IsDisposed) return;
            refreshButton.Enabled = true;
            ShowReaderState();
            if (announce) boardPanel.ShowNote(error, error != null ? Theme.Danger : Theme.Neutral);
            NoteClockSkew();

            LoadParts queued;
            lock (pendingGate)
            {
                queued = pendingParts;
                pendingParts = LoadParts.None;
            }
            if (queued != LoadParts.None) BeginLoad(queued, null);
        }

        // Each loader returns null on success or a line for the board, and
        // hands a refused sign-in to HandleRejection itself. A failure keeps
        // whatever was loaded before.

        private string RefreshSession()
        {
            Dictionary<string, object> result = api.Get("api/desktop/session");
            if (Json.IsRejection(result)) { EndForRejection(result); return null; }
            if (!Json.Ok(result)) return Json.IsOutage(result) ? null : (Json.Message(result) ?? "Could not check the sign-in.");
            SessionUser refreshed = SessionUser.From(Json.Data(result));
            if (refreshed == null) return null;
            sessionConfirmed = true;
            if (!refreshed.CanRunDesk)
            {
                Log.Info("The account lost the Attendance desktop app permission");
                EndSession(true, "This account is no longer allowed to run the attendance app (its role needs "
                    + "\"Desktop app → Attendance (fingerprint)\" view + create). Sign in with another account.");
                return null;
            }
            session = refreshed;
            OfflineStore.SaveSession(refreshed);
            RunOnUi(ApplySession);
            RenewTokenIfDue();
            return null;
        }

        private static bool TokenDueForRenewal()
        {
            StoredToken stored = TokenStore.Load();
            return stored != null && stored.DueForRenewal;
        }

        // A fresh 7-day token once the saved one is a day old, so a counter
        // that is used every day never has to be signed in again.
        private void RenewTokenIfDue()
        {
            if (!TokenDueForRenewal()) return;
            Dictionary<string, object> result = api.Post("api/desktop/session/renew", new Dictionary<string, object>());
            Dictionary<string, object> data = Json.Data(result);
            string token = Json.Str(data, "token");
            if (!Json.Ok(result) || string.IsNullOrEmpty(token))
            {
                if (!Json.IsOutage(result)) Log.Error("Sign-in not renewed: " + (Json.Message(result) ?? "HTTP " + Json.HttpStatus(result)));
                return;
            }
            DateTime expires = Json.Time(data, "expiresAt") ?? DateTime.UtcNow.AddDays(7);
            api.Token = token;
            TokenStore.Save(token, expires);
            Log.Info("Sign-in renewed until " + ShopTime.ToIso(expires));
        }

        private string LoadStaff()
        {
            Dictionary<string, object> result;
            StaffDirectory loaded = StaffDirectory.Load(api, out result);
            if (loaded == null)
            {
                if (Json.IsRejection(result)) { HandleRejection(result); return null; }
                return Json.IsOutage(result) ? null : (Json.Message(result) ?? "Could not load the staff list.");
            }
            directory = loaded;
            OfflineStore.SaveStaff(loaded);
            var photos = new List<string>();
            foreach (StaffMember m in loaded.All) if (!string.IsNullOrEmpty(m.PhotoUrl)) photos.Add(api.Resolve(m.PhotoUrl));
            PhotoCache.Prefetch(photos);
            return null;
        }

        private string LoadBoard()
        {
            Dictionary<string, object> result = api.Get("api/desktop/board");
            if (Json.IsRejection(result)) { HandleRejection(result); return null; }
            if (!Json.Ok(result)) return Json.IsOutage(result) ? null : (Json.Message(result) ?? "Could not load today's list.");
            BoardData loaded = BoardData.From(result);
            if (loaded == null) return "The server sent an empty list.";
            board = loaded;
            if (loaded.Date != null) boardDay = loaded.Date;
            ShowBoard();
            return null;
        }

        private string LoadTemplates()
        {
            bool full = forceFullTemplates;
            forceFullTemplates = false;
            bool changed;
            Dictionary<string, object> problem = templates.Sync(api, full, out changed);
            if (problem == null)
            {
                templateError = null;
                return null;
            }
            if (Json.IsRejection(problem)) { HandleRejection(problem); return null; }
            templateError = Json.IsOutage(problem)
                ? "No connection to the server, and this PC has no saved copy yet. It needs the internet once."
                : (Json.Message(problem) ?? "The server did not send the list.");
            Log.Error("Fingerprints not loaded: " + templateError + (templates.Loaded ? " (keeping the " + templates.Current.Count + " already here)" : string.Empty));
            return Json.IsOutage(problem) ? null : templateError;
        }

        // The server's list with this PC's own rows on top: punches waiting to
        // be sent (amber) and ones the server would not record (red).
        private void ShowBoard()
        {
            if (InvokeRequired) { RunOnUi(ShowBoard); return; }
            if (sessionEnded || IsDisposed) return;
            var local = new List<BoardRow>();
            List<QueuedPunch> waiting = OfflineStore.QueueSnapshot();
            StaffDirectory people = directory;
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                QueuedPunch q = waiting[i];
                StaffMember m = people.Find(q.EmployeeId);
                local.Add(new BoardRow
                {
                    EmployeeId = q.EmployeeId,
                    Name = q.EmployeeName ?? (m != null ? m.Name : "Someone"),
                    Designation = m != null ? m.Designation : string.Empty,
                    State = "queued",
                    LocalNote = (q.Action == PunchApi.BreakAction ? "break · " : string.Empty)
                        + "saved on this PC at " + ShopTime.Clock(q.ScannedAtUtc + ShopTime.ServerOffset),
                });
            }
            string today = ShopTime.TodayKey;
            lock (syncGate)
            {
                for (int i = syncProblems.Count - 1; i >= 0; i--)
                    if (syncProblems[i].Key == today) local.Add(syncProblems[i].Value);
            }
            boardPanel.SetBoard(board, local);
        }

        // A PC clock that is far off would confuse anyone comparing the screen
        // with a watch; the app uses the server's time anyway, but says so.
        private void NoteClockSkew()
        {
            if (skewNoted || !ShopTime.OffsetKnown) return;
            double minutes = Math.Abs(ShopTime.ServerOffset.TotalMinutes);
            if (minutes < 5) return;
            skewNoted = true;
            Log.Info("This PC's clock is " + Math.Round(minutes) + " min off the server's");
            boardPanel.ShowNote("This PC's clock is " + Math.Round(minutes) + " minutes off. Times here come from the server, "
                + "so attendance is right — but please fix the clock in Windows (Settings → Time).", Theme.Warning);
        }

        // ------------------------------------------------------------ enrol / sign out

        private void Enroll_Click(object sender, EventArgs e)
        {
            if (!enrollShown) return;
            if (blocked)
            {
                boardPanel.ShowNote("Plug the fingerprint reader in first.", Theme.Danger);
                return;
            }
            if (!api.ServerReachable)
            {
                boardPanel.ShowNote("Enrolling needs the internet: the fingerprint is saved on the server.", Theme.Warning);
                return;
            }
            // Only one capture session can own the reader at a time.
            try { if (capturer != null) capturer.Stop(); } catch { }
            bool changed;
            using (var form = new EnrollForm(api, session, directory, templates.Loaded ? templates.Current : null, config.DeviceLabel,
                readerBrand, config.ReaderLowPriority))
            {
                form.ShowDialog(this);
                changed = form.ChangedAny;
                DateTime until = DateTime.UtcNow.AddSeconds(JustEnrolledQuietSeconds);
                lock (justEnrolledUntilUtc)
                    foreach (string id in form.EnrolledIds) justEnrolledUntilUtc[id] = until;
                scansQuietUntilUtc = DateTime.UtcNow.AddSeconds(3);
            }
            try { if (capturer != null) capturer.Start(); } catch (Exception ex) { Log.Error("Reader could not be restarted", ex); }
            if (changed)
            {
                forceFullTemplates = true;
                BeginLoad(LoadParts.Templates | LoadParts.Staff | LoadParts.Board, "Updating the fingerprint list...");
            }
            ShowReaderState();
        }

        // Asks the server for a one-time sign-in code (POST /api/desktop/pos-link,
        // a few seconds at most, on a pool thread) so the POS screen opens
        // already signed in as this counter. Offline, refused or slow: the POS
        // screen opens as before, with its own saved sign-in / offline copy.
        private void Sales_Click(object sender, EventArgs e)
        {
            if (salesOpening) return;
            string posUrl = config.PosUrl;
            if (!api.ServerReachable || string.IsNullOrEmpty(api.Token))
            {
                OpenPos(posUrl);
                return;
            }
            salesOpening = true;
            salesButton.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string url = posUrl;
                try
                {
                    string code = PosLauncher.FetchSignInCode(api);
                    if (code != null) url = PosLauncher.SignInUrl(posUrl, code) ?? posUrl;
                }
                catch (Exception ex)
                {
                    Log.Error("Sales: sign-in code not fetched", ex);
                }
                RunOnUi(delegate
                {
                    salesOpening = false;
                    salesButton.Enabled = true;
                    OpenPos(url);
                });
            });
        }

        private void OpenPos(string url)
        {
            string problem = PosLauncher.Open(url, config.PosUrl);
            if (problem != null) boardPanel.ShowNote(problem, Theme.Danger);
        }

        private void SignOut_Click(object sender, EventArgs e)
        {
            DialogResult answer = MessageBox.Show(this,
                "Sign out this counter?" + Environment.NewLine + Environment.NewLine
                + "Staff cannot clock in until a manager signs in again."
                + (OfflineStore.QueueCount > 0 ? Environment.NewLine + Environment.NewLine
                    + OfflineStore.QueueCount + " punch(es) saved offline stay on this PC and are sent after the next sign-in." : string.Empty),
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            Log.Info("Signed out");
            EndSession(true, null);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            foreach (System.Windows.Forms.Timer t in new[] { detailTimer, boardTimer, templateTimer, clockTimer, connectionTimer,
                replayTimer, sessionTimer, readerGraceTimer, touchTimer, breakTimer })
                t.Stop();
            api.ReachabilityChanged -= Api_ReachabilityChanged;
            try { if (capturer != null) capturer.Stop(); } catch { }
            Sounds.Shutdown();
            KeepAwake(false);
        }

        // ------------------------------------------------------------ IReaderEvents
        //
        // These fire on the reader's own thread. Two rules for everything below:
        // nothing may throw (an exception there ends the app), and nothing
        // waits on the UI thread (it may be waiting on this one inside
        // Stop) -- the window is told what to show with BeginInvoke.

        public void OnReaderConnect()
        {
            if (!readerConnected) Log.Info("Fingerprint reader connected");
            readerConnected = true;
            readerChecked = true;
            RunOnUi(delegate { readerGraceTimer.Stop(); ShowReaderState(); });
        }

        public void OnReaderProblem(string problem)
        {
            readerDriverError = problem;
            readerChecked = true;
            ShowReaderState();
        }

        public void OnReaderDisconnect()
        {
            if (readerConnected) Log.Info("Fingerprint reader disconnected");
            readerConnected = false;
            readerChecked = true;
            touchPending = false;
            ShowReaderState();
        }

        public void OnFingerTouch()
        {
            touchPending = true;
            if (!readerConnected) OnReaderConnect();
            RunOnUi(delegate
            {
                if (sessionEnded || blocked || busy) return;
                touchTimer.Stop();
                detailTimer.Stop();
                showingDetail = true;
                reader.ShowIdle(PulseState.Busy, "Reading...", breakArmed ? "Hold still · break" : "Hold still", breakArmed ? Theme.Warning : Theme.Brand);
            });
        }

        public void OnFingerGone()
        {
            if (!touchPending) return;
            RunOnUi(delegate
            {
                if (!touchPending) return;
                touchTimer.Stop();
                touchTimer.Start();
            });
        }

        public void OnScan(FingerScan scan)
        {
            touchPending = false;
            if (!readerConnected) OnReaderConnect();
            if (sessionEnded) return;
            if (DateTime.UtcNow < scansQuietUntilUtc) return; // finger still on the reader from enrolling
            if (busy)
            {
                ShowMessage("One moment", "Still marking the last finger. Scan again.", Theme.Warning, ErrorHoldMs);
                return;
            }
            if (!templates.Loaded || engine.UsableCount(templates.Current) == 0)
            {
                Sounds.PlayNotRecognised(null);
                ShowMessage(templates.Loaded ? "No fingerprints enrolled yet" : "Fingerprints not loaded yet",
                    templates.Loaded ? "A manager must enroll this finger on this reader first." : "Wait a moment and try again.", Theme.Warning, ErrorHoldMs);
                return;
            }

            busy = true;
            try
            {
                ProcessScan(scan, breakArmed);
            }
            catch (Exception ex)
            {
                Log.Error("Scan failed", ex);
                Sounds.PlayProblem(null);
                ShowMessage("Not marked", "Something went wrong. Please scan again.", Theme.Danger, ErrorHoldMs);
            }
            finally
            {
                busy = false;
            }
        }

        // ------------------------------------------------------------ match here, then tell the server who

        private bool JustEnrolled(string employeeId)
        {
            if (string.IsNullOrEmpty(employeeId)) return false;
            lock (justEnrolledUntilUtc)
            {
                DateTime until;
                if (!justEnrolledUntilUtc.TryGetValue(employeeId, out until)) return false;
                if (DateTime.UtcNow < until) return true;
                justEnrolledUntilUtc.Remove(employeeId);
                return false;
            }
        }

        private void ProcessScan(FingerScan scan, bool forBreak)
        {
            DateTime scannedUtc = DateTime.UtcNow; // this PC's clock; see SendQueued
            ShowMessage("Checking...", string.Empty, Theme.Brand, 0);

            string extractError;
            object features = engine.Probe(scan, out extractError);
            if (features == null)
            {
                Sounds.PlayNotRecognised(null);
                ShowMessage("Could not read that", extractError ?? "Please try again.", Theme.Danger, ErrorHoldMs);
                return;
            }

            MatchResult match = engine.Identify(features, templates.Current);
            if (!match.Matched)
            {
                Sounds.PlayNotRecognised(match.Ambiguous ? "Please try again." : "Not recognised.");
                ShowMessage(match.Ambiguous ? "Please scan again" : "Not recognised",
                    match.Ambiguous ? "That finger looked like more than one person."
                        : "Try again with the finger you enrolled. Not enrolled yet? Ask a manager.",
                    Theme.Danger, ErrorHoldMs);
                return;
            }

            // Recognised HERE: the name goes up at once, before the server answers.
            // A matched scan uses up break mode.
            if (JustEnrolled(match.EmployeeId))
            {
                StaffMember enrolled = directory.Find(match.EmployeeId);
                Sounds.PlayNotRecognised(null);
                ShowMessage("Fingerprint registered", (enrolled != null ? enrolled.Name + ": " : string.Empty)
                    + "saved, not clocked in. Clock in with this finger after 2 minutes.", Theme.Brand, ErrorHoldMs);
                return;
            }

            string action = forBreak ? PunchApi.BreakAction : null;
            if (forBreak) DisarmBreak();
            StaffMember person = directory.Find(match.EmployeeId);
            string photo = person != null ? api.Resolve(person.PhotoUrl) : null;
            RunOnUi(delegate
            {
                if (sessionEnded) return;
                detailTimer.Stop();
                showingDetail = true;
                reader.ShowPerson(person, "Recognised", forBreak ? "Marking break..." : "Marking attendance...",
                    forBreak ? Theme.Warning : Theme.Brand, photo);
            });
            Punch(match.EmployeeId, person, scannedUtc, action);
        }

        private void Punch(string employeeId, StaffMember person, DateTime scannedUtc, string action)
        {
            string clientId = PunchApi.NewClientId();
            // No connection, or older punches still waiting: this one goes in the
            // queue behind them, so the server gets them in the order they happened.
            if (!api.ServerReachable || OfflineStore.QueueCount > 0)
            {
                SaveOffline(employeeId, person, clientId, scannedUtc, action);
                return;
            }

            Dictionary<string, object> result = PunchApi.SendLive(api, employeeId, clientId, config.DeviceLabel, action);

            // No answer: it may or may not have been recorded. Saved, and sent
            // again under the SAME clientId, which the server records only once.
            if (Json.IsOutage(result))
            {
                SaveOffline(employeeId, person, clientId, scannedUtc, action);
                return;
            }

            string name = person != null ? person.Name : null;
            string photo = person != null ? api.Resolve(person.PhotoUrl) : null;
            if (Json.IsRejection(result))
            {
                Sounds.PlayProblem(null);
                ShowResult(name, person, photo, "NOT MARKED", null, "sign-in problem", Theme.Danger,
                    Json.Message(result) ?? "The counter's sign-in was refused.", ErrorHoldMs);
                HandleRejection(result);
                return;
            }

            PunchResult punch = Json.Ok(result) ? PunchResult.From(result) : null;
            if (punch == null)
            {
                string message = Json.Message(result) ?? "The server did not record it.";
                string code = Json.Code(result);
                Log.Error("Punch for " + employeeId + (action != null ? " (" + action + ")" : string.Empty) + " refused: "
                    + (code != null ? code + " " : string.Empty) + message);
                // A break the server will not start (not clocked in yet, or
                // already gone home): amber, with the server's own sentence.
                bool breakRefused = action != null && (code == "NOT_CLOCKED_IN" || code == "ALREADY_OUT");
                Sounds.PlayProblem(breakRefused ? "No break." : "Not marked.");
                ShowResult(name, person, photo, breakRefused ? "NO BREAK" : "NOT MARKED", null, null,
                    breakRefused ? Theme.Warning : Theme.Danger, message, ErrorHoldMs);
                return;
            }

            Log.Info("Punch " + employeeId + " -> " + punch.Kind + (punch.Duplicate ? " (double tap)" : string.Empty)
                + " " + punch.Status + (punch.LateMinutes > 0 ? " late " + punch.LateMinutes : string.Empty));
            Announce(punch, person);
            ShowResult(punch.Name ?? name, person, photo, punch.Headline,
                punch.At.HasValue ? ShopTime.Clock(punch.At.Value) : null, punch.Detail, punch.Accent, punch.Message, ResultHoldMs);
            BeginLoad(LoadParts.Board, null);
        }

        private static void Announce(PunchResult punch, StaffMember person)
        {
            string first = person != null ? person.Name.Split(' ')[0] : (punch.Name ?? string.Empty).Split(' ')[0];
            string speech = punch.Speech(first);
            if (punch.IsBreakStart) Sounds.PlayOut(speech);
            else if (punch.IsBreakEnd) Sounds.PlayIn(speech);
            else if (punch.IsOut) Sounds.PlayOut(speech);
            else if (punch.IsLate) Sounds.PlayLate(speech);
            else Sounds.PlayIn(speech);
        }

        // ------------------------------------------------------------ offline

        private void SaveOffline(string employeeId, StaffMember person, string clientId, DateTime scannedUtc, string action)
        {
            bool added = OfflineStore.Enqueue(new QueuedPunch
            {
                ClientId = clientId,
                EmployeeId = employeeId,
                EmployeeName = person != null ? person.Name : null,
                ScannedAtUtc = scannedUtc,
                Action = action,
            });
            int waiting = OfflineStore.QueueCount;
            bool forBreak = action == PunchApi.BreakAction;
            Log.Info("Punch " + employeeId + (forBreak ? " (break)" : string.Empty) + " saved offline (" + waiting + " waiting)");

            string name = person != null ? person.Name : "Recognised";
            string first = name.Split(' ')[0];
            if (added) Sounds.PlayIn(first + (forBreak ? ", break saved." : ", saved."));
            ShowResult(name, person, person != null ? api.Resolve(person.PhotoUrl) : null, forBreak ? "BREAK SAVED" : "SAVED",
                ShopTime.Clock(scannedUtc + ShopTime.ServerOffset), "on this PC — sent when online", Theme.Warning,
                "No connection to the server right now. This " + (forBreak ? "break" : "punch") + " is kept on this PC (" + waiting
                + " waiting) and sent automatically with its real time when the internet is back.", ResultHoldMs);
            UpdateOfflineNotice();
            ShowBoard();
            if (api.ServerReachable) StartReplay();
        }

        // Sends the saved punches oldest first, one at a time, on a pool thread.
        // Stops at the first one that gets no answer and leaves the rest.
        private void StartReplay()
        {
            if (sessionEnded || OfflineStore.QueueCount == 0) return;
            if (Interlocked.CompareExchange(ref replaying, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                int sent = 0, problems = 0;
                try
                {
                    while (!sessionEnded)
                    {
                        QueuedPunch next = OfflineStore.Peek();
                        if (next == null) break;
                        bool? outcome = SendQueued(next);
                        if (outcome == null) break;
                        OfflineStore.Remove(next.ClientId);
                        if (outcome == true) sent++;
                        else problems++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Sending the saved punches failed", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref replaying, 0);
                }

                int left = OfflineStore.QueueCount;
                if (sent + problems > 0) Log.Info("Saved punches sent: " + sent + " recorded, " + problems + " not recorded, " + left + " still waiting");
                RunOnUi(delegate
                {
                    if (sessionEnded || IsDisposed) return;
                    UpdateOfflineNotice();
                    ShowBoard();
                    if (left > 0) replayTimer.Start();
                    else replayTimer.Stop();
                    if (sent + problems > 0)
                    {
                        boardPanel.ShowNote(sent + (sent == 1 ? " punch" : " punches") + " saved offline sent."
                            + (problems > 0 ? " " + problems + " not recorded — shown in red; a manager can fix them under HR → Attendance." : string.Empty),
                            problems > 0 ? Theme.Danger : Theme.Success);
                        BeginLoad(LoadParts.Board, null);
                    }
                });
            });
        }

        // True: recorded (or already recorded). False: the server answered and
        // will not record it (too old, employee inactive...). Null: no answer, keep it.
        private bool? SendQueued(QueuedPunch punch)
        {
            Dictionary<string, object> result = PunchApi.SendQueued(api, punch, config.DeviceLabel);
            if (Json.IsRejection(result)) { HandleRejection(result); return null; }
            bool? outcome = PunchApi.QueuedOutcome(result);
            if (outcome == null) return null;

            string who = punch.EmployeeId + (string.IsNullOrEmpty(punch.Action) ? string.Empty : " (" + punch.Action + ")")
                + " scanned " + ShopTime.ToIso(punch.ScannedAtUtc);
            if (outcome == true)
            {
                PunchResult answer = PunchResult.From(result);
                Log.Info("Saved punch " + who + " -> " + (answer != null ? answer.Kind : "recorded")
                    + (answer != null && answer.Repeated ? " (already recorded)" : string.Empty));
                return true;
            }

            string reason = Json.Message(result) ?? "not recorded";
            Log.Error("Saved punch " + who + " not recorded: " + reason);
            StaffMember m = directory.Find(punch.EmployeeId);
            var row = new BoardRow
            {
                EmployeeId = punch.EmployeeId,
                Name = punch.EmployeeName ?? (m != null ? m.Name : "Someone"),
                Designation = m != null ? m.Designation : string.Empty,
                State = "problem",
                LocalNote = "scanned " + ShopTime.Clock(punch.ScannedAtUtc + ShopTime.ServerOffset) + " — " + reason.TrimEnd('.'),
            };
            lock (syncGate) syncProblems.Add(new KeyValuePair<string, BoardRow>(ShopTime.DayKey(punch.ScannedAtUtc + ShopTime.ServerOffset), row));
            return false;
        }

        // ------------------------------------------------------------ showing things on the reader side

        // One line on the reader panel, held for `holdMs` (0 = an in-between
        // state that the next one replaces).
        private void ShowMessage(string headline, string subtext, Color color, int holdMs)
        {
            RunOnUi(delegate
            {
                if (sessionEnded) return;
                reader.ShowIdle(holdMs == 0 ? PulseState.Busy : PulseState.Off, headline, subtext, color);
                Hold(holdMs);
            });
        }

        private void ShowResult(string name, StaffMember person, string photo, string word, string time, string detail,
            Color accent, string message, int holdMs)
        {
            RunOnUi(delegate
            {
                if (sessionEnded) return;
                reader.ShowResult(name ?? "Recognised", person != null ? person.Designation : string.Empty, photo, word, time, detail, accent, message);
                Hold(holdMs);
            });
        }

        // UI thread.
        private void Hold(int holdMs)
        {
            showingDetail = true;
            detailTimer.Stop();
            if (holdMs <= 0) return;
            detailTimer.Interval = holdMs;
            detailTimer.Start();
        }

        private void ReturnToWaiting()
        {
            showingDetail = false;
            ShowReaderState();
        }

        // ------------------------------------------------------------ sign-in refused

        // Staff list, board, fingerprints or a punch was refused (401 / 403).
        // That alone does not sign the counter out: the session itself is asked
        // (GET /api/desktop/session) and only its own refusal -- the token is
        // really expired / revoked, the account disabled, the permission
        // removed -- ends it. Session fine or no answer: keep working (renewing
        // the token if it is due). Runs on a worker thread.
        private int confirmingSession;
        private void HandleRejection(Dictionary<string, object> result)
        {
            if (sessionEnded) return;
            if (Interlocked.CompareExchange(ref confirmingSession, 1, 0) != 0) return;
            try
            {
                Log.Info("Request refused (HTTP " + Json.HttpStatus(result) + " " + (Json.Message(result) ?? string.Empty)
                    + "); checking the sign-in before doing anything");
                Dictionary<string, object> check = api.Get("api/desktop/session", 15000);
                if (Json.IsRejection(check)) { EndForRejection(check); return; }
                if (Json.Ok(check))
                {
                    SessionUser user = SessionUser.From(Json.Data(check));
                    if (user != null && !user.CanRunDesk)
                    {
                        EndForRejection(new Dictionary<string, object> { { "http_status", 403 },
                            { "message", "This account is no longer allowed to run the attendance app" } });
                        return;
                    }
                    if (user != null)
                    {
                        session = user;
                        OfflineStore.SaveSession(user);
                        RunOnUi(ApplySession);
                    }
                    RenewTokenIfDue();
                }
                Log.Info("The sign-in is still valid; the counter stays signed in");
            }
            finally
            {
                Interlocked.Exchange(ref confirmingSession, 0);
            }
        }

        // The server refused the session itself: the only way, besides the
        // Sign out button, that the counter goes back to the sign-in screen.
        private void EndForRejection(Dictionary<string, object> result)
        {
            int status = Json.HttpStatus(result);
            string message = status == 401
                ? "The counter's sign-in has expired (it lasts 7 days and renews itself while the app runs and is online). "
                    + "A manager needs to sign in again."
                : "The server refused this account: " + (Json.Message(result) ?? "no permission") + ". Sign in with an account that has "
                    + "\"Desktop app → Attendance (fingerprint)\" permission.";
            Log.Info("Session ended by the server: HTTP " + status + " " + (Json.Message(result) ?? string.Empty));
            EndSession(true, message);
        }

        private void EndSession(bool clearToken, string message)
        {
            if (InvokeRequired)
            {
                sessionEnded = true;
                try { BeginInvoke(new Action<bool, string>(EndSessionOnUi), clearToken, message); }
                catch (Exception ex) { Log.Error("Session end could not be delivered", ex); }
                return;
            }
            EndSessionOnUi(clearToken, message);
        }

        private bool endRaised;
        private void EndSessionOnUi(bool clearToken, string message)
        {
            sessionEnded = true;
            if (endRaised) return;
            endRaised = true;
            if (LoggedOut != null) LoggedOut(this, new SessionEndedEventArgs { ClearToken = clearToken, Message = message });
        }

        // ------------------------------------------------------------ keeping the PC awake
        //
        // A finger on the reader is not a key press, so Windows thinks a busy
        // counter is idle and blanks the screen. While this window is open it
        // is asked not to (withdrawn when the window closes).

        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_CONTINUOUS = 0x80000000;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private static void KeepAwake(bool on)
        {
            try
            {
                uint previous = SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
                if (previous == 0) Log.Error("Windows did not accept the request to keep this PC awake");
            }
            catch (Exception ex)
            {
                Log.Error("Could not ask Windows to keep this PC awake", ex);
            }
        }

        // Developer preview (Preview.cs, /render): put the window in a given
        // state without a reader or a server. Not used by the running app.
        internal void PreviewState(BoardData previewBoard, bool readerReady, bool offline, string blockTitle, string blockBody)
        {
            WindowState = FormWindowState.Normal;
            board = previewBoard;
            readerConnected = readerReady;
            readerChecked = true;
            serverDown = offline;
            connectionLabel.Visible = true;
            connectionLabel.Text = offline ? "OFFLINE  ·  2 WAITING TO SEND" : "ONLINE";
            connectionLabel.BackColor = offline ? Theme.Warning : Theme.Success;
            if (offline) reader.SetNotice("No connection to the server. Fingerprints still work: each punch is saved on this PC (2 waiting) and sent when the internet is back.", Theme.Warning);
            readerDot.DotColor = readerReady ? Theme.Success : Theme.Danger;
            readerLabel.Text = readerReady ? "READER READY" : "READER NOT CONNECTED";
            readerLabel.ForeColor = readerReady ? Theme.Success : Theme.Danger;
            SetBlocked(blockTitle, blockBody);
            boardPanel.SetBoard(previewBoard, null);
            Relayout();
        }

        internal ReaderPanel PreviewReader { get { return reader; } }

        // Hands work from the reader's thread or a pool thread to the UI thread
        // without waiting. Never throws (the window may close in between).
        private void RunOnUi(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The window closed between the check and the call.
            }
            catch (Exception ex)
            {
                Log.Error("The window could not be updated from another thread", ex);
            }
        }
    }
}
