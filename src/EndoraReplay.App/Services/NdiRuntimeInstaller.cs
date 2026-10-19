using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EndoraReplay.App.Services;

/// <summary>
/// Downloads the official NDI® Runtime installer from NDI's own link and starts it, so the operator does not have to
/// find it. Endora does not ship the runtime itself; the operator accepts NDI's licence in NDI's installer.
/// The download is only run if Windows confirms it is validly signed by NDI (Vizrt / NewTek).
/// </summary>
public static class NdiRuntimeInstaller
{
    /// <summary>NDI's documented redistributable links, newest first.</summary>
    private static readonly string[] Links = ["https://ndi.link/NDIRedistV6", "https://ndi.link/NDIRedistV5"];

    private static readonly string[] Publishers = ["Vizrt", "NDI", "NewTek"];

    /// <summary>Downloads and launches the installer. Returns null when the installer was started, otherwise why not.</summary>
    public static async Task<string?> DownloadAndRunAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EndoraReplay/" + UpdateService.CurrentVersionText);
        var file = Path.Combine(Path.GetTempPath(), "Endora-NDI-Runtime-Installer.exe");
        string? lastError = null;
        foreach (var link in Links)
        {
            try
            {
                progress.Report($"Downloading the NDI Runtime from {link}…");
                using var resp = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(file))
                    await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                if (CheckSignature(file) is { } bad)
                {
                    File.Delete(file);
                    lastError = bad;
                    continue;
                }
                progress.Report("Starting the NDI Runtime installer. Accept Windows' prompt, finish the installer, then come back here.");
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.WaitForExit();
                Hardware.Ndi.NdiRuntime.Retry();
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or System.ComponentModel.Win32Exception)
            {
                lastError = e.Message;
            }
        }
        return $"Could not install the NDI Runtime ({lastError}). Install NDI Tools from ndi.video instead.";
    }

    private static string? CheckSignature(string file)
    {
        if (!WinTrust.IsValid(file)) return "The downloaded file is not validly signed, so it was not run.";
        string subject;
        try
        {
#pragma warning disable SYSLIB0057
            subject = X509Certificate.CreateFromSignedFile(file).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (Exception) { return "The downloaded file has no signature, so it was not run."; }
        return Publishers.Any(p => subject.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"The downloaded file is signed by \"{subject}\", not NDI, so it was not run.";
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
        private struct Data
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
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref Data data);

        public static bool IsValid(string path)
        {
            var fi = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(),
                    dwUIChoice = 2, // WTD_UI_NONE
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
