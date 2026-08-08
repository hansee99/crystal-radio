using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RadioPlayer.Services;

/// <summary>
/// Makes sure the current user's Start Menu holds a shortcut tagged with this app's
/// AppUserModelID, creating or repairing it at startup.
///
/// <para><b>Why the app does this rather than the installer.</b> Once a process calls
/// <c>SetCurrentProcessExplicitAppUserModelID</c>, the shell stops treating it as an anonymous
/// Win32 window: the taskbar button's icon and grouping, and toast attribution, are all resolved
/// from a Start Menu shortcut carrying that same ID — and it looks in the <b>current user's</b>
/// Start Menu. The installer writes a per-machine shortcut (<c>{group}</c> under an admin
/// install, so <c>%ProgramData%</c>), which satisfies nobody's per-user lookup. The visible
/// symptom was a generic application icon in the taskbar for installed copies; toasts were
/// silently missing for the same reason. Only the app itself can do this once per user of a
/// machine-wide install, so it belongs here.</para>
///
/// <para>Best-effort throughout: every failure is logged and swallowed. A missing shortcut costs
/// the icon and toasts, never playback.</para>
/// </summary>
internal static class StartMenuShortcut
{
    private const string ShortcutName = "Crystal Radio.lnk";

    /// <summary>
    /// Creates the shortcut if it is missing, and rewrites it if it has lost its
    /// AppUserModelID or points at an executable that no longer exists.
    ///
    /// <para>A shortcut that is already correct is left completely alone — deliberately, so that
    /// running a development build does not repoint the installed copy's shortcut at
    /// <c>bin\</c> (and vice versa). Whichever valid one exists keeps working for both.</para>
    ///
    /// <para>Call once at startup, after
    /// <see cref="WindowsNotificationService.ApplyAppIdentity"/> and before the first window.
    /// On the very first launch after an install the shell may already have resolved the taskbar
    /// button before the new shortcut is indexed, so that one launch can still show the generic
    /// icon; every later one is correct.</para>
    /// </summary>
    public static void Ensure()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                AppLog.Debug("[Shortcut] no executable path; skipping");
                return;
            }

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

            if (IsUsable(path))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Create(path, exe, WindowsNotificationService.AppUserModelId);

            // Tell the shell a shortcut appeared instead of waiting for it to notice.
            SHChangeNotify(SHCNE_CREATE, SHCNF_PATH_W, Marshal.StringToCoTaskMemUni(path), IntPtr.Zero);

            AppLog.Info($"[Shortcut] wrote Start Menu shortcut -> {exe}");
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Shortcut] couldn't ensure the Start Menu shortcut: {ex.Message}");
        }
    }

    /// <summary>
    /// True when an existing shortcut still does its job: it carries our AppUserModelID (without
    /// which it is invisible to the lookup that matters) and its target is still on disk (an
    /// uninstalled or moved build would otherwise leave a shortcut that resolves to nothing).
    /// </summary>
    private static bool IsUsable(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(path, 0);

            if (!string.Equals(ReadAppUserModelId(link), WindowsNotificationService.AppUserModelId,
                    StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("[Shortcut] existing shortcut has no matching AppUserModelID; rewriting");
                return false;
            }

            var target = new StringBuilder(260);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);

            if (target.Length == 0 || !File.Exists(target.ToString()))
            {
                AppLog.Debug("[Shortcut] existing shortcut points at a missing file; rewriting");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Unreadable is as good as wrong — fall through and replace it.
            AppLog.Debug($"[Shortcut] couldn't read the existing shortcut: {ex.Message}");
            return false;
        }
    }

    private static string? ReadAppUserModelId(IShellLinkW link)
    {
        var key = PkeyAppUserModelId;
        var value = default(PropVariant);
        try
        {
            ((IPropertyStore)link).GetValue(ref key, out value);
            return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Ptr) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static void Create(string path, string target, string appId)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription("Crystal Radio");

        var key = PkeyAppUserModelId;
        var value = new PropVariant { Type = VT_LPWSTR, Ptr = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            var store = (IPropertyStore)link;
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }

        ((IPersistFile)link).Save(path, true);
    }

    // --- interop ---------------------------------------------------------------------------
    // WScript.Shell can create a .lnk but cannot set a property on it, and the property is the
    // entire point here — so this goes through IShellLink + IPropertyStore directly. Mirrors the
    // inline C# in scripts/build-release.ps1.

    private const ushort VT_LPWSTR = 31;
    private const uint SHCNE_CREATE = 0x00000002;
    private const uint SHCNF_PATH_W = 0x0005;

    private static PropertyKey PkeyAppUserModelId => new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int chars, IntPtr data, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int chars);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int chars);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int chars);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int chars, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>
    /// Only the two fields this code touches are named, but the declared size must match the real
    /// PROPVARIANT (24 bytes on x64) — <c>GetValue</c> writes the whole struct, so a short one
    /// would let COM scribble past it.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Ptr;
    }
}
