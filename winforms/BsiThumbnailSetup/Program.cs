using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BsiThumbnailSetup
{
    /// <summary>
    /// Registers BsiThumbnail.dll as the Explorer thumbnail handler for .bsi files,
    /// for the current user only, so no administrator rights are needed.
    /// Run with no arguments to be asked, or with /install or /uninstall.
    /// </summary>
    static class Program
    {
        const string Clsid = "{6B1F4E52-3C8A-4D27-9E61-B5A0D2C47F18}";
        const string ClassName = "BsiThumbnail.BsiThumbnailProvider";
        const string ThumbnailProviderIid = "{e357fccd-a995-4576-b01f-234630154e96}";
        const string Extension = ".bsi";
        const string Title = "Battlespire BSI thumbnails";

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            string mode = args.Length > 0 ? args[0].TrimStart('/', '-').ToLowerInvariant() : "";
            bool quiet = mode.Length > 0;
            try
            {
                if (mode == "")
                {
                    bool installed = IsInstalled();
                    string q = installed
                        ? "BSI thumbnails are turned on for your account.\n\nTurn them off?"
                        : "Show thumbnails of Battlespire .BSI images in Explorer for your account?";
                    if (MessageBox.Show(q, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                    mode = installed ? "uninstall" : "install";
                }

                if (mode == "install" || mode == "i")
                {
                    string dll = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "BsiThumbnail.dll");
                    if (!File.Exists(dll)) throw new FileNotFoundException("BsiThumbnail.dll must be in the same folder as this program.", dll);
                    Install(dll);
                    Report(quiet, "BSI thumbnails are on. Explorer may need a moment, or a restart, to replace thumbnails it has already cached.");
                }
                else if (mode == "uninstall" || mode == "u")
                {
                    Uninstall();
                    Report(quiet, "BSI thumbnails are off.");
                }
                else
                {
                    Report(quiet, "Usage: BsiThumbnailSetup [/install | /uninstall]");
                    return 2;
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (quiet) Console.Error.WriteLine(ex.Message);
                else MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static void Report(bool quiet, string text)
        {
            if (quiet) Console.WriteLine(text);
            else MessageBox.Show(text, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// HKCU\Software\Classes in the native view: on 64 bit Windows a 32 bit process
        /// would otherwise write to Wow6432Node, which 64 bit Explorer never reads.
        /// </summary>
        static RegistryKey OpenClasses()
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            return user.CreateSubKey(@"Software\Classes");
        }

        static bool IsInstalled()
        {
            using (RegistryKey classes = OpenClasses())
            using (RegistryKey shellEx = classes.OpenSubKey(Extension + @"\ShellEx"))
                return shellEx != null && IsOurs(shellEx);
        }

        static void Install(string dll)
        {
            AssemblyName name = AssemblyName.GetAssemblyName(dll);
            string codeBase = new Uri(dll).AbsoluteUri;
            using (RegistryKey classes = OpenClasses())
            {
                using (RegistryKey k = classes.CreateSubKey(@"CLSID\" + Clsid))
                {
                    k.SetValue("", "Battlespire BSI thumbnail handler");
                    // Thumbnail handlers run in a separate dllhost.exe unless this is 1,
                    // so the .NET runtime is never loaded into Explorer itself.
                    k.SetValue("DisableProcessIsolation", 0, RegistryValueKind.DWord);
                    using (RegistryKey pid = k.CreateSubKey("ProgId")) pid.SetValue("", "Battlespire.BsiThumbnail");
                    using (RegistryKey cat = k.CreateSubKey(@"Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}")) { } // .NET Category
                }
                // The same values regasm /codebase would write.
                WriteServer(classes, @"CLSID\" + Clsid + @"\InprocServer32", name, codeBase, true);
                WriteServer(classes, @"CLSID\" + Clsid + @"\InprocServer32\" + name.Version, name, codeBase, false);

                using (RegistryKey k = classes.CreateSubKey(Extension + @"\ShellEx\" + ThumbnailProviderIid))
                    k.SetValue("", Clsid);
            }
            NotifyShell();
        }

        static void WriteServer(RegistryKey classes, string path, AssemblyName name, string codeBase, bool isRoot)
        {
            using (RegistryKey k = classes.CreateSubKey(path))
            {
                if (isRoot)
                {
                    k.SetValue("", "mscoree.dll");
                    k.SetValue("ThreadingModel", "Both");
                }
                k.SetValue("Class", ClassName);
                k.SetValue("Assembly", name.FullName);
                k.SetValue("RuntimeVersion", "v4.0.30319");
                k.SetValue("CodeBase", codeBase);
            }
        }

        static void Uninstall()
        {
            using (RegistryKey classes = OpenClasses())
            {
                using (RegistryKey shellEx = classes.OpenSubKey(Extension + @"\ShellEx", true))
                {
                    if (shellEx != null)
                    {
                        // Leave another program's thumbnail handler alone.
                        if (IsOurs(shellEx)) shellEx.DeleteSubKeyTree(ThumbnailProviderIid, false);
                    }
                }
                classes.DeleteSubKeyTree(@"CLSID\" + Clsid, false);
            }
            NotifyShell();
        }

        static bool IsOurs(RegistryKey shellEx)
        {
            using (RegistryKey k = shellEx.OpenSubKey(ThumbnailProviderIid))
                return k != null && string.Equals(k.GetValue("") as string, Clsid, StringComparison.OrdinalIgnoreCase);
        }

        static void NotifyShell()
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
