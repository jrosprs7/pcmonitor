using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TaskbarMonitor;

/// <summary>
/// Self-update from GitHub Releases. Only builds made by the release workflow update themselves:
/// it stamps the name of the matching release file (standalone or small) into the exe. Local and CI
/// builds have no stamp, so they never replace themselves.
///
/// Update steps (the running exe is never renamed or touched while it runs: the app is a single-file
/// bundle that loads parts of itself from its own file on demand):
///   1. Download the new exe next to the running one as TaskbarMonitor.update.exe and check it.
///   2. Start it with "--install-update &lt;pid&gt; &lt;path&gt;", then shut down normally.
///   3. The installer waits for the old copy to exit, copies itself over TaskbarMonitor.exe,
///      starts it with --updated, and exits.
///   4. The new copy deletes TaskbarMonitor.update.exe on start-up.
/// </summary>
internal static class Updater
{
    public const string Repo = "jrosprs7/pcmonitor";

    public enum Status { UpToDate, Updated, Failed, NotAvailable }
    public sealed record Result(Status Status, string Message, Version? Latest = null);

    private static string? _pendingInstaller; // downloaded, checked update waiting to be installed

    public static Version Current => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>Release file this build updates from, e.g. "TaskbarMonitor.exe"; null for dev builds.</summary>
    public static string? AssetName =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateAsset")?.Value is { Length: > 0 } name ? name : null;

    public static bool IsReleaseBuild => AssetName is not null;

    private static string? _versionText;
    public static string VersionText => _versionText ??= $"{Current.ToString(3)}{(IsReleaseBuild ? "" : " (dev build)")}";

