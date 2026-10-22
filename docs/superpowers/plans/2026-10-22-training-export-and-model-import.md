# Training Export & Custom Model Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Export selected messages as labelled training JSON, train on a folder of such files on a PC, and import the resulting model into the app as a pinned custom model.

**Architecture:** A pure exporter in `Gheychi.Core` builds the JSON; the App collects texts from the inbox (selected threads) and the spam tab (selected messages) and saves through Avalonia's `StorageProvider`. The Trainer accepts a folder and resolves label conflicts in favour of the newest export, and also writes a `.zip` package. `SpamModelStore` keeps an imported model in a `custom` folder that wins over bundled/installed models; while it exists, `SpamModelUpdates` never touches the network.

**Tech Stack:** .NET 10, Avalonia 12 (Android), ML.NET, xunit, System.Text.Json, System.IO.Compression.

**Spec:** `docs/superpowers/specs/2026-10-22-training-export-and-model-import-design.md` (Persian). Read it before any task.

## Global Constraints

- Nothing in this feature sends data to the network. Export only writes where the user picks; import only reads a picked file.
- Exported JSON is exactly the dataset format: array of `{ "Text": string, "Label": 0|1 }`, 1 = spam, UTF-8, Persian not escaped, indented.
- Every exported text is `TextNormalizer.Normalize`d; empty-after-normalize and duplicates within one file are dropped.
- Export file name: `gheychi-spam-yyyyMMdd-HHmm.json` / `gheychi-ham-yyyyMMdd-HHmm.json` (local time).
- Model package: `gheychi-model-v{N}.zip` containing `manifest.json` and the model file the manifest names.
- Custom model folder: `<models root>/custom`. Its existence is custom mode. No version comparison for custom installs.
- Code style: match surrounding code — file-scoped namespaces, `///` summaries saying *why*, `System.Diagnostics.Debug.WriteLine` for swallowed errors, no new dependencies.
- Every user-visible string goes in both `src/Gheychi.App/Localization/AppResources.resx` (English) and `AppResources.fa.resx` (Persian), keys and copy as listed in Tasks 5–6.
- Build/test commands need `export PATH=$HOME/.dotnet:$PATH`.

## Review Focus

1. A selected inbox thread with hundreds of messages — export must not block the UI thread (collect on `Task.Run`). Owner: Task 5.
2. A zip whose manifest names a model file that is missing from the zip, or a zip that isn't a zip — import returns false and the active model keeps scoring. Test in Task 3.
3. Import of a custom model while a custom model already exists — the new one replaces it, the old folder is gone. Test in Task 3.
4. App update ships a newer bundled model while custom is active — custom still loads. Test in Task 3 (`Custom_WinsOverNewerBundled`).
5. User cancels the label sheet or the save dialog — nothing written, no toast of success. Owner: Task 5 (manual check).

---

### Task 1: Trainer — folder input, export-wins conflicts, zip package

Repo: `/co/study-works/gheychi/sms-spam-dataset` (branch `custom`).

**Files:**
- Modify: `Trainer/Program.cs`
- Modify: `README.md` (one short paragraph under "Spam model for Gheychi")

**Interfaces:**
- Produces: CLI `Trainer <data.json | folder> <version> <output directory>`; writes `spam.mlnet`, `manifest.json`, `gheychi-model-v{version}.zip`.

- [ ] **Step 1: Load from file or folder.** If `args[0]` is a directory, read every top-level `*.json` in it. Order files: those whose name does **not** start with `gheychi-` first (ordinal by name), then `gheychi-*.json` ordinal by name. A file's index in that order is its priority (higher wins). A single file path keeps today's behaviour (one file, priority 0). Print one line per file read with its row count.

- [ ] **Step 2: Conflict rule.** Replace the current `GroupBy(...).Where(distinct labels == 1)` with:

```
group normalized rows by Text
  top = rows in the group with the group's highest priority
  if top has more than one distinct label → drop the text   (conflict inside one file, as today)
  else keep one row with top's label
  if the whole group had more than one distinct label → overridden++
```
Print `"{overridden} texts took the label of a newer file"`.

