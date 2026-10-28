# DollarCoPos

The counter PC's attendance app. Staff clock in and out by putting a finger on
a **fingerprint reader** — that is the **only** way attendance is marked at
DOLLAR & Co. The app talks to the same POS server as the admin panel, so every
punch shows up straight away under **HR → Attendance**, and payroll uses it.

It also has a **Sales** button that opens the POS selling screen in its own
window on the same PC. That screen keeps selling when the internet drops and
sends the sales to the server by itself when it comes back (see *Sales*).

```
   Fingerprint reader (USB)
            │
            ▼
   ┌────────────────────────────┐        HTTPS: only "who" and "when",
   │  Counter PC                │        never a fingerprint scan
   │  DollarCoPos    │ ─────────────────────────────────►  POS server
   │  matches the finger HERE   │                                    (/api/desktop)
   └────────────────────────────┘
```

---

## 1. What the PC needs

| | |
|---|---|
| Windows | Windows 10 or 11, **64-bit** |
| .NET Framework 4.x | Already part of Windows 10 and 11 — nothing to install |
| Fingerprint reader | **Required.** A DigitalPersona USB reader (U.are.U 4000/4500 family) **plus** its software: *DigitalPersona One Touch for Windows runtime (64-bit)*. Without the reader, attendance cannot be marked. |
| Internet | Needed for the first sign-in and for enrolling fingers. After that the counter keeps working when the internet drops (see *No internet*). |
| A manager / admin account | The POS email and password of an account whose role has **Desktop app → Attendance (fingerprint)** (View + Create). See step 4. |
| Microsoft Edge | For the **Sales** button. Already part of Windows 10 and 11 (Chrome or any browser works too). |

## 2. Install (once)

1. **Install the reader software.** Run the *DigitalPersona One Touch for
   Windows* runtime installer (from the CD/USB stick that came with the reader,
   the shop that sold it, or HID Global's driver downloads at
   www.hidglobal.com/drivers). Restart the PC if it asks. Plug the reader into a
   USB port **on the PC itself**, not a hub.
2. **Install the app.** Run **`DollarCoPos-Setup.exe`** and click
   through. It installs to *Program Files\DollarCo Attendance*, puts a shortcut
   on the desktop and in the Start menu, and (ticked) starts the app with
   Windows. If the reader software is missing, the installer says so — install
   it before using the counter. Running a newer setup later upgrades in place
   and keeps the PC's settings, sign-in and any punches saved offline.
3. **Stop the PC sleeping.** *Settings → System → Power → Screen and sleep →
   When plugged in, put my device to sleep after: **Never***. (The app also
   keeps the screen awake while it is open.) If the reader ever disconnects by
   itself, open *Device Manager*, find the reader and each *USB Root Hub*, and
   on *Power Management* untick *Allow the computer to turn off this device*.
4. **Give a manager account the permission.** In the admin panel open
   **Roles**, edit the manager's (or admin's) role and, under the **Desktop
   app** section, tick:
   * **Attendance (fingerprint) → View and Create** — needed to sign the
     counter in and mark attendance.
   * **Sales on the counter PC (works offline) → View and Create** — shows the
     **Sales** button and lets that PC sell with no internet and upload those
     sales later. Without it the Sales button is hidden.

   To enroll fingerprints the account also needs **Employees → Edit** or
   **Attendance → Edit** (managers and admins usually have these). Admin has
   everything already. Existing roles are not changed by themselves: tick the
   boxes once.
5. **Sign the counter in — once.** Start *DollarCoPos* and sign in
   with that manager's POS email and password. The counter stays signed in
   from then on (the sign-in renews itself every day it is used; if the PC is
   left off for more than a week, sign in again).
