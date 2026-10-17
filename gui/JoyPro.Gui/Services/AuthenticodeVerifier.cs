using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace JoyPro.Gui.Services;

// The result of an Authenticode check: a valid signature (WinVerifyTrust: unchanged file, trusted chain, expired
// certificates accepted only with a timestamp) and the signer's organization (O= of the signing certificate).
public sealed record SignatureCheck(bool Valid, string Organization, string Subject, string Problem)
{
    public static SignatureCheck Invalid(string problem) => new(false, string.Empty, string.Empty, problem);
}

public interface ISignatureVerifier
{
    SignatureCheck Verify(string file);
}

// Windows' own signature check of a downloaded installer before JoyPro runs it.
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    public SignatureCheck Verify(string file)
    {
        if (!WinTrust.Verify(file))
            return SignatureCheck.Invalid("The file " + System.IO.Path.GetFileName(file) + " has no valid signature.");
        try
        {
#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            string subject = certificate.Subject;
            return new SignatureCheck(true, OrganizationOf(subject), subject, string.Empty);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return SignatureCheck.Invalid("The signature of " + System.IO.Path.GetFileName(file) + " could not be read: " + ex.Message);
        }
    }

    // O= of a distinguished name ("CN=x, O=Nefarius Software Solutions e.U., L=Wels" -> "Nefarius Software Solutions e.U.").
    // Values with commas are quoted ("O=\"DigiCert, Inc.\"").
    public static string OrganizationOf(string subject)
    {
        foreach (X500RelativeDistinguishedName part in new X500DistinguishedName(subject).EnumerateRelativeDistinguishedNames())
        {
            if (part.GetSingleElementType().Value == "2.5.4.10")
                return part.GetSingleElementValue() ?? string.Empty;
        }
        return string.Empty;
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        public static bool Verify(string path)
        {
            FileInfo info = new() { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                TrustData data = new()
                {
                    cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = infoPtr,
                    dwStateAction = 0,
                    dwProvFlags = 0x00000010 // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                Marshal.StructureToPtr(data, dataPtr, false);
                return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr) == 0;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(infoPtr);
                Marshal.FreeHGlobal(infoPtr);
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }
}
