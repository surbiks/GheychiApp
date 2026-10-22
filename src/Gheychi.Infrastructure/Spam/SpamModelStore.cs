using System.Globalization;
using System.IO.Compression;

namespace Gheychi.Infrastructure.Spam;

/// <summary>
/// Installed models, one folder per version under the root. The model bundled with the app is copied in when it is
/// newer than every installed one, so both an app update and a downloaded model can bring a new version.
/// </summary>
public sealed class SpamModelStore
{
    private const string StagingPrefix = ".staging-";

    public const string CustomDirectoryName = "custom";

    private readonly string _root;
    private readonly Func<string, Task<Stream>> _openBundledFile;

    /// <param name="openBundledFile">Opens a file of the bundled model package by name: the manifest, then the model file it names.</param>
    public SpamModelStore(string root, Func<string, Task<Stream>> openBundledFile)
    {
        _root = root;
        _openBundledFile = openBundledFile;
    }

    /// <summary>True when an imported model folder exists, even if it no longer loads.</summary>
    public bool HasCustom => Directory.Exists(CustomDirectory);

    /// <summary>Set by <see cref="LoadActiveAsync"/> when <see cref="HasCustom"/> is true but the folder did not load.</summary>
    public bool CustomLoadFailed { get; private set; }

    /// <summary>The newest model that loads, or null when there is none.</summary>
    public async Task<SpamModel?> LoadActiveAsync(CancellationToken cancellationToken = default)
    {
        CustomLoadFailed = false;

        if (HasCustom)
        {
            try
            {
                return SpamModel.Load(CustomDirectory);
            }
            catch (InvalidDataException ex)
            {
                CustomLoadFailed = true;
                System.Diagnostics.Debug.WriteLine($"Custom spam model is unusable: {ex}");
            }
        }

        var bundled = await InstallBundledIfNewerAsync(cancellationToken);
        if (bundled is not null)
            return bundled;

        foreach (var version in InstalledVersions())
        {
            try
            {
                return SpamModel.Load(VersionDirectory(version));
            }
            catch (InvalidDataException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Spam model {version} is unusable: {ex}");
            }
        }

        return null;
    }

    /// <summary>
    /// The version <see cref="LoadActiveAsync"/> would start from, read without loading a model: the newest installed
    /// one, or the bundled one when that is newer. Null when there is neither.
    /// </summary>
    public async Task<int?> PeekActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        if (HasCustom)
        {
            try
            {
                return SpamModelManifest.Read(Path.Combine(CustomDirectory, SpamModelManifest.FileName)).Version;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Custom spam model manifest unreadable: {ex}");
            }
        }

        var newest = 0;
        try
        {
            newest = InstalledVersions().DefaultIfEmpty(0).First();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Reading installed spam models failed: {ex}");
        }

        try
        {
            await using var stream = await _openBundledFile(SpamModelManifest.FileName);
            newest = Math.Max(newest, SpamModelManifest.Parse(await ReadAllAsync(stream, cancellationToken)).Version);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Bundled spam model unavailable: {ex}");
        }

        return newest > 0 ? newest : null;
    }

    /// <summary>Writes the package, loads it, and only then makes it an installed version. Null when it is rejected.</summary>
    public async Task<SpamModel?> InstallAsync(Stream manifest, Stream model, int activeVersion, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(_root, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            var manifestBytes = await ReadAllAsync(manifest, cancellationToken);
            var parsed = SpamModelManifest.Parse(manifestBytes);
            if (parsed.Version <= activeVersion)
                return null;

            var loaded = await StageAndLoadAsync(staging, parsed, manifestBytes, model, cancellationToken);
            if (loaded is null)
                return null;

            var target = VersionDirectory(parsed.Version);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            return loaded;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Spam model install failed: {ex}");
            return null;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Installs an imported zip package into <see cref="CustomDirectoryName"/> when it validates. Null when rejected;
    /// existing custom and versioned folders are untouched on rejection.
    /// </summary>
    public async Task<SpamModel?> InstallCustomAsync(Stream zip, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(_root, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await using var zipCopy = new MemoryStream();
            await zip.CopyToAsync(zipCopy, cancellationToken);
            zipCopy.Position = 0;

            using var archive = new ZipArchive(zipCopy, ZipArchiveMode.Read);
            var manifestEntry = archive.GetEntry(SpamModelManifest.FileName);
            if (manifestEntry is null)
                return null;

            var manifestBytes = await ReadAllAsync(manifestEntry.Open(), cancellationToken);
            var parsed = SpamModelManifest.Parse(manifestBytes);

            var modelEntry = archive.GetEntry(parsed.ModelFile);
            if (modelEntry is null)
                return null;

            var loaded = await StageAndLoadAsync(staging, parsed, manifestBytes, modelEntry.Open(), cancellationToken);
            if (loaded is null)
                return null;

            if (Directory.Exists(CustomDirectory))
                Directory.Delete(CustomDirectory, recursive: true);
            Directory.Move(staging, CustomDirectory);
            return loaded;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Custom spam model install failed: {ex}");
            return null;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Removes the imported model so bundled and downloaded versions can become active again.</summary>
    public void RemoveCustom()
    {
        TryDelete(CustomDirectory);
    }

    /// <summary>Removes every installed version older than <paramref name="version"/>, and leftovers of interrupted installs.</summary>
    public void DeleteOlderThan(int version)
    {
        if (!Directory.Exists(_root))
            return;

        foreach (var old in InstalledVersions().Where(v => v < version))
            TryDelete(VersionDirectory(old));

        foreach (var staging in Directory.EnumerateDirectories(_root, StagingPrefix + "*"))
            TryDelete(staging);
    }

    /// <summary>The bundled model, already loaded, when it was just installed; null when an installed one is as new.</summary>
    private async Task<SpamModel?> InstallBundledIfNewerAsync(CancellationToken cancellationToken)
    {
        try
        {
            SpamModelManifest bundled;
            await using (var stream = await _openBundledFile(SpamModelManifest.FileName))
                bundled = SpamModelManifest.Parse(await ReadAllAsync(stream, cancellationToken));

            var newest = InstalledVersions().DefaultIfEmpty(0).First();
            if (bundled.Version <= newest)
                return null;

            await using var manifest = await _openBundledFile(SpamModelManifest.FileName);
            await using var model = await _openBundledFile(bundled.ModelFile);
            return await InstallAsync(manifest, model, newest, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Bundled spam model unavailable: {ex}");
            return null;
        }
    }

    /// <summary>Validates manifest and model bytes in a staging folder before any live directory is replaced.</summary>
    private static async Task<SpamModel?> StageAndLoadAsync(
        string staging,
        SpamModelManifest parsed,
        byte[] manifestBytes,
        Stream model,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, SpamModelManifest.FileName), manifestBytes, cancellationToken);
            await using (var file = File.Create(Path.Combine(staging, parsed.ModelFile)))
                await model.CopyToAsync(file, cancellationToken);

            return SpamModel.Load(staging);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Spam model staging failed: {ex}");
            return null;
        }
    }

    private string CustomDirectory => Path.Combine(_root, CustomDirectoryName);

    /// <summary>Newest first.</summary>
    private List<int> InstalledVersions()
    {
        if (!Directory.Exists(_root))
            return [];

        return Directory.EnumerateDirectories(_root)
            .Select(path => int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : 0)
            .Where(version => version > 0)
            .OrderByDescending(version => version)
            .ToList();
    }

    private string VersionDirectory(int version) => Path.Combine(_root, version.ToString(CultureInfo.InvariantCulture));

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not delete {directory}: {ex}");
        }
    }
}