- [ ] **Step 3: Zip package.** After `manifest.json` is written, create `gheychi-model-v{version}.zip` in the output directory with entries `manifest.json` and `spam.mlnet` (`ZipFile.Open(..., ZipArchiveMode.Create)` + `CreateEntryFromFile`; delete an existing zip of that name first). `train.yml` uploads files by name, so it is unaffected — do not edit it.

- [ ] **Step 4: Verify (manual, no test project).**

```bash
cd /co/study-works/gheychi/sms-spam-dataset && export PATH=$HOME/.dotnet:$PATH
mkdir -p /tmp/claude-1000/trainer-check/in && cp data.json /tmp/claude-1000/trainer-check/in/
# take the first spam text of data.json and export it as ham
python3 -c "import json;t=json.load(open('data.json'))[0]['Text'];json.dump([{'Text':t,'Label':0}],open('/tmp/claude-1000/trainer-check/in/gheychi-ham-20261022-1200.json','w'),ensure_ascii=False)"
dotnet run --project Trainer -c Release -- /tmp/claude-1000/trainer-check/in 99 /tmp/claude-1000/trainer-check/out
unzip -l /tmp/claude-1000/trainer-check/out/gheychi-model-v99.zip
dotnet run --project Trainer -c Release -- data.json 98 /tmp/claude-1000/trainer-check/out-single
```
Expected: two "read" lines; `1 texts took the label of a newer file`; zip lists `manifest.json` and `spam.mlnet`; the single-file run still succeeds.

- [ ] **Step 5: Commit** (`git add Trainer/Program.cs README.md`), message `feat(trainer): train on a folder, newer exports win label conflicts, write a model zip`.

---

### Task 2: Core — `TrainingExport`

**Files:**
- Create: `src/Gheychi.Core/Spam/TrainingExport.cs`
- Test: `tests/Gheychi.Core.Tests/TrainingExportTests.cs`

**Interfaces:**
- Produces (namespace `Gheychi.Core.Spam`):
  - `public static IReadOnlyList<string> TrainingExport.Prepare(IEnumerable<string?> texts)` — normalized, empties dropped, distinct, first-seen order.
  - `public static string TrainingExport.ToJson(IReadOnlyList<string> prepared, bool isSpam)`
  - `public static string TrainingExport.FileName(bool isSpam, DateTime localTime)`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Prepare_NormalizesDropsEmptyAndDuplicates()
{
    var a = "تخفیف ویژه 09121234567";
    var prepared = TrainingExport.Prepare([a, a, "   ", null, "سلام"]);
    Assert.Equal([TextNormalizer.Normalize(a), TextNormalizer.Normalize("سلام")], prepared);
}

[Fact] public void ToJson_IsDatasetFormatWithReadablePersian()
{
    var json = TrainingExport.ToJson(["سلام"], isSpam: true);
    Assert.Contains("سلام", json);                       // not \uXXXX-escaped
    var rows = JsonSerializer.Deserialize<List<JsonElement>>(json)!;
    Assert.Equal("سلام", rows[0].GetProperty("Text").GetString());
    Assert.Equal(1, rows[0].GetProperty("Label").GetInt32());
    Assert.Equal(0, JsonSerializer.Deserialize<List<JsonElement>>(TrainingExport.ToJson(["x"], false))![0].GetProperty("Label").GetInt32());
}

[Fact] public void FileName_HasLabelAndMinute()
{
    var t = new DateTime(2026, 10, 22, 14, 30, 59);
    Assert.Equal("gheychi-spam-20261022-1430.json", TrainingExport.FileName(true, t));
    Assert.Equal("gheychi-ham-20261022-1430.json", TrainingExport.FileName(false, t));
}
```

- [ ] **Step 2: Run, expect compile failure.** `dotnet test tests/Gheychi.Core.Tests --filter TrainingExportTests`

- [ ] **Step 3: Implement.** `ToJson`: serialize `{ Text, Label }` objects in that property order with `WriteIndented = true` and `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. `FileName`: invariant culture, format `yyyyMMdd-HHmm`.

- [ ] **Step 4: Run, expect PASS** (whole `Gheychi.Core.Tests` project green).

- [ ] **Step 5: Commit** `feat(spam): build training export JSON`.

---

