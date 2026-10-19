using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ImageViewer;

static class ImageFormats
{
    public static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif",
        ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp",
    ];

    static readonly HashSet<string> Set = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path) => Set.Contains(Path.GetExtension(path));

    public static string DialogFilter =>
        "Imagens|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|Todos os arquivos|*.*";
}

/// <summary>Sorts file names the same way Explorer does ("img2" before "img10").</summary>
sealed class NaturalFileComparer : IComparer<string>
{
    public static readonly NaturalFileComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) =>
        StrCmpLogicalW(Path.GetFileName(x) ?? "", Path.GetFileName(y) ?? "");
}

static class NativeShell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Moves a file to the Recycle Bin. Returns true when the file is gone.</summary>
    public static bool MoveToRecycleBin(string path, IntPtr owner)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    public static void ShowProperties(string path, IntPtr owner) => SHObjectProperties(owner, 0x2, path, null);

    public static void RevealInExplorer(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"");
}

/// <summary>
/// File association registration. The Settings "Default apps" list only shows desktop apps
/// registered under HKLM, so we register machine-wide (one UAC prompt) and fall back to
/// per-user registration when elevation is refused.
/// </summary>
static class Registration
{
    const string ProgId = "ImageViewer.Image";
    const string AppName = "Visualizador de Imagens";
    const string CapabilitiesPath = @"Software\ImageViewer\Capabilities";
    public const string MachineArg = "--register-machine";

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>Stable per-user install location; the registry always points here.</summary>
    public static string InstallExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ImageViewer", "ImageViewer.exe");

    /// <summary>
    /// Copies the running single-file exe to <see cref="InstallExe"/> (so registering from Downloads or a
    /// build folder still points the registry at a stable path) and returns the path to register.
    /// </summary>
    static string Install()
    {
        string current = Environment.ProcessPath!;
        if (string.Equals(current, InstallExe, StringComparison.OrdinalIgnoreCase)) return current;
        // Installed by the MSI: already in a stable place.
        if (current.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            return current;

        // A framework-dependent build output (exe + dll) can't be copied alone.
        bool singleFile = !File.Exists(Path.Combine(Path.GetDirectoryName(current)!, "ImageViewer.dll"));
        if (singleFile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InstallExe)!);
                File.Copy(current, InstallExe, overwrite: true);
                return InstallExe;
            }
            catch { /* installed copy in use: keep it */ }
        }
        return File.Exists(InstallExe) ? InstallExe : current;
    }

    /// <summary>Registers the app and opens its page in Settings. Returns false if only per-user registration was possible.</summary>
    public static bool RegisterAndOpenSettings()
    {
        Log($"register: process={Environment.ProcessPath} elevated={IsElevated}");
        string exe = Install();
        Log($"register: target={exe}");
        bool machine = IsElevated ? TryRegister(Registry.LocalMachine, exe) : RunElevated($"{MachineArg} \"{exe}\"");
        Log($"register: machine={machine}");
        if (!machine) Register(Registry.CurrentUser, exe);
        else RemoveTree(Registry.CurrentUser); // avoid stale per-user duplicates
        NotifyAssocChanged();
        OpenDefaultAppsSettings(machine);
        return machine;
    }

    static bool TryRegister(RegistryKey root, string exe)
    {
        try { Register(root, exe); return true; }
        catch (Exception ex) { Log("register failed: " + ex); return false; }
    }

    static bool RunElevated(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            p!.WaitForExit();
            Log($"elevated child exit={p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log("elevation failed: " + ex.Message); // e.g. UAC cancelled
            return false;
        }
    }

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ImageViewer", "register.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Register(RegistryKey root, string exe)
    {
        string command = $"\"{exe}\" \"%1\"";

        using var classes = root.CreateSubKey(@"Software\Classes");

        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "Arquivo de imagem");
            prog.SetValue("FriendlyTypeName", "Arquivo de imagem");
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue("", @"%SystemRoot%\System32\imageres.dll,-72", RegistryValueKind.ExpandString);
            using (var app = prog.CreateSubKey("Application"))
            {
                app.SetValue("ApplicationName", AppName);
                app.SetValue("ApplicationIcon", $"\"{exe}\",0");
            }
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", command);
        }

        using (var app = classes.CreateSubKey(@"Applications\ImageViewer.exe"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var icon = app.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in ImageFormats.Extensions) types.SetValue(ext, "");
        }

        foreach (var ext in ImageFormats.Extensions)
        {
            // Some extension keys are owned by TrustedInstaller; Capabilities below is what Settings needs.
            try
            {
                using var openWith = classes.CreateSubKey($@"{ext}\OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        using (var appPath = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\ImageViewer.exe"))
            appPath.SetValue("", exe);

        using (var cap = root.CreateSubKey(CapabilitiesPath))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", "Visualizador de imagens leve, no estilo do Windows 11.");
            cap.SetValue("ApplicationIcon", $"\"{exe}\",0");
            using var assoc = cap.CreateSubKey("FileAssociations");
            foreach (var ext in ImageFormats.Extensions) assoc.SetValue(ext, ProgId);
        }

        using (var registered = root.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesPath);
    }

    /// <summary>Removes everything; machine-wide entries need an elevated process.</summary>
    public static void Unregister()
    {
        RemoveTree(Registry.CurrentUser);
        if (IsElevated) RemoveTree(Registry.LocalMachine);
        else RunElevated("--unregister-machine");
        NotifyAssocChanged();
    }

    public static void RemoveTree(RegistryKey root)
    {
        // Each step on its own, so one protected key doesn't leave the rest behind.
        static void Try(Action action) { try { action(); } catch (Exception ex) { Log("cleanup step failed: " + ex.Message); } }
        Log($"cleanup: {root.Name}");

        Try(() => root.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false));
        Try(() => root.DeleteSubKeyTree(@"Software\Classes\Applications\ImageViewer.exe", throwOnMissingSubKey: false));
        foreach (var ext in ImageFormats.Extensions)
            Try(() =>
            {
                using var openWith = root.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            });
        Try(() => root.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\App Paths\ImageViewer.exe", throwOnMissingSubKey: false));
        Try(() => root.DeleteSubKeyTree(@"Software\ImageViewer", throwOnMissingSubKey: false));
        Try(() =>
        {
            using var registered = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        });
    }

    public static void NotifyAssocChanged() =>
        NativeShell.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED

    public static void OpenDefaultAppsSettings(bool machine) =>
        Process.Start(new ProcessStartInfo(
            $"ms-settings:defaultapps?{(machine ? "registeredAppMachine" : "registeredAppUser")}={Uri.EscapeDataString(AppName)}")
            { UseShellExecute = true });
}
