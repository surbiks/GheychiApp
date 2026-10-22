using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Gheychi.Core.Spam;
using Gheychi.Infrastructure.Spam;
using Xunit;

namespace Gheychi.Infrastructure.Tests;

// The bundled model is the latest release of the dataset repository, fetched by the build, so its version is read
// rather than assumed.
public sealed class SpamModelStoreTests : IDisposable
{
    private static readonly string BundledDirectory = Path.Combine(AppContext.BaseDirectory, "SpamModel");
    private static readonly SpamModelManifest Bundled = SpamModelManifest.Read(Path.Combine(BundledDirectory, SpamModelManifest.FileName));

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gheychi_spam_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private SpamModelStore CreateStore() =>
        new(_root, name => Task.FromResult<Stream>(File.OpenRead(Path.Combine(BundledDirectory, name))));

    private static Stream Manifest(int version, Action<JsonObject>? change = null)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(BundledDirectory, SpamModelManifest.FileName)))!.AsObject();
        json["version"] = version;
        json["model"] = "next.mlnet";
        change?.Invoke(json);
        return new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    private static Stream BundledModel() => File.OpenRead(Path.Combine(BundledDirectory, Bundled.ModelFile));

    private static MemoryStream Zip(Stream manifest, Stream model, string modelEntryName = "next.mlnet")
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry(SpamModelManifest.FileName);
            using (var entry = manifestEntry.Open())
                manifest.CopyTo(entry);

            var modelEntry = archive.CreateEntry(modelEntryName);
            using (var entry = modelEntry.Open())
                model.CopyTo(entry);
        }

        zip.Position = 0;
        return zip;
    }

    private static string VersionName(int version) => version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task BundledModel_IsInstalledAndScoresSpamAboveDefaultThreshold()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());

        var spam = await classifier.ScoreAsync("تخفیف ویژه ۷۰ درصد فقط امروز! برای خرید روی لینک کلیک کنید lnkd.ir/abc لغو۱۱");
        var personal = await classifier.ScoreAsync("سلام عزیزم، شب میای خونه؟");

        Assert.NotNull(spam);
        Assert.NotNull(personal);
        Assert.Equal(Bundled.Version, spam.Value.ModelVersion);
        Assert.True(spam.Value.Probability >= SpamDetector.DefaultThreshold);
        Assert.True(personal.Value.Probability < SpamDetector.DefaultThreshold);
        Assert.True(Directory.Exists(Path.Combine(_root, VersionName(Bundled.Version))));
    }

    [Fact]
    public async Task InstallAsync_NewerVersion_BecomesActiveAndRemovesOlder()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());
        Assert.Equal(Bundled.Version, await classifier.GetActiveVersionAsync());

        await using var manifest = Manifest(Bundled.Version + 1);
        await using var model = BundledModel();
        Assert.True(await classifier.InstallAsync(manifest, model));

        Assert.Equal(Bundled.Version + 1, await classifier.GetActiveVersionAsync());
        Assert.Equal(Bundled.Version + 1, (await classifier.ScoreAsync("hello"))?.ModelVersion);
        Assert.False(Directory.Exists(Path.Combine(_root, VersionName(Bundled.Version))));
    }

    [Fact]
    public async Task InstalledNewerVersion_SurvivesRestart_AndBundledIsNotReinstalled()
    {
        var first = new MlNetSpamClassifier(CreateStore());
        await using (var manifest = Manifest(Bundled.Version + 2))
        await using (var model = BundledModel())
            Assert.True(await first.InstallAsync(manifest, model));

        var afterRestart = new MlNetSpamClassifier(CreateStore());
        await afterRestart.WarmUpAsync();

        Assert.Equal(Bundled.Version + 2, await afterRestart.GetActiveVersionAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, VersionName(Bundled.Version))));
    }

    [Fact]
    public async Task ActiveVersion_BeforeTheModelIsLoaded_IsReadWithoutInstallingIt()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());

        Assert.Equal(Bundled.Version, await classifier.GetActiveVersionAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, VersionName(Bundled.Version))));
    }

    [Fact]
    public async Task ActiveVersion_BeforeTheModelIsLoaded_PrefersANewerInstalledOne()
    {
        await using (var manifest = Manifest(Bundled.Version + 3))
        await using (var model = BundledModel())
            Assert.True(await new MlNetSpamClassifier(CreateStore()).InstallAsync(manifest, model));

        Assert.Equal(Bundled.Version + 3, await new MlNetSpamClassifier(CreateStore()).GetActiveVersionAsync());
    }

    [Fact]
    public async Task InstallAsync_SameVersion_IsRejected()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());

        await using var manifest = Manifest(Bundled.Version);
        await using var model = BundledModel();

        Assert.False(await classifier.InstallAsync(manifest, model));
        Assert.Equal(Bundled.Version, await classifier.GetActiveVersionAsync());
    }

    [Fact]
    public async Task InstallAsync_CorruptModel_KeepsActiveModel()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());

        await using var manifest = Manifest(Bundled.Version + 1);
        await using var model = new MemoryStream([1, 2, 3, 4]);

        Assert.False(await classifier.InstallAsync(manifest, model));
        Assert.Equal(Bundled.Version, await classifier.GetActiveVersionAsync());
        Assert.Equal([VersionName(Bundled.Version)], Directory.GetDirectories(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task InstallAsync_UnknownSpamLabel_IsRejected()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());

        await using var manifest = Manifest(Bundled.Version + 1, json => json["spamLabel"] = "not-a-label");
        await using var model = BundledModel();

        Assert.False(await classifier.InstallAsync(manifest, model));
        Assert.Equal(Bundled.Version, await classifier.GetActiveVersionAsync());
    }

    [Fact]
    public async Task InstallAsync_NewerNormalizerThanThisApp_IsRejected()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());

        await using var manifest = Manifest(Bundled.Version + 1, json => json["normalizer"] = TextNormalizer.Version + 1);
        await using var model = BundledModel();

        Assert.False(await classifier.InstallAsync(manifest, model));
        Assert.Equal(Bundled.Version, await classifier.GetActiveVersionAsync());
    }

    [Fact]
    public async Task MissingBundledModel_ScoresNothing()
    {
        var store = new SpamModelStore(_root, _ => throw new FileNotFoundException());
        var classifier = new MlNetSpamClassifier(store);

        Assert.Null(await classifier.ScoreAsync("hello"));
        Assert.Null(await classifier.GetActiveVersionAsync());
    }

    [Theory]
    [InlineData("../evil.mlnet")]
    [InlineData("sub/model.mlnet")]
    [InlineData("manifest.json")]
    [InlineData("")]
    public void Manifest_RejectsModelNamesThatAreNotPlainFiles(string model)
    {
        var json = $$"""{"version":2,"model":"{{model}}","textColumn":"t","labelColumn":"l","spamLabel":"s"}""";

        Assert.Throws<InvalidDataException>(() => SpamModelManifest.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task Custom_InstallsWithLowerVersionAndLoadsActive()
    {
        var store = CreateStore();
        await using var manifest = Manifest(1);
        await using var model = BundledModel();
        await using var zip = Zip(manifest, model);

        var installed = await store.InstallCustomAsync(zip);
        Assert.NotNull(installed);
        Assert.Equal(1, installed.Version);
        Assert.True(store.HasCustom);

        var active = await CreateStore().LoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(1, active.Version);
    }

    [Fact]
    public async Task Custom_WinsOverNewerBundled()
    {
        var store = CreateStore();
        await using var manifest = Manifest(1);
        await using var model = BundledModel();
        await using var zip = Zip(manifest, model);
        Assert.NotNull(await store.InstallCustomAsync(zip));

        var score = await new MlNetSpamClassifier(CreateStore()).ScoreAsync("hello");
        Assert.NotNull(score);
        Assert.Equal(1, score.Value.ModelVersion);
    }

    [Fact]
    public async Task Classifier_InstallRefusedWhileCustomActive()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());
        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.True(await classifier.ImportCustomAsync(zip));

        await using var updateManifest = Manifest(Bundled.Version + 10);
        await using var updateModel = BundledModel();
        Assert.False(await classifier.InstallAsync(updateManifest, updateModel));
        Assert.Equal(1, (await classifier.ScoreAsync("hello"))?.ModelVersion);
    }

    [Fact]
    public async Task Classifier_ImportCustom_SwitchesScoringAndRemoveRestores()
    {
        var classifier = new MlNetSpamClassifier(CreateStore());
        await using var manifest = Manifest(1);
        await using var model = BundledModel();
        await using var zip = Zip(manifest, model);

        Assert.True(await classifier.ImportCustomAsync(zip));
        Assert.True(classifier.IsCustomActive);
        Assert.Equal(1, (await classifier.ScoreAsync("hello"))?.ModelVersion);

        await classifier.RemoveCustomAsync();
        Assert.False(classifier.IsCustomActive);
        Assert.Equal(Bundled.Version, (await classifier.ScoreAsync("hello"))?.ModelVersion);
    }

    [Fact]
    public async Task Custom_ReplacesPreviousCustom()
    {
        var store = CreateStore();
        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        await using (var manifest = Manifest(2))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        var active = await CreateStore().LoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(2, active.Version);
        Assert.Equal([SpamModelStore.CustomDirectoryName], Directory.GetDirectories(_root).Select(Path.GetFileName));
        Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path)!.StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Custom_RejectsBrokenPackages()
    {
        var store = CreateStore();

        await using (var bad = new MemoryStream("not a zip"u8.ToArray()))
            Assert.Null(await store.InstallCustomAsync(bad));
        Assert.False(store.HasCustom);

        await using (var manifest = Manifest(1))
        {
            await using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifestEntry = archive.CreateEntry(SpamModelManifest.FileName);
                using (var entry = manifestEntry.Open())
                    manifest.CopyTo(entry);
            }

            zip.Position = 0;
            Assert.Null(await store.InstallCustomAsync(zip));
        }
        Assert.False(store.HasCustom);

        await using (var manifest = Manifest(1, json => json["normalizer"] = TextNormalizer.Version + 1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.Null(await store.InstallCustomAsync(zip));
        Assert.False(store.HasCustom);
    }

    [Fact]
    public async Task Custom_RejectedImportKeepsExistingCustom()
    {
        var store = CreateStore();
        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        await using (var bad = new MemoryStream("not a zip"u8.ToArray()))
            Assert.Null(await store.InstallCustomAsync(bad));

        var active = await CreateStore().LoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(1, active.Version);
    }

    [Fact]
    public async Task RemoveCustom_FallsBackToOfficial()
    {
        var store = CreateStore();
        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        store.RemoveCustom();

        var active = await CreateStore().LoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(Bundled.Version, active.Version);
    }

    [Fact]
    public async Task Custom_UnloadableFallsBackAndFlags()
    {
        var store = CreateStore();
        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        await File.WriteAllBytesAsync(Path.Combine(_root, SpamModelStore.CustomDirectoryName, "next.mlnet"), [1, 2, 3, 4]);

        var fresh = CreateStore();
        var active = await fresh.LoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(Bundled.Version, active.Version);
        Assert.True(fresh.HasCustom);
        Assert.True(fresh.CustomLoadFailed);
    }

    [Fact]
    public async Task Custom_FailedLoad_ClearedAfterGoodImport()
    {
        var store = CreateStore();
        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        await File.WriteAllBytesAsync(Path.Combine(_root, SpamModelStore.CustomDirectoryName, "next.mlnet"), [1, 2, 3, 4]);
        await store.LoadActiveAsync();
        Assert.True(store.CustomLoadFailed);

        await using (var manifest = Manifest(1))
        await using (var model = BundledModel())
        await using (var zip = Zip(manifest, model))
            Assert.NotNull(await store.InstallCustomAsync(zip));

        Assert.False(store.CustomLoadFailed);
    }

    [Fact]
    public void Manifest_ReadsReleaseFields_AndDefaultsToRawText()
    {
        var release = SpamModelManifest.Parse(Encoding.UTF8.GetBytes(
            """{"version":4,"model":"spam.mlnet","textColumn":"Text","labelColumn":"Label","spamLabel":"spam","normalizer":1,"size":123,"sha256":"ab"}"""));
        var legacy = SpamModelManifest.Parse(Encoding.UTF8.GetBytes(
            """{"version":1,"model":"spam-v1.mlnet","textColumn":"t","labelColumn":"l","spamLabel":"s"}"""));

        Assert.Equal((1, 123L, "ab"), (release.Normalizer, release.Size, release.Sha256));
        Assert.Equal((0, (long?)null, (string?)null), (legacy.Normalizer, legacy.Size, legacy.Sha256));
    }
}
