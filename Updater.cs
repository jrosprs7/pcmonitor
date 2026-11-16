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
/// Update steps: download the new exe next to the running one (*.new), check it, rename the running exe
/// to *.old (Windows allows renaming a running exe), move the new one into place, start it with
/// --updated (it waits for this copy to exit), and exit. The new copy deletes *.old on start-up.
/// </summary>
internal static class Updater
{
    public const string Repo = "jrosprs7/pcmonitor";

    public enum Status { UpToDate, Updated, Failed, NotAvailable }
    public sealed record Result(Status Status, string Message, Version? Latest = null);

    public static Version Current => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>Release file this build updates from, e.g. "TaskbarMonitor.exe"; null for dev builds.</summary>
    public static string? AssetName =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateAsset")?.Value is { Length: > 0 } name ? name : null;

    public static bool IsReleaseBuild => AssetName is not null;

    public static string VersionText => $"{Current.ToString(3)}{(IsReleaseBuild ? "" : " (dev build)")}";

    public static async Task<Result> CheckAndInstallAsync(CancellationToken ct = default)
    {
        string? asset = AssetName;
        if (asset is null)
            return new(Status.NotAvailable, "This is a development build. Automatic updates only work in copies downloaded from the Releases page.");

        string? exe = Environment.ProcessPath;
        if (exe is null) return new(Status.Failed, "Couldn't find the app's own file.");

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

            string newPath = exe + ".new", oldPath = exe + ".old";
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

            // Swap: running exe -> .old, new -> exe (roll back if the second move fails).
            TryDelete(oldPath);
            File.Move(exe, oldPath);
            try { File.Move(newPath, exe); }
            catch
            {
                File.Move(oldPath, exe);
                throw;
            }
            return new(Status.Updated, $"Updated to version {latest.ToString(3)}.", latest);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(Status.Failed, "GitHub took too long to respond.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            TryDelete(exe + ".new");
            return new(Status.Failed, "Update failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Start the freshly installed copy (it waits for this one to exit). The caller should exit right after.
    /// As a safety net, this process ends itself if it is still running 8 seconds later.
    /// </summary>
    public static void StartNewVersion()
    {
        if (Environment.ProcessPath is not string exe) return;
        Log.Write("Starting the new version and exiting");
        Process.Start(new ProcessStartInfo(exe, $"--updated {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
        new Thread(() =>
        {
            Thread.Sleep(8000);
            Log.Write("Still running 8 s after the update; forcing exit");
            Process.GetCurrentProcess().Kill();
        }) { IsBackground = true, Name = "UpdateExitGuard" }.Start();
    }

    /// <summary>
    /// Remove leftovers from a previous update. Runs on every start-up. Right after an update the old exe
    /// can stay locked for a while (the old process is still shutting down, or antivirus is scanning it),
    /// so keep retrying for a few minutes.
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        if (Environment.ProcessPath is not string exe) return;
        if (!File.Exists(exe + ".old") && !File.Exists(exe + ".new")) return;
        Task.Run(async () =>
        {
            for (int i = 0; i < 90 && (File.Exists(exe + ".old") || File.Exists(exe + ".new")); i++)
            {
                TryDelete(exe + ".old");
                TryDelete(exe + ".new");
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
