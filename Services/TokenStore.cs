using autodraw_plugin.Models.Auth;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace autodraw_plugin.Services;

/// <summary>
/// The login kept between AutoCAD runs - the refresh token and whose it is -
/// encrypted with Windows DPAPI, so only this Windows user on this machine can
/// read it back. The password is never kept.
/// </summary>
internal static class TokenStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "autodraw", "session.bin");

    /// <summary>The saved session, or null if there is none or it cannot be read.</summary>
    public static SavedSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string json = Encoding.UTF8.GetString(Crypt(File.ReadAllBytes(FilePath), protect: false));
            return JsonConvert.DeserializeObject<SavedSession>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SavedSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(session));
        File.WriteAllBytes(FilePath, Crypt(json, protect: true));
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    // CryptProtectData in the current user's scope. This is what
    // System.Security.Cryptography.ProtectedData wraps, called directly
    // because AutoCAD does not ship that assembly and NETLOAD would not find it.
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        var input = new DataBlob { cbData = data.Length, pbData = pinned.AddrOfPinnedObject() };
        var output = new DataBlob();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            pinned.Free();
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
