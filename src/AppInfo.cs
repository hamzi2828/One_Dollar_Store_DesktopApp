using System;
using System.IO;
using System.Reflection;

// What Windows shows under Properties > Details for the exe. All of it follows
// from AppInfo.Version below.
[assembly: AssemblyTitle(DollarCoAttendance.AppInfo.Name)]
[assembly: AssemblyProduct(DollarCoAttendance.AppInfo.Name)]
[assembly: AssemblyCompany(DollarCoAttendance.AppInfo.Company)]
[assembly: AssemblyVersion(DollarCoAttendance.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(DollarCoAttendance.AppInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(DollarCoAttendance.AppInfo.Version)]

namespace DollarCoAttendance
{
    // Which build this is. The exe's properties, the sign-in and Settings
    // windows, the first line of the log and the user-agent sent with every
    // request all read it from here.
    public static class AppInfo
    {
        // BUMP THIS for every build handed to the shop: major.minor.fix.
        public const string Version = "1.2.1";

        public const string Name = "DollarCoPos";
        public const string Company = "DOLLAR & Co";

        // Sent as the user-agent, so the server log can tell this app's calls
        // apart from the website's.
        public const string ClientId = "DollarCoPosDesktop/" + Version;

        public const string Label = "Version " + Version;

        // %LOCALAPPDATA%\<this>: log, saved sign-in, offline copies.
        public const string DataFolderName = "DollarCoAttendance";

        public static string DataFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
            }
        }

        // When this exe was built, for the log: tells two builds apart even if
        // nobody remembered to bump Version between them.
        public static string BuildStamp
        {
            get
            {
                try
                {
                    return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm");
                }
                catch (Exception)
                {
                    return "unknown";
                }
            }
        }
    }
}