### Task 3: Infrastructure — custom model in `SpamModelStore`

**Files:**
- Modify: `src/Gheychi.Infrastructure/Spam/SpamModelStore.cs`
- Test: `tests/Gheychi.Infrastructure.Tests/SpamModelStoreTests.cs`

**Interfaces:**
- Produces on `SpamModelStore`:
  - `public const string CustomDirectoryName = "custom";`
  - `public bool HasCustom { get; }` — `Directory.Exists(<root>/custom)`.
  - `public bool CustomLoadFailed { get; private set; }` — set by `LoadActiveAsync`: true when `HasCustom` but it did not load.
  - `public Task<SpamModel?> InstallCustomAsync(Stream zip, CancellationToken cancellationToken = default)` — null when rejected; never touches the active custom/official folders on rejection.
  - `public void RemoveCustom()`

- [ ] **Step 1: Write failing tests** (use existing helpers `Manifest(...)`, `BundledModel()`, `CreateStore()`; add a helper `Zip(Stream manifest, Stream model, string modelEntryName = "next.mlnet")` building an in-memory zip).

```csharp
[Fact] Custom_InstallsWithLowerVersionAndLoadsActive()
    // InstallCustomAsync(Zip(Manifest(1), BundledModel())) → not null, Version 1; HasCustom;
    // new store LoadActiveAsync() → Version 1 (bundled is higher, custom still wins)
[Fact] Custom_WinsOverNewerBundled()          // same as above via a fresh store + MlNetSpamClassifier.ScoreAsync → ModelVersion 1
[Fact] Custom_ReplacesPreviousCustom()        // install v1 then v2 → load gives 2; only one "custom" dir, no ".staging-*" left
[Fact] Custom_RejectsBrokenPackages()         // not-a-zip bytes; zip without model entry; manifest normalizer = TextNormalizer.Version + 1
                                              //  → each returns null, HasCustom stays false
[Fact] Custom_RejectedImportKeepsExistingCustom() // install v1, then a broken zip → null; LoadActiveAsync still v1
[Fact] RemoveCustom_FallsBackToOfficial()     // install v1, RemoveCustom(), fresh store LoadActiveAsync → Bundled.Version
[Fact] Custom_UnloadableFallsBackAndFlags()   // install v1, overwrite custom/next.mlnet with garbage bytes,
                                              //  fresh store LoadActiveAsync → Bundled.Version, CustomLoadFailed true, HasCustom true
```

- [ ] **Step 2: Run, expect compile failure.** `dotnet test tests/Gheychi.Infrastructure.Tests --filter SpamModelStoreTests`

- [ ] **Step 3: Implement.**
  - `InstallCustomAsync`: copy zip to a `MemoryStream`, open `ZipArchive` (read), read `manifest.json` entry, `SpamModelManifest.Parse`, open entry `parsed.ModelFile`; stage + `SpamModel.Load(staging)` exactly like `InstallAsync` (extract the shared staging part into a private method rather than duplicating), then delete `custom` and move staging to it. Catch `InvalidDataException`, `IOException`, `UnauthorizedAccessException` (zip errors are `InvalidDataException`); missing entry → null.
  - `LoadActiveAsync`: first, if `HasCustom`, try `SpamModel.Load(custom)`; success → return it (skip bundled install); failure → `CustomLoadFailed = true`, log, continue with today's path.
  - `PeekActiveVersionAsync`: if `HasCustom` and its manifest parses, return that version.
  - `InstalledVersions` already ignores `custom` (non-numeric); keep it that way.

- [ ] **Step 4: Run, expect PASS** (whole project green, existing tests untouched).

- [ ] **Step 5: Commit** `feat(spam): keep an imported model in a custom folder that wins over official ones`.

---

### Task 4: Updater surface and update suppression

**Files:**
- Modify: `src/Gheychi.Core/Spam/ISpamModelUpdater.cs`
- Modify: `src/Gheychi.Infrastructure/Spam/MlNetSpamClassifier.cs`
- Modify: `src/Gheychi.Core/Spam/SpamModelUpdates.cs`
- Test: `tests/Gheychi.Core.Tests/SpamModelUpdatesTests.cs` (extend its fake updater), `tests/Gheychi.Infrastructure.Tests/SpamModelStoreTests.cs`

