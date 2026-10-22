namespace Gheychi.Core.Spam;

/// <summary>Replaces the bundled spam model with a newer one, e.g. a downloaded update.</summary>
public interface ISpamModelUpdater
{
    /// <summary>
    /// Version of the model in use, or of the one that will be loaded while none is yet; null when none could be loaded.
    /// </summary>
    Task<int?> GetActiveVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a model package (its manifest and model file) and switches to it. Returns false, leaving the
    /// active model in place, when the package does not load or is not newer than the active model.
    /// </summary>
    Task<bool> InstallAsync(Stream manifest, Stream model, CancellationToken cancellationToken = default);

    /// <summary>True while a user-imported model in app storage is the active scorer (not an official update).</summary>
    bool IsCustomActive { get; }

    /// <summary>True when a custom folder exists but could not be loaded; official models may still score.</summary>
    bool CustomLoadFailed { get; }

    /// <summary>
    /// Installs a training-export zip and switches scoring to it. False leaves the active model unchanged.
    /// </summary>
    Task<bool> ImportCustomAsync(Stream zip, CancellationToken cancellationToken = default);

    /// <summary>Removes the custom model, if any, and switches back to the official active model.</summary>
    Task RemoveCustomAsync(CancellationToken cancellationToken = default);
}
