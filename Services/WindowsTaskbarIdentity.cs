using System.Runtime.InteropServices;

namespace Deviso.Services;

public static class WindowsTaskbarIdentity
{
    public const string AppUserModelId = "Deviso.StorageMonitor";
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant variant);

    public static void SetCurrentProcessAppUserModelId()
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
    }

    public static void SetShortcutAppUserModelId(object shortcut)
    {
        var propertyStore = (IPropertyStore)shortcut;
        var key = AppUserModelIdKey;
        var value = PropVariant.FromString(AppUserModelId);
        try
        {
            Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(propertyStore.Commit());
        }
        finally
        {
            Marshal.ThrowExceptionForHR(PropVariantClear(ref value));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = 31,
            PointerValue = Marshal.StringToCoTaskMemUni(value)
        };
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }
}
