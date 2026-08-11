using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HydraX.Library
{
    /// <summary>
    /// Checks GitHub for a newer release and, if the user wants it, replaces the
    /// installed binaries with it.
    /// </summary>
    /// <remarks>
    /// A running executable cannot overwrite itself, so the swap is done by a
    /// batch file: it waits for this process to exit, backs up the files it is
    /// about to replace, copies the new ones in, restores the backup if any copy
    /// fails, and relaunches. Everything the release ships is installed,
    /// including the hash dictionaries, which grow with every release. Files the
    /// user put there themselves are never in the payload, so they are never
    /// touched.
    /// </remarks>
    public static class FiggleUpdater
    {
        /// <summary>
        /// The repository releases are published to
        /// </summary>
        public const string Owner = "Aidan601";
        public const string Repo = "FiggleFX";

        /// <summary>
        /// Names the updater never installs, relative to the payload root. These
        /// are the user's own state, written next to the exe at runtime: a
        /// release should never carry them, and if one ever does by mistake it
        /// must not overwrite what the user has.
        /// </summary>
        private static readonly string[] Excluded = { "Settings.json", "Log.txt", "exported_files" };

        /// <summary>
        /// The folder a release ships its hash dictionaries in.
        /// </summary>
        /// <remarks>
        /// NOT "hashes", deliberately, and this must not change: the updater
        /// that installs a release is the OLD build's, and every build up to
        /// 1.1.1 skipped a top-level "hashes" folder in the payload, so a
        /// release shipping them under that name can never deliver them to the
        /// users who need them most. Under any other name they install like any
        /// other file, and <see cref="InstallShippedHashes"/> moves them into
        /// hashes\ on the next start.
        /// </remarks>
        public const string HashPayloadFolder = "hashes_update";

        /// <summary>
        /// Moves the dictionaries a release shipped in
        /// <see cref="HashPayloadFolder"/> into hashes\, replacing the shipped
        /// files of the same name and leaving any the user added alone, then
        /// removes the folder.
        /// </summary>
        /// <returns>How many files were installed; 0 when there was nothing to do</returns>
        public static int InstallShippedHashes()
        {
            var install = AppDomain.CurrentDomain.BaseDirectory;
            var source = Path.Combine(install, HashPayloadFolder);

            if (!Directory.Exists(source))
                return 0;

            var target = Path.Combine(install, "hashes");
            Directory.CreateDirectory(target);

            int installed = 0;
            bool complete = true;

            // Flat on purpose: a dictionary folder has no subfolders, and the
            // loader only reads the top level of it either way
            foreach (var file in Directory.GetFiles(source))
            {
                try
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                    File.Delete(file);
                    installed++;
                }
                catch
                {
                    // Read-only install, file in use: leave the folder behind so
                    // the loader can still read it and the next start can retry
                    complete = false;
                }
            }

            if (complete)
            {
                try
                {
                    Directory.Delete(source, true);
                }
                catch { }
            }

            return installed;
        }

        /// <summary>
        /// Gets the page to send the user to when an automatic update isn't possible
        /// </summary>
        public static string ReleasesPage
        {
            get { return string.Format("https://github.com/{0}/{1}/releases/latest", Owner, Repo); }
        }

        /// <summary>
        /// A published release newer than the running build
        /// </summary>
        public class Release
        {
            public Version Version { get; set; }
            public string Tag { get; set; }
            public string ZipUrl { get; set; }
        }

        /// <summary>
        /// Asks GitHub for the latest release
        /// </summary>
        /// <param name="current">Version of the running build</param>
        /// <returns>The release if it is newer than <paramref name="current"/>, else null</returns>
        public static Release CheckForUpdate(Version current)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string json;

                using (var client = new WebClient())
                {
                    // GitHub rejects requests without one
                    client.Headers["User-Agent"] = Repo;
                    client.Headers["Accept"] = "application/vnd.github+json";
                    json = client.DownloadString(string.Format(
                        "https://api.github.com/repos/{0}/{1}/releases/latest", Owner, Repo));
                }

                var tag = Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");

                if (string.IsNullOrEmpty(tag))
                    return null;

                var version = ParseVersion(tag);

                if (version == null || version <= Normalize(current))
                    return null;

                return new Release
                {
                    Version = version,
                    Tag = tag,
                    ZipUrl = Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\""),
                };
            }
            catch
            {
                // No network, rate limited, no releases yet: never a reason to
                // bother the user
                return null;
            }
        }

        /// <summary>
        /// Downloads a release zip and extracts it, returning the folder inside
        /// it that holds the new binaries
        /// </summary>
        /// <param name="progress">Called with 0-100 as the download runs</param>
        public static string Fetch(Release release, Action<int> progress)
        {
            var staging = Path.Combine(Path.GetTempPath(), Repo + "_update_" + release.Version);

            if (Directory.Exists(staging))
                Directory.Delete(staging, true);

            Directory.CreateDirectory(staging);

            var zip = Path.Combine(staging, "release.zip");

            using (var client = new WebClient())
            {
                client.Headers["User-Agent"] = Repo;

                if (progress != null)
                    client.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);

                // DownloadFile is synchronous; the caller is already off the UI thread
                client.DownloadFile(release.ZipUrl, zip);
            }

            var payload = Path.Combine(staging, "payload");
            ZipFile.ExtractToDirectory(zip, payload);

            return FindPayloadRoot(payload);
        }

        /// <summary>
        /// Writes and starts the batch file that swaps the binaries in and
        /// relaunches. The caller must shut the application down straight after.
        /// </summary>
        public static void ApplyAndRestart(string payloadRoot)
        {
            var install = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var exe = Path.Combine(install, Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName));

            payloadRoot = Path.GetFullPath(payloadRoot).TrimEnd('\\');

            // The script deletes its own working folder when it is done, so both
            // it and the backup live in temp, never anywhere near the install
            if (Contains(install, payloadRoot))
                throw new InvalidOperationException("The update was extracted inside the install folder.");

            var work = Path.Combine(Path.GetTempPath(), Repo + "_apply");

            if (Directory.Exists(work))
                Directory.Delete(work, true);

            var backup = Path.Combine(work, "backup");
            Directory.CreateDirectory(backup);

            var files = PayloadFiles(payloadRoot);

            if (files.Count == 0)
                throw new InvalidOperationException("The downloaded update contains no files to install.");

            // Only ever clean up our own temp folders
            var cleanup = new List<string> { work };

            if (Contains(Path.GetTempPath().TrimEnd('\\'), payloadRoot))
                cleanup.Add(payloadRoot);

            var script = Path.Combine(work, "apply.bat");
            File.WriteAllText(script, BuildScript(files, payloadRoot, install, backup, exe, cleanup), Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        /// <summary>
        /// Builds the swap script: wait for exit, back up, copy, roll back on any
        /// failure, relaunch, delete itself
        /// </summary>
        private static string BuildScript(List<string> files, string payload, string install, string backup, string exe, List<string> cleanup)
        {
            var sb = new StringBuilder();

            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");

            // Wait for this process to let go of its files
            sb.AppendLine(":wait");
            sb.AppendFormat("tasklist /fi \"PID eq {0}\" /nh | findstr /i /c:\"{1}\" >nul\r\n",
                Process.GetCurrentProcess().Id, Path.GetFileName(exe));
            sb.AppendLine("if %errorlevel%==0 (");
            sb.AppendLine("    ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("    goto wait");
            sb.AppendLine(")");

            foreach (var file in files)
            {
                var target = Path.Combine(install, file);
                var saved = Path.Combine(backup, file);

                sb.AppendFormat("if exist \"{0}\" (\r\n", target);
                sb.AppendFormat("    if not exist \"{0}\" mkdir \"{0}\"\r\n", Path.GetDirectoryName(saved));
                sb.AppendFormat("    copy /y \"{0}\" \"{1}\" >nul || goto rollback\r\n", target, saved);
                sb.AppendLine(")");
            }

            foreach (var file in files)
            {
                var target = Path.Combine(install, file);

                sb.AppendFormat("if not exist \"{0}\" mkdir \"{0}\"\r\n", Path.GetDirectoryName(target));
                sb.AppendFormat("copy /y \"{0}\" \"{1}\" >nul || goto rollback\r\n", Path.Combine(payload, file), target);
            }

            sb.AppendLine("goto done");

            sb.AppendLine(":rollback");
            foreach (var file in files)
            {
                var target = Path.Combine(install, file);
                var saved = Path.Combine(backup, file);

                sb.AppendFormat("if exist \"{0}\" copy /y \"{0}\" \"{1}\" >nul\r\n", saved, target);
            }

            sb.AppendLine(":done");
            sb.AppendFormat("start \"\" \"{0}\"\r\n", exe);

            foreach (var directory in cleanup)
                sb.AppendFormat("rmdir /s /q \"{0}\" >nul 2>&1\r\n", directory);

            // Ends the batch before deleting itself, which cmd allows
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");

            return sb.ToString();
        }

        /// <summary>
        /// Whether <paramref name="path"/> is <paramref name="parent"/> or sits inside it
        /// </summary>
        private static bool Contains(string parent, string path)
        {
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The files an update installs, relative to the payload root
        /// </summary>
        private static List<string> PayloadFiles(string payloadRoot)
        {
            var results = new List<string>();

            foreach (var file in Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(payloadRoot.Length).TrimStart('\\');
                var top = relative.Split('\\')[0];
                bool skip = false;

                foreach (var excluded in Excluded)
                    if (string.Equals(top, excluded, StringComparison.OrdinalIgnoreCase))
                        skip = true;

                if (!skip)
                    results.Add(relative);
            }

            return results;
        }

        /// <summary>
        /// Finds the folder in an extracted release that actually holds the
        /// binaries, since the zip may or may not wrap them in a version folder
        /// </summary>
        private static string FindPayloadRoot(string extracted)
        {
            var exe = Repo + ".exe";

            if (File.Exists(Path.Combine(extracted, exe)))
                return extracted;

            foreach (var directory in Directory.GetDirectories(extracted))
                if (File.Exists(Path.Combine(directory, exe)))
                    return directory;

            throw new InvalidOperationException("The downloaded update does not contain " + exe + ".");
        }

        /// <summary>
        /// Reads a version out of a release tag ("v1.2.0", "1.2.0")
        /// </summary>
        private static Version ParseVersion(string tag)
        {
            var text = Match(tag, "([0-9]+(?:\\.[0-9]+)*)");

            Version version;
            return !string.IsNullOrEmpty(text) && Version.TryParse(Pad(text), out version) ? Normalize(version) : null;
        }

        /// <summary>
        /// Version.TryParse needs at least major.minor
        /// </summary>
        private static string Pad(string text)
        {
            return text.Contains(".") ? text : text + ".0";
        }

        /// <summary>
        /// Drops unset build/revision so "1.1" and "1.1.0.0" compare equal
        /// </summary>
        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor,
                Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        }

        private static string Match(string input, string pattern)
        {
            var match = Regex.Match(input, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
