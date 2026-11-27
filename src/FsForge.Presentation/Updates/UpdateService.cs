namespace FsForge.Presentation.Updates;

/// <summary>
/// FsForge releases on GitHub: which file of a release this copy of the program is updated from, where it
/// is downloaded to, and the calls to <see cref="GitHubUpdater"/>. No user interface here; see
/// <see cref="ViewModels.MainViewModel"/> for the questions and the progress.
/// </summary>
public sealed class UpdateService
{
    public const string Owner = "alexsys87";
    public const string Repository = "FsForge";

    private readonly HttpClient _http;
    private readonly Uri? _apiBaseUrl;

    /// <param name="assetName">Name of the release file this build is made from (see <see cref="Detect"/>).</param>
    /// <param name="isInstaller">The file is the MSI installer, which can be started to replace this copy.</param>
    /// <param name="apiBaseUrl">GitHub API root; null for api.github.com (tests pass their own).</param>
    public UpdateService(HttpClient http, Version currentVersion, string assetName, bool isInstaller, string downloadFolder, Uri? apiBaseUrl = null)
    {
        _http = http;
        CurrentVersion = currentVersion;
        AssetName = assetName;
        IsInstaller = isInstaller;
        DownloadFolder = downloadFolder;
        _apiBaseUrl = apiBaseUrl;
    }

    public Version CurrentVersion { get; }
    public string AssetName { get; }
    public bool IsInstaller { get; }
    public string DownloadFolder { get; }

    public static Uri ReleasesPage => new($"https://github.com/{Owner}/{Repository}/releases");

    /// <summary>The service for the running program: its version, the kind of build and the Downloads folder.</summary>
    public static UpdateService ForThisProcess(HttpClient http)
    {
        (string asset, bool installer) = Detect();
        Version version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        return new UpdateService(http, version, asset, installer, DefaultDownloadFolder());
    }

    /// <summary>
    /// Which of the published files this copy was made from. The release holds, for each .NET version N:
    /// <c>FsForge-netN-win-x64.msi</c> (installs the single exe into "Program Files\FsForge-netN"),
    /// <c>FsForge-netN-win-x64-standalone.zip</c> (the single exe) and <c>FsForge-netN-win-x64.zip</c>
    /// (the framework-dependent folder).
    /// </summary>
    public static (string AssetName, bool IsInstaller) Detect()
    {
        string runtime = $"net{Environment.Version.Major}";
        // A single-file application has no assembly location on disk.
#pragma warning disable IL3000
        bool singleFile = string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);
#pragma warning restore IL3000
        if (!singleFile) return ($"FsForge-{runtime}-win-x64.zip", false);

        string folder = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        bool installed = programFiles.Length > 0 && string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar),
            Path.Combine(programFiles, $"FsForge-{runtime}"), StringComparison.OrdinalIgnoreCase);
        return installed ? ($"FsForge-{runtime}-win-x64.msi", true) : ($"FsForge-{runtime}-win-x64-standalone.zip", false);
    }

    /// <summary>The user's Downloads folder, or %LOCALAPPDATA%\FsForge\Updates when there is none.</summary>
    public static string DefaultDownloadFolder()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FsForge", "Updates");
    }

    /// <summary>"0.2.0": three components, missing ones as zero.</summary>
    public static string Format(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0)).ToString();

    /// <summary>Finds the newest release and its file for this build.</summary>
    public Task<UpdateCheckResult> CheckAsync(bool includePrereleases, CancellationToken ct = default) =>
        Create(includePrereleases).CheckForUpdateAsync(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase), ct);

    /// <summary>Downloads the file of a release found by <see cref="CheckAsync"/> and checks its SHA-256.</summary>
    public Task<DownloadedUpdate> DownloadAsync(UpdateCheckResult check, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct = default)
    {
        if (check is not { IsUpdateAvailable: true, Latest: { } release, Asset: { } asset })
            throw new InvalidOperationException("There is no update to download.");
        return Create(release.IsPrerelease).DownloadAsync(release, asset, DownloadFolder, progress, ct);
    }

    private GitHubUpdater Create(bool includePrereleases) => new(_http, new GitHubUpdaterOptions
    {
        Owner = Owner,
        Repository = Repository,
        CurrentVersion = CurrentVersion,
        IncludePrereleases = includePrereleases,
        UserAgent = $"FsForge/{Format(CurrentVersion)}",
        ApiBaseUrl = _apiBaseUrl ?? new Uri("https://api.github.com/"),
    });
}