**Interfaces:**
- Consumes: Task 3's `SpamModelStore` members.
- Produces on `ISpamModelUpdater`:
  - `bool IsCustomActive { get; }`
  - `bool CustomLoadFailed { get; }`
  - `Task<bool> ImportCustomAsync(Stream zip, CancellationToken cancellationToken = default);` — true when installed and now scoring.
  - `Task RemoveCustomAsync(CancellationToken cancellationToken = default);` — removes, then loads the official model.

- [ ] **Step 1: Write failing tests**

```csharp
// SpamModelUpdatesTests
[Fact] Check_WhileCustomActive_DoesNotAskSource()
    // fake updater IsCustomActive = true; CheckAsync(force: true) → null; fake source call count 0
[Fact] Available_WhileCustomActive_IsNull()
    // state.Available = some release newer than active; IsCustomActive = true → GetAvailableAsync() null

// SpamModelStoreTests (classifier level)
[Fact] Classifier_ImportCustom_SwitchesScoringAndRemoveRestores()
    // classifier over CreateStore(); ImportCustomAsync(zip v1) true; ScoreAsync(...).ModelVersion == 1; IsCustomActive;
    // RemoveCustomAsync(); ScoreAsync(...).ModelVersion == Bundled.Version; !IsCustomActive
```

- [ ] **Step 2: Run both test projects, expect compile failure.**

- [ ] **Step 3: Implement.**
  - `MlNetSpamClassifier`: members forward to the store under `_gate`; `ImportCustomAsync` swaps `_model` with `Volatile.Write` like `InstallAsync`. `RemoveCustomAsync` calls `RemoveCustom()` then loads `_store.LoadActiveAsync()` on `Task.Run` and swaps it in. `CustomLoadFailed` reads the store's flag.
  - `SpamModelUpdates.CheckAsync` and `GetAvailableAsync`: when `_updater.IsCustomActive`, `SetAvailable(null)` and return null before any source call.

- [ ] **Step 4: Run both test projects, expect PASS.**

- [ ] **Step 5: Commit** `feat(spam): import and remove a custom model; no update checks while one is active`.

---

### Task 5: App — export from inbox and spam tab

**Files:**
- Create: `src/Gheychi.App/Controls/TrainingExportFlow.cs`
- Modify: `src/Gheychi.App/Pages/MessagesPage.axaml(.cs)` (item in the selection three-dots menu `SelectBoxOverlay`)
- Modify: `src/Gheychi.App/Pages/SpamPage.axaml(.cs)`, `src/Gheychi.App/ViewModels/SpamItem.cs`, `src/Gheychi.App/ViewModels/SpamViewModel.cs` (multi-select)
- Modify: `src/Gheychi.App/Localization/AppResources.resx`, `AppResources.fa.resx`

**Interfaces:**
- Consumes: Task 2 `TrainingExport`.
- Produces: `internal static class TrainingExportFlow { public static Task RunAsync(Func<Task<IReadOnlyList<string>>> collectTexts); }` — asks the label (`Sheet.ListAsync`), collects on `Task.Run`, `Prepare`; empty → toast `Export_Empty`; else `TopLevel.GetTopLevel(MainView.Current).StorageProvider.SaveFilePickerAsync` with `SuggestedFileName = TrainingExport.FileName(...)`, `DefaultExtension = "json"`; writes UTF-8; toast `Export_Done`. Cancel at the sheet or the picker → return silently. Any exception → log + toast `Export_Failed`. Nothing may throw (called from `async void` handlers), same as `SpamReport.SubmitAsync`.

| Key | English | فارسی |
|---|---|---|
| `Export_ForTraining` | Export for training | اکسپورت برای آموزش |
| `Export_LabelTitle` | Label these messages as | برچسب این پیام‌ها |
| `Export_LabelSpam` | Spam | هرزنامه |
| `Export_LabelHam` | Normal message | پیام معمولی |
| `Export_Done` | {0} messages exported | {0} پیام اکسپورت شد |
| `Export_Empty` | No messages to export | پیامی برای اکسپورت نبود |
| `Export_Failed` | Export failed | اکسپورت انجام نشد |