    private static string UpdateFileFor(string exe) =>
        Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".update.exe");

    /// <summary>Checks for a newer release and, if there is one, downloads and verifies it.
    /// Status.Updated means it is ready: call <see cref="StartInstaller"/> and exit.</summary>
    public static async Task<Result> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        string? asset = AssetName;
        if (asset is null)
            return new(Status.NotAvailable, "This is a development build. Automatic updates only work in copies downloaded from the Releases page.");

        string? exe = Environment.ProcessPath;
        if (exe is null) return new(Status.Failed, "Couldn't find the app's own file.");

        // Test hook (used by CI): treat a local exe as the latest release, to test the full update
        // hand-over without publishing anything.
        if (Environment.GetEnvironmentVariable("TASKBARMONITOR_TEST_UPDATE_FILE") is { Length: > 0 } testFile)
        {
            try
            {
                var testVersion = Normalize(Version.Parse(FileVersionInfo.GetVersionInfo(testFile).FileVersion ?? "0.0"));
                if (testVersion <= Current) return new(Status.UpToDate, $"You have the latest version ({Current.ToString(3)}).", testVersion);
                File.Copy(testFile, UpdateFileFor(exe), overwrite: true);
                _pendingInstaller = UpdateFileFor(exe);
                return new(Status.Updated, $"Version {testVersion.ToString(3)} is ready.", testVersion);
            }
            catch (Exception ex) { return new(Status.Failed, "Test update failed: " + ex.Message); }
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TaskbarMonitor", Current.ToString(3)));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode)
                return new(Status.Failed, $"Couldn't reach GitHub (HTTP {(int)resp.StatusCode}).");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
                return new(Status.Failed, $"Unrecognised release version \"{tag}\".");
            var latest = Normalize(parsed);
            if (latest <= Current)
                return new(Status.UpToDate, $"You have the latest version ({Current.ToString(3)}).", latest);

            // Find the file matching this build (standalone or small).
            string? url = null, sha256 = null;
            long size = -1;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != asset) continue;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var d) && d.GetString() is string digest &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest[7..];
            }
            if (url is null || !url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal))
                return new(Status.Failed, $"Version {latest.ToString(3)} has no {asset} download.");

            string newPath = UpdateFileFor(exe);
            try
            {
                await using (var file = File.Create(newPath))
                {
                    using var dl = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    dl.EnsureSuccessStatusCode();
                    await dl.Content.CopyToAsync(file, ct);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new(Status.Failed,
                    $"No permission to update files in {Path.GetDirectoryName(exe)}. " +
                    @"Move TaskbarMonitor.exe to a folder you own, e.g. %LOCALAPPDATA%\TaskbarMonitor.");
            }

            string? problem = Verify(newPath, size, sha256);
            if (problem is not null)
            {
                TryDelete(newPath);
                return new(Status.Failed, $"The downloaded update failed a safety check ({problem}), so it wasn't installed.");
            }

            _pendingInstaller = newPath;
            return new(Status.Updated, $"Version {latest.ToString(3)} is ready.", latest);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(Status.Failed, "GitHub took too long to respond.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            TryDelete(UpdateFileFor(exe));
            return new(Status.Failed, "Update failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Starts the downloaded installer. The caller must exit right after. As a safety net, this process
    /// ends itself if it is still running 10 seconds later (the installer would end it after 20 s anyway).
    /// </summary>
    public static void StartInstaller()
    {
        if (_pendingInstaller is not string installer || Environment.ProcessPath is not string exe) return;
        Log.Write("Starting the installer and exiting");
        Process.Start(new ProcessStartInfo(installer, $"--install-update {Environment.ProcessId} \"{exe}\"")
        {
            UseShellExecute = false,
        })?.Dispose();
        new Thread(() =>
        {
            Thread.Sleep(10000);
            Log.Write("Still running 10 s after starting the installer; forcing exit");
            Process.GetCurrentProcess().Kill();
        }) { IsBackground = true, Name = "UpdateExitGuard" }.Start();
    }

    /// <summary>Installer mode: runs from TaskbarMonitor.update.exe. Replaces the app and restarts it.</summary>
    public static void RunInstaller(int oldPid, string target)
    {
        Log.Write($"Installer: waiting for the old copy (pid {oldPid}) to exit");
        try
        {
            using var old = Process.GetProcessById(oldPid);
            if (!old.WaitForExit(20000))
            {
                Log.Write("Installer: the old copy didn't exit; ending it");
                old.Kill();
                old.WaitForExit(5000);
            }
        }
        catch (ArgumentException) { /* already exited */ }
        catch (Exception ex) { Log.Write("Installer: " + ex.Message); }

        string self = Environment.ProcessPath!;
        bool copied = false;
        for (int attempt = 1; attempt <= 30 && !copied; attempt++) // the file can stay locked briefly (e.g. antivirus)
        {
            try
            {
                File.Copy(self, target, overwrite: true);
                copied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 30) Log.Write("Installer: couldn't replace the app: " + ex.Message);
                Thread.Sleep(1000);
            }
        }

        Log.Write(copied ? "Installer: installed; starting the new version" : "Installer: restarting the current version");
        Process.Start(new ProcessStartInfo(target, copied ? "--updated" : "") { UseShellExecute = false })?.Dispose();
    }

    /// <summary>
    /// Remove leftovers from a previous update (the installer, plus *.old / *.new from versions 1.0.0-1.0.2).
    /// Runs on every start-up and keeps retrying for a few minutes, as files can stay locked for a while.
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        if (Environment.ProcessPath is not string exe) return;
        string[] leftovers = { UpdateFileFor(exe), exe + ".old", exe + ".new" };
        if (!leftovers.Any(File.Exists)) return;
        Task.Run(async () =>
        {
            for (int i = 0; i < 90 && leftovers.Any(File.Exists); i++)
            {
                foreach (var f in leftovers) TryDelete(f);
                await Task.Delay(2000);
            }
        });
    }

    private static string? Verify(string path, long expectedSize, string? expectedSha256)
    {
        var info = new FileInfo(path);
        if (expectedSize >= 0 && info.Length != expectedSize) return "size mismatch";
        using var fs = File.OpenRead(path);
        if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return "not a Windows program";
        if (expectedSha256 is not null)
        {
            fs.Position = 0;
            string actual = Convert.ToHexString(SHA256.HashData(fs));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return "checksum mismatch";
        }
        return null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
