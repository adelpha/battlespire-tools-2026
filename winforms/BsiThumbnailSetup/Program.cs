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
    /// for the current user only, so no administrator rights are needed. The DLL is
    /// copied to %LOCALAPPDATA%\BattlespireTools first, so the build folder can be
    /// moved, cleaned or rebuilt without breaking thumbnails.
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
                    if (IsInstalled())
                    {
                        DialogResult r = MessageBox.Show(
                            "BSI thumbnails are turned on for your account.\n\n" +
                            "Yes: update them from this folder.\nNo: turn them off.\nCancel: leave them as they are.",
                            Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (r == DialogResult.Cancel) return 0;
                        mode = r == DialogResult.Yes ? "install" : "uninstall";
                    }
                    else
                    {
                        if (MessageBox.Show("Show thumbnails of Battlespire .BSI images in Explorer for your account?",
                            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                        mode = "install";
                    }
                }

                if (mode == "install" || mode == "i")
                {
                    string dll = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "BsiThumbnail.dll");
                    if (!File.Exists(dll)) throw new FileNotFoundException("BsiThumbnail.dll must be in the same folder as this program.", dll);
                    Install(InstallCopy(dll));
                    Report(quiet, "BSI thumbnails are on. Explorer may need a moment, or a restart, to replace thumbnails it has already cached.");
                }
                else if (mode == "uninstall" || mode == "u")
                {
                    Uninstall();
                    Report(quiet, "BSI thumbnails are off. If Explorer still had the handler open, its copy in " + InstallFolder + " is removed next time.");
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

        /// <summary>Where the registered copy of the handler lives.</summary>
        static string InstallFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BattlespireTools"); }
        }

        /// <summary>
        /// Copies the handler to the install folder and returns the copy's path.
        /// Explorer's dllhost.exe may still have an older copy open, in which case it
        /// cannot be overwritten; the new copy then gets a unique name and the old
        /// one is removed by a later install or uninstall once it is no longer in use.
        /// </summary>
        static string InstallCopy(string dll)
        {
            string folder = InstallFolder;
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, "BsiThumbnail.dll");
            try
            {
                File.Copy(dll, target, true);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                target = Path.Combine(folder, "BsiThumbnail-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".dll");
                File.Copy(dll, target, true);
            }
            DeleteOldCopies(target);
            return target;
        }

        /// <summary>Deletes copies other than <paramref name="keep"/>, skipping any still in use.</summary>
        static void DeleteOldCopies(string keep)
        {
            string folder = InstallFolder;
            if (!Directory.Exists(folder)) return;
            foreach (string f in Directory.GetFiles(folder, "BsiThumbnail*.dll"))
            {
                if (keep != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (keep == null)
            {
                try { if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
            DeleteOldCopies(null);
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