6. **Name the PC.** *Settings* (bottom right) → *This PC's name*, e.g.
   `Counter PC`. The name is stored with every punch ("fingerprint · Counter
   PC") so the admin panel shows where it came from. Sound and the spoken
   result can be switched off here too. *Sales screen address* is the page
   the Sales button opens; leave it as it is
   (`https://pos-one-dollar-store-frontend.vercel.app/admin/pos`).
7. **Enroll everyone's fingers** (next section), then test: one touch should
   show the person's name at once and then **IN** with the time.

The version is shown on the sign-in and Settings windows (*Version 1.1.0*).

## 3. Enroll fingerprints (managers only)

Employees are added in the admin panel (**HR → Employees**) first. Then, on the
counter PC:

1. Press **Enroll fingerprints** (bottom left).
2. **Sign in as a manager** — even if the counter is already signed in. This
   stops anyone at the counter putting their own finger on someone else's name.
3. **Pick the employee** on the left (search by name or code). The badge says
   how many fingers they have on file.
4. **Pick the finger** — left hand on the top row, right hand on the bottom row.
   Use the **index finger** of each hand (they read best).
5. Press **Start scanning** and have the employee press the **same finger four
   times**: flat, firm, lifting it between scans. The bar fills one segment per
   good scan.
6. **Saved.** Enroll a **second finger** (the other hand's index) as a backup in
   case of a cut or a bandage. **Up to 2 fingers per employee.**

* **Replace a finger** that reads badly: pick that finger (it says *ON FILE*)
  and scan it again.
* **Remove a finger**: *Remove* next to it under *Fingers on file*.
* **One finger, one person.** Before saving, the app checks the new finger
  against every finger already enrolled. If it matches someone else, it is
  refused (*"This finger is already enrolled for …"*): use another finger, or
  remove the wrong one first.
* Enrolling needs the internet (the fingerprint is saved on the POS server).
  The counter picks up the new finger straight away; other counters within 5
  minutes.

## 4. Daily use

Staff put their enrolled finger on the reader when they **arrive** and when they
**leave**. That's all. The server decides whether it is *in* or *out*, and
whether they are on time, late or doing overtime, using the shift and HR policy
set in the admin panel.

**Left side — the reader:**

| The screen shows | Meaning |
|---|---|
| Pulsing yellow fingerprint — *Place your finger on the reader* | Ready. |
| *Reading… Hold still* | A finger is on the reader. |
| The person's **photo / initials and name**, *Marking attendance…* | The finger was recognised on this PC; the server is recording it. |
| **IN 9:58 am · on time** (green) | Clocked in on time. Chime + "Ali, clocked in." |
| **IN 10:12 am · 12 min late** (amber) | Clocked in late. Two-note sound + "…12 minutes late." |
| **OUT 6:05 pm · worked 7 h 35 m** (blue) | Clocked out. |
| **ALREADY IN / ALREADY OUT** (grey) | Same finger twice within a minute — the first one counted. |
| **SAVED · on this PC** (amber) | No internet: the punch is kept on this PC and sent later (see below). |
| **Not recognised** (red, three low beeps) | Try again with the enrolled finger. Still failing? A manager re-enrolls it. |
| **NOT MARKED** (red) | The server refused it (e.g. employee deactivated). The message says why. |

After 8 seconds the screen goes back to *Place your finger*.

**Right side — today's staff:** five counts across the top (**in now, late,
not in yet, absent, on leave**) and one line per person: shift times, **IN since
9:58 am**, **OUT** with both times, **NOT IN YET**, **ABSENT**, **ON LEAVE**,
**NO CHECK-OUT**. It refreshes after every punch, every 3 minutes, and at
midnight (Pakistan time — all times on screen are PKT, whatever the PC's clock
says).

### Forgot to clock in? Reader broken?

There is **no other way to clock in** on the counter. A manager fixes it in the
admin panel: **HR → Attendance → Correct times** on that day, entering the real
time **with a reason**. The same goes for a day the reader was unplugged or
broken.

### Reader not connected

If the reader is unplugged (or its software is missing), the whole counter
screen turns red: **Fingerprint reader not connected — attendance can't be
marked**. Plug it back in; the message goes away by itself. Missed punches →
*Correct times* as above.

### Sales

Press **Sales** (bottom, green) to open the POS selling screen. It opens in its
own Microsoft Edge window (no tabs, no address bar, full screen) with its own
saved sign-in, separate from anyone's normal browsing. Sign in to it once with a
cashier's or manager's POS account; it stays signed in.

* Pressing **Sales** again just brings that window to the front.
* It opens even with **no internet**: the app does not check anything first.
  The selling screen then works from its offline copy and keeps the sales on
  this PC until the internet is back (see the POS screen's *Offline* badge).
* No Edge on the PC? It uses Chrome, or else the normal web browser.
* The button is hidden when the signed-in counter account's role does not have
  **Desktop app → Sales on the counter PC**.

## 5. No internet

Fingerprints keep working without the internet:

* The finger is matched on this PC (it keeps an encrypted copy of the enrolled
  fingers), the punch is **saved on this PC with its real time**, and the screen
  shows **SAVED** in amber.
* The header shows **OFFLINE · n WAITING TO SEND**, and the waiting punches are
  listed at the top of today's list in amber.
* When the internet is back they are sent automatically, oldest first, with the
  time they actually happened (even if the PC's clock is wrong). Sending the same
  punch twice never counts it twice.
* A punch more than **48 hours** old is not accepted by the server; it is shown
  in red (*NOT RECORDED*) and a manager adds it with *Correct times*.
* Enrolling fingers needs the internet.
* Don't uninstall the app while punches are waiting — the uninstaller warns you.

If the app is started with no internet, it opens on the last signed-in account
and works offline. The very first sign-in on a PC needs the internet.

## 6. Troubleshooting

| Problem | What to do |
|---|---|
| Red screen *Fingerprint reader not connected* | Check the USB cable; use a port on the PC itself. See install step 3 for the power setting. |
| Red screen *Fingerprint reader software not installed* | Install the DigitalPersona One Touch for Windows runtime (64-bit), restart. |
| Red screen *Fingerprint files missing* | Run the installer again. |
| *Not recognised* for one person, again and again | Clean the reader glass. Enroll that finger again (*Replace*), or enroll the other hand. |
| *No fingerprints enrolled yet* | A manager enrolls staff (section 3). |
| Touching the reader does nothing while another program is in front | Close the app, open `desktopApp.config.json` (in the app folder) in Notepad as administrator, add `"readerPriority": "low",` on its own line, save, start the app. |
| *OFFLINE* in the header | The PC can't reach the server. Check the internet; it clears by itself. |
| *This account cannot run the attendance app* | The role needs **Desktop app → Attendance (fingerprint) → View + Create** (install step 4). |
| *This account cannot enroll fingerprints* | The manager's role needs **Employees → Edit** or **Attendance → Edit**. |
| *The counter's sign-in has expired* | A manager signs in again. |
| *This PC's clock is … minutes off* | Fix the clock (Windows Settings → Time & language). Attendance is still right: the app uses the server's time. |
| *… is already open on this PC* | Only one copy can use the reader. Find it on the taskbar. |
| Anything else | Look in `%LOCALAPPDATA%\DollarCoAttendance\attendance.log` (every failed call and error is written there). |

## 7. Privacy and security

* Fingerprints are matched **on the counter PC**. The server stores each
  finger's DigitalPersona template (not a picture of the finger) and never sees
  a scan. Removing a finger deletes its template.
* The counter keeps its copies (sign-in, fingerprints, staff list, waiting
  punches) in `%LOCALAPPDATA%\DollarCoAttendance\`, **encrypted with Windows
  DPAPI** for the Windows account the app runs as. *Sign out* deletes the
  copies but keeps waiting punches.
* The app talks to the server over **https** only (plain http is refused except
  for a server on the same PC).
* No salary, CNIC or phone number is ever sent to the counter.

---

## For developers

**Build** (no Visual Studio: the C# 5 compiler that ships with Windows):

```
build.cmd               -> bin\DollarCoPos.exe       (desktopApp.config.json: live server)
build-local.cmd         -> bin-local\                       (desktopApp.local.config.json: http://localhost:5000/,
                                                             Sales -> http://localhost:3000/admin/pos)
build.cmd /installer    -> ...and installer\Output\DollarCoPos-Setup.exe  (Inno Setup 6:
                           winget install JRSoftware.InnoSetup)
```

`lib\` holds the DigitalPersona One Touch SDK .NET wrappers and engine
(`DPFP*.dll`, `dpHFtrEx.dll`, `dpHMatch.dll`), copied beside the exe by the
build. A config already in the output folder is kept (it is that PC's own
settings). Bump `AppInfo.Version` in `src\AppInfo.cs` for every build handed to
the shop. `app.ico`, `assets\logo.png` (the full *DOLLAR & Co* on its own dark rounded
plate, so the white *Co* shows on any background), `assets\icon-1024.png`
(*D&* over *Co*) and the installer pictures are made by
`python tools\make_brand.py ..\pos_frontend\public\images\logo.png assets` (needs Pillow).

**Config** (`desktopApp.config.json` beside the exe; the Settings window edits it):
`apiBaseUrl` (POS server), `deviceName`, `posUrl` (the page the Sales button
opens), `sound`, `voice`. A missing key takes its default, so a config from an
older version still works (no `posUrl` → the live POS screen).

**Checks without a window or a reader:**

```
bin\DollarCoPos.exe /selftest results.txt
    shop-time wording, JSON parsing, template sync merge, https rule, Sales
    address + features switch, DPAPI queue and sign-in on this Windows account,
    fingerprint engine DLLs load
bin\DollarCoPos.exe /apitest <server> <email> <password> results.txt
    the app's HTTP code end to end against a TEST server (it records punches --
    never the shop's live server): sign-in, staff, template sync, live + offline
    punches, clientId retry, 48 h limit, board, renew, enrol + remove
bin\DollarCoPos.exe /render <folder>
    draws each window with sample data to PNGs (off-screen) to check the layout
```

**Server API** (`pos_backend`, `src/routes/modules/desktopRoutes.js`):

| Endpoint | Permission | Use |
|---|---|---|
| `POST /user/login` | — | sign-in (email + password → JWT, 7 days) |
| `GET /api/desktop/session` | signed in | who, `canView` / `canPunch` / `canEnroll`, `features: { attendance, sales }` (`sales` = desktop_pos view: show the Sales button; no `features` from an older server → shown) |
| `POST /api/desktop/session/renew` | attendance_kiosk view | fresh 7-day token (the app renews daily) |
| `GET /api/desktop/employees` | attendance_kiosk / attendance / employees view | names, code, designation, photo, fingers on file, in/out now |
| `GET /api/desktop/fingerprints[?updatedSince=ISO]` | attendance_kiosk view | templates for local 1:N matching; incremental: changed + `removed` + `employeeIds` |
| `POST /api/desktop/fingerprints/:employeeId` | employees edit or attendance edit | `{ finger, template, quality, deviceName }`, max 2 per employee, re-scan replaces |
| `DELETE /api/desktop/fingerprints/:employeeId/:finger` | employees edit or attendance edit | remove a finger |
| `POST /api/desktop/punch` | attendance_kiosk create | `{ employeeId, method: 'fingerprint', clientId, deviceName, offline?, at?, sentAt? }` → `attendanceCore.punch(source 'device')`; same `clientId` returns the first answer; offline `at` up to 48 h |
| `GET /api/desktop/board?date=` | attendance_kiosk view | today's rows (name, designation, shift, in/out, state, late) + counts |

Backend tests (in-memory MongoDB harness, never the real database):
`STRICT_MODULES=1 node desktop.test.js` and `node desktop-app.apitest.js`
(runs `/apitest` against the harness) in the scratchpad harness folder.

**Layout:**

```
src/
  Program.cs            single instance, crash handlers, sign-in <-> counter window
  AppInfo.cs  Log.cs    version; %LOCALAPPDATA%\DollarCoAttendance\attendance.log
  AppConfig.cs          desktopApp.config.json (server, PC name, Sales screen address, sound, voice)
  PosLauncher.cs        Sales button: POS screen in an Edge app window (Chrome / default browser fallback)
  ApiClient.cs          JSON over HTTPS (+ Json helpers, server clock from Date header)
  ShopTime.cs           PKT times and wording ("9:58 am", "7 h 35 m")
  TokenStore.cs         DPAPI-saved sign-in, one per server
  OfflineStore.cs       DPAPI copies + the queue of punches taken offline
  TemplateStore.cs      fingerprint list: full + incremental sync, offline copy
  PunchApi.cs           live / queued punch requests
  Models.cs             session, staff, punch result, board rows
  FingerprintMatcher.cs local 1:N matching (DigitalPersona engine)
  FingerprintEnroller.cs four scans -> one template
  MainForm.cs           the counter: reader, punches, offline, refresh timers
  ReaderPanel.cs        left: pulse, name + photo, IN / OUT result card
  BoardPanel.cs         right: counts + today's staff
  BlockedPanel.cs       full-screen "reader not connected"
  EnrollForm.cs         manager sign-in, pick employee + finger, 4 scans, upload / remove
  LoginForm.cs  SettingsForm.cs
  Theme.cs  Controls.cs  ReadyPulse.cs  Avatar.cs   black / yellow brand, owner-drawn controls
  Sounds.cs  Voice.cs   chimes, tones, spoken result
  SelfTest.cs  Preview.cs   /selftest, /apitest, /render
installer/DollarCoPos.iss   Inno Setup 6 script
```

## Files not in git

This repo is public, so two sets of third-party files are not committed. Put
them back before running `build.cmd`:

- `lib\` - the DigitalPersona U.are.U SDK DLLs: `DPFPDevNET.dll`, `DPFPShrNET.dll`,
  `DPFPEngNET.dll`, `DPFPVerNET.dll`, `dpHFtrEx.dll`, `dpHMatch.dll` (from the
  DigitalPersona SDK / reader driver package).
- `sounds\` - `clock-in.mp3` and `clock-out.mp3` (any short chimes; without them
  the app still works, just with no chime - the spoken result still plays).