- [ ] **Step 1: `TrainingExportFlow`** as above.
- [ ] **Step 2: Inbox.** Add an `Export_ForTraining` row to `SelectBoxOverlay` (same markup as the pin/mark-read rows). Handler: close overlay, take the selected threads' ids, call `TrainingExportFlow.RunAsync` with a collector that calls `ISmsService.GetMessagesAsync(threadId)` per thread and keeps `Body` where `!IsOutgoing`; then exit selection mode.
- [ ] **Step 3: Spam tab multi-select.** Long-press (`Holding`/the gesture the inbox uses — copy its mechanism) on a `SpamItem` enters selection mode; while in it, tap toggles `SpamItem.IsSelected` (bindable, with a check mark in the template like the inbox rows) instead of opening `Overlay.ShowMenu`. A selection header replaces `AppHeader` (copy the inbox's selection header look: close ✕, count, an export icon button). Export → `TrainingExportFlow.RunAsync` with the selected items' `Message.Body`; then exit selection. Back press and `OnHidden` exit selection first (`HandleBack` returns true when it did).
- [ ] **Step 4: Build.** `dotnet build src/Gheychi.App -f net10.0-android` → `Build succeeded`, no new warnings in touched files.
- [ ] **Step 5: Commit** `feat: export selected conversations and spam as training data`.

---

### Task 6: App — import / revert in spam settings

**Files:**
- Modify: `src/Gheychi.App/Controls/Settings/SpamSettingsScreen.axaml(.cs)`
- Modify: `src/Gheychi.App/Localization/AppResources.resx`, `AppResources.fa.resx`

**Interfaces:**
- Consumes: Task 4's `ISpamModelUpdater` members.

| Key | English | فارسی |
|---|---|---|
| `Settings_ModelImport` | Import model | ایمپورت مدل |
| `Settings_ModelImportHint` | A .zip made by the Trainer | فایل zip ساخته‌شده با Trainer |
| `Settings_ModelImported` | Model {0} imported | مدل نسخه‌ی {0} ایمپورت شد |
| `Settings_ModelImportFailed` | This file is not a usable model | این فایل مدل قابل استفاده‌ای نیست |
| `Settings_ModelRevert` | Back to the official model | برگشت به مدل رسمی |
| `Settings_ModelRevertConfirm` | Remove your model and go back to the official one? | مدل خودتون حذف بشه و مدل رسمی برگرده؟ |
| `Settings_ModelCustom` | {0} (custom) | {0} (سفارشی) |
| `Settings_ModelCustomFailed` | Your model did not load; the official model is in use for now | مدل سفارشی بارگذاری نشد؛ فعلاً مدل رسمی استفاده می‌شه |

- [ ] **Step 1: Rows.** Under the model version/update rows add "Import model" (hint row) and "Back to the official model" (visible only when `IsCustomActive`). Hide the model-update row when `IsCustomActive`. Version text uses `Settings_ModelCustom` when custom. When `CustomLoadFailed`, show `Settings_ModelCustomFailed` as the version row's hint.
- [ ] **Step 2: Import.** `StorageProvider.OpenFilePickerAsync` (single file, filter `*.zip`); open the picked file's read stream; `ImportCustomAsync`; toast `Settings_ModelImported` (formatted version via existing `SettingsUi.Number`) or `Settings_ModelImportFailed`; refresh rows. Cancel → nothing.
- [ ] **Step 3: Revert.** `Sheet.AlertAsync(..., danger: true)` with `Settings_ModelRevertConfirm`; yes → `RemoveCustomAsync`, refresh rows, then run the existing update check path once so an official update can be offered again.
- [ ] **Step 4: Build** as Task 5 Step 4.
- [ ] **Step 5: Commit** `feat: import a custom spam model from settings and revert to the official one`.

---

### Task 7: End-to-end check on a device/emulator

- [ ] Install the debug build; in the inbox select 2 threads → Export for training → Normal → save; in the spam tab long-press, select 2 → Export → Spam → save. Pull both files, run Task 1's Trainer on a folder with them + `data.json`, import the zip in settings: version shows "(custom)", update row hidden; revert → official version back. Cancel paths (Review Focus 5) leave nothing behind.
