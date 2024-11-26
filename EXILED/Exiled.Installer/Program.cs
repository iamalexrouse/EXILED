// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Exiled Team">
// Copyright (c) Exiled Team. All rights reserved.
// Licensed under the CC BY-SA 3.0 license.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using Octokit;

namespace Exiled.Installer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;

    using Exiled.Installer.Properties;

    using ICSharpCode.SharpZipLib.GZip;
    using ICSharpCode.SharpZipLib.Tar;

    using Octokit;

    using Version = SemanticVersioning.Version;

    internal enum PathResolution
    {
        Undefined,

        /// <summary>
        /// Absolute path that is routed to AppData.
        /// </summary>
        Absolute,

        /// <summary>
        /// Exiled path that is routed to exiled root path.
        /// </summary>
        Exiled,
    }

    internal static class Program
    {
        private const long RepoID = 833723500;
        private const string ExiledAssetName = "exiled.tar.gz";

        // This is the lowest version the installer will check to install
        private static readonly Version VersionLimit = new("8.0.0");
        private static readonly uint SecondsWaitForDownload = 480;

        private static readonly string Header = $"{Assembly.GetExecutingAssembly().GetName().Name}-{Assembly.GetExecutingAssembly().GetName().Version}";

        private static readonly GitHubClient GitHubClient = new(new ProductHeaderValue(Header));

        // Force use of LF because the file uses LF
        private static readonly Dictionary<string, string> Markup = Resources.Markup.Trim().Split('\n').ToDictionary(s => s.Split(':')[0], s => s.Split(':', 2)[1]);

        private static async Task Main(string[] args)
        {
            await NewInstaller.Install(new InstallSettings());
            // Console.OutputEncoding = new UTF8Encoding(false, false);
            // await CommandSettings.Parse(args).ConfigureAwait(false);
        }

        internal static async Task MainSafe(CommandSettings args)
        {
            bool error = false;
            try
            {
                Console.WriteLine(Header);

                if (args.GetVersions)
                {
                    IEnumerable<Release> releases1 = await GetReleases().ConfigureAwait(false);
                    Console.WriteLine(Resources.Program_MainSafe_____AVAILABLE_VERSIONS____);
                    foreach (Release r in releases1)
                        Console.WriteLine(FormatRelease(r, true));

                    if (args.Exit)
                        Environment.Exit(0);
                }

                Console.WriteLine(Resources.Program_MainSafe_AppData_folder___0_, args.AppData.FullName);
                Console.WriteLine(Resources.Program_MainSafe_Exiled_folder___0_, args.Exiled.FullName);

                if (args.GitHubToken is not null)
                {
                    Console.WriteLine(Resources.Program_MainSafe_Token_detected__Using_the_token___);
                    GitHubClient.Credentials = new Credentials(args.GitHubToken, AuthenticationType.Bearer);
                }

                Console.WriteLine(Resources.Program_MainSafe_Receiving_releases___);
                Console.WriteLine(Resources.Program_MainSafe_Prereleases_included____0_, args.PreReleases);
                Console.WriteLine(Resources.Program_MainSafe_Target_release_version____0_, string.IsNullOrEmpty(args.TargetVersion) ? "(null)" : args.TargetVersion);

                IEnumerable<Release> releases = await GetReleases().ConfigureAwait(false);
                Console.WriteLine(Resources.Program_MainSafe_Searching_for_the_latest_release_that_matches_the_parameters___);

                Release targetRelease = FindRelease(args, releases);

                Console.WriteLine(Resources.Program_MainSafe_Release_found_);
                Console.WriteLine(FormatRelease(targetRelease!));

                ReleaseAsset? exiledAsset = targetRelease!.Assets.FirstOrDefault(a => a.Name.Equals(ExiledAssetName, StringComparison.OrdinalIgnoreCase));
                if (exiledAsset is null)
                {
                    Console.WriteLine(Resources.Program_MainSafe_____ASSETS____);
                    Console.WriteLine(string.Join(Environment.NewLine, targetRelease.Assets.Select(FormatAsset)));
                    throw new InvalidOperationException("Couldn't find asset");
                }

                Console.WriteLine(Resources.Program_MainSafe_Asset_found_);
                Console.WriteLine(FormatAsset(exiledAsset));

                using HttpClient httpClient = new();
                httpClient.Timeout = TimeSpan.FromSeconds(SecondsWaitForDownload);
                httpClient.DefaultRequestHeaders.Add("User-Agent", Header);

                using HttpResponseMessage downloadResult = await httpClient.GetAsync(exiledAsset.BrowserDownloadUrl).ConfigureAwait(false);
                using Stream downloadArchiveStream = await downloadResult.Content.ReadAsStreamAsync().ConfigureAwait(false);

                using GZipInputStream gzInputStream = new(downloadArchiveStream);
                using TarInputStream tarInputStream = new(gzInputStream, null);

                TarEntry entry;
                while ((entry = tarInputStream.GetNextEntry()) is not null)
                {
                    entry.Name = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                    ProcessTarEntry(args, tarInputStream, entry);
                }

                Console.WriteLine(Resources.Program_MainSafe_Installation_complete);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine(Resources.Program_MainSafe_Read_the_exception_message__read_the_readme__and_if_you_still_don_t_understand_what_to_do__then_contact__support_in_our_discord_server_with_the_attached_screenshot_of_the_full_exception);
                if (!args.Exit)
                    Console.Read();
            }

            if (args.Exit)
                Environment.Exit(error ? 1 : 0);
        }

        private static async Task<IEnumerable<Release>> GetReleases()
        {
            IEnumerable<Release> releases = (await GitHubClient.Repository.Release.GetAll(RepoID).ConfigureAwait(false))
                .Where(
                    r => Version.TryParse(r.TagName, out Version version)
                         && version > VersionLimit);

            return releases.OrderByDescending(r => r.CreatedAt.Ticks);
        }

        private static string FormatRelease(Release r)
            => FormatRelease(r, false);

        private static string FormatRelease(Release r, bool includeAssets)
        {
            StringBuilder builder = new(30);
            builder.AppendLine($"PRE: {r.Prerelease} | ID: {r.Id} | TAG: {r.TagName}");
            if (includeAssets)
            {
                foreach (ReleaseAsset asset in r.Assets)
                    builder.Append("   - ").AppendLine(FormatAsset(asset));
            }

            return builder.ToString().Trim('\r', '\n');
        }

        private static string FormatAsset(ReleaseAsset a) => $"ID: {a.Id} | NAME: {a.Name} | SIZE: {a.Size} | URL: {a.Url} | DownloadURL: {a.BrowserDownloadUrl}";

        private static void ResolvePath(string filePath, string folderPath, out string path) => path = Path.Combine(folderPath, filePath);

        private static void ProcessTarEntry(CommandSettings args, TarInputStream tarInputStream, TarEntry entry)
        {
            if (entry.Name.Contains("global") && args.TargetPort is not null)
            {
                entry.Name = entry.Name.Replace("global", args.TargetPort);
            }

            if (entry.IsDirectory)
            {
                TarEntry[] entries = entry.GetDirectoryEntries();

                for (int z = 0; z < entries.Length; z++)
                    ProcessTarEntry(args, tarInputStream, entries[z]);
            }
            else
            {
                Console.WriteLine(Resources.Program_ProcessTarEntry_Processing___0__, entry.Name);

                if (entry.Name.Contains("example", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(Resources.Program_ProcessTarEntry_Extract_for__0__is_disabled, entry.Name);
                    return;
                }

                switch (ResolveEntry(entry))
                {
                    case PathResolution.Absolute:
                        ResolvePath(entry.Name, args.AppData.FullName, out string path);
                        ExtractEntry(tarInputStream, entry, path);
                        break;
                    case PathResolution.Exiled:
                        ResolvePath(entry.Name, args.Exiled.FullName, out path);
                        ExtractEntry(tarInputStream, entry, path);
                        break;
                    default:
                        Console.WriteLine(Resources.Program_ProcessTarEntry_Couldn_t_resolve_path_for___0____update_installer, entry.Name);
                        break;
                }
            }
        }

        private static void ExtractEntry(TarInputStream tarInputStream, TarEntry entry, string path)
        {
            Console.WriteLine(Resources.Program_ExtractEntry_Extracting___0___into___1_____, Path.GetFileName(entry.Name), path);

            EnsureDirExists(Path.GetDirectoryName(path)!);

            FileStream? fs = null;
            try
            {
                fs = new FileStream(path, System.IO.FileMode.Create, FileAccess.Write, FileShare.None);
                tarInputStream.CopyEntryContents(fs);
            }
            catch (Exception ex)
            {
                Console.WriteLine(Resources.Program_ExtractEntry_An_exception_occurred_while_trying_to_extract_a_file);
                Console.WriteLine(ex);
            }
            finally
            {
                fs?.Dispose();
            }
        }

        private static void EnsureDirExists(string pathToDir)
        {
#if DEBUG
            Console.WriteLine(Resources.Program_EnsureDirExists_Ensuring_directory_path___0_, pathToDir);
            Console.WriteLine(Resources.Program_EnsureDirExists_Does_it_exist_____0_, Directory.Exists(pathToDir));
#endif
            if (!Directory.Exists(pathToDir))
                Directory.CreateDirectory(pathToDir);
        }

        private static PathResolution ResolveEntry(TarEntry entry)
        {
            static PathResolution TryParse(string s)
            {
                // We'll get UNDEFINED if it cannot be determined
                Enum.TryParse(s, true, out PathResolution result);
                return result;
            }

            string fileName = entry.Name;
            bool fileInFolder = !string.IsNullOrEmpty(Path.GetDirectoryName(fileName));
            foreach (KeyValuePair<string, string> pair in Markup)
            {
                bool isFolder = pair.Key.EndsWith('\\');
                if (fileInFolder && isFolder &&
                    pair.Key[0..^1].Equals(fileName.Substring(0, fileName.IndexOf(Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
                {
                    return TryParse(pair.Value);
                }

                if (!fileInFolder && !isFolder &&
                         pair.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return TryParse(pair.Value);
                }
            }

            return PathResolution.Undefined;
        }

        private static Release FindRelease(CommandSettings args, IEnumerable<Release> releases)
        {
            Console.WriteLine(Resources.Program_TryFindRelease_Trying_to_find_release__);
            Version? targetVersion = args.TargetVersion is not null ? new Version(args.TargetVersion) : null;

            List<Release> enumerable = releases.ToList();

            foreach (Release release in enumerable)
            {
                if (targetVersion != null)
                {
                    if (targetVersion == new Version(release.TagName))
                        return release;
                }
                else
                {
                    if (release.Prerelease && !args.PreReleases)
                        continue;

                    return release;
                }
            }

            return enumerable.First();
        }
    }
}

internal static class NewInstaller
{
    /// <summary>
    /// The default GitHub client used to download releases from GitHub.
    /// </summary>
    private static readonly GitHubClient Client = new(new ProductHeaderValue($"{Assembly.GetExecutingAssembly().GetName().Name}-{Assembly.GetExecutingAssembly().GetName().Version}"));
    
    /// <summary>
    /// Checks and installs EXILED from GitHub using the configured settings.
    /// </summary>
    public static async Task Install(InstallSettings settings)
    {
        Console.WriteLine("Preparing to install EXILED...");
        IEnumerable<Release> AvailableReleases = await GetAvailableReleases(settings);

#if DEBUG
        Console.WriteLine("---- RELEASES ----");
        foreach (Release release in AvailableReleases)
            Console.WriteLine($"> '{release.TagName}' (ID: {release.Id}) | CHANNEL: {(release.Prerelease ? "BETA" : "STABLE")}");
#endif
        // Now download the latest release and compare asset hashes
        Release? ReleaseToInstall = AvailableReleases.FirstOrDefault();

        if (ReleaseToInstall == null)
        {
            Console.WriteLine("Unable to install EXILED! An error occured while trying to get the latest release.");
            Environment.Exit(2); // CODE: 2 (No Available Release to install.)
        }

        await DownloadRelease(ReleaseToInstall, settings);
    }

    /// <summary>
    /// Checks and installs EXILED from GitHub using the configured settings.
    /// </summary>
    private static async Task<IEnumerable<Release>> GetAvailableReleases(InstallSettings settings)
    {
        Console.WriteLine($"FEED: {settings.InstallFeed.Owner}/{settings.InstallFeed.Repository}");
        IEnumerable<Release> releases = (await Client.Repository.Release.GetAll(settings.InstallFeed.Owner, settings.InstallFeed.Repository))
            .Where(x => x.Prerelease == false || x.Prerelease == settings.AllowPreReleases);
        return releases.OrderByDescending(r => r.CreatedAt.Ticks);
    }

    private static async Task DownloadRelease(Release targetRelease, InstallSettings settings, bool saveToCache = false)
    {
        Console.WriteLine($"DOWNLOADING RELEASE: '{targetRelease.TagName}' (ID: {targetRelease.Id}) | CHANNEL: {(targetRelease.Prerelease ? "BETA" : "STABLE")}");
        
        HttpClient downloadClient = new()
        {
            Timeout = TimeSpan.FromMinutes(1) // Default to 2 minutes to allow any proxy issues to be resolved.
        };
        downloadClient.DefaultRequestHeaders.Add("User-Agent", settings.InstallFeed.ToString());

        // Locate the appropriately named zip file.
        ReleaseAsset? dataPacket = targetRelease.Assets.FirstOrDefault(a => a.Name.Equals("exiled.tar.gz", StringComparison.OrdinalIgnoreCase));

        if (dataPacket == null)
        {
            Console.WriteLine("Unable to install EXILED! An error occured while trying to get the latest release asset.");
            Environment.Exit(3); // CODE: 3 (No Available Release Asset was found.)
        }

        // Perform the download.
        HttpResponseMessage reponseMessage = await downloadClient.GetAsync(dataPacket.BrowserDownloadUrl);
        Stream downloadStream = await reponseMessage.Content.ReadAsStreamAsync();
        
        Console.WriteLine("EXILED was installed successfully.");
    }

    private static void ProcessTarEntry(TarInputStream tarInputStream, TarEntry entry)
    {
        if (entry.IsDirectory)
        {
            TarEntry[] entries = entry.GetDirectoryEntries();

            for (int z = 0; z < entries.Length; z++)
                ProcessTarEntry(args, tarInputStream, entries[z]);
        }
        else
        {
            if (entry.Name.Contains("example", StringComparison.OrdinalIgnoreCase))
                return;

            switch (ResolveEntry(entry))
            {
                case PathResolution.Absolute:
                    ResolvePath(entry.Name, args.AppData.FullName, out string path);
                    ExtractEntry(tarInputStream, entry, path);
                    break;
                case PathResolution.Exiled:
                    ResolvePath(entry.Name, args.Exiled.FullName, out path);
                    ExtractEntry(tarInputStream, entry, path);
                    break;
                default:
                    break;
            }
        }
    }

    private static void ExtractEntry(TarInputStream tarInputStream, TarEntry entry, string path)
    {
        EnsureDirExists(Path.GetDirectoryName(path)!);

        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, System.IO.FileMode.Create, FileAccess.Write, FileShare.None);
            tarInputStream.CopyEntryContents(fs);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
        finally
        {
            fs?.Dispose();
        }
    }

    private static PathResolution ResolveEntry(TarEntry entry)
    {
        static PathResolution TryParse(string s)
        {
            // We'll get UNDEFINED if it cannot be determined
            Enum.TryParse(s, true, out PathResolution result);
            return result;
        }

        string fileName = entry.Name;
        bool fileInFolder = !string.IsNullOrEmpty(Path.GetDirectoryName(fileName));
        foreach (KeyValuePair<string, string> pair in Markup)
        {
            bool isFolder = pair.Key.EndsWith('\\');
            if (fileInFolder && isFolder &&
                pair.Key[0..^1].Equals(fileName.Substring(0, fileName.IndexOf(Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
                return TryParse(pair.Value);

            if (!fileInFolder && !isFolder && pair.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return TryParse(pair.Value);
        }

        return PathResolution.Undefined;
    }

    private static void EnsureDirExists(string pathToDir)
    {
        if (!Directory.Exists(pathToDir))
            Directory.CreateDirectory(pathToDir);
    }
    private static void ResolvePath(string filePath, string folderPath, out string path) => path = Path.Combine(folderPath, filePath);
}

internal class InstallSettings
{
    /// <summary>
    /// The feed to download releases from. Default is ExMod-Team/EXILED.
    /// </summary>
    public FeedInfo InstallFeed { get; set; } = new();

    /// <summary>
    /// Allows Pre-Releases to be found/downloaded/installed. Default is false.
    /// </summary>
    public bool AllowPreReleases { get; set; } = true;
}

internal record FeedInfo
{
    public string Owner { get; set; } = "ExMod-Team";
    public string Repository { get; set; } = "EXILED";
    public override string ToString() => $"{Owner}/{Repository}";
}

internal enum PathResolution
{
    Undefined,

    /// <summary>
    /// Absolute path that is routed to AppData.
    /// </summary>
    Absolute,

    /// <summary>
    /// Exiled path that is routed to exiled root path.
    /// </summary>
    Exiled,
}