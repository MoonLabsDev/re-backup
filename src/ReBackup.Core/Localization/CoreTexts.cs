using System.Globalization;
using System.Text.RegularExpressions;
using ReBackup.Shared.Localization;

namespace ReBackup.Core.Localization;

/// <summary>
/// The English texts of the messages Core produces. The App shows messages in the chosen language from its label files
/// (en-US.json holds exactly these texts; a test checks it). Core needs English itself for what it writes to disk (the
/// run log stays English) and for exception messages. <see cref="Recognize"/> turns such an English text (a stored
/// reason, an exception message) back into its message, so the App can show it in the chosen language.
/// </summary>
public static class CoreTexts
{
    public static IReadOnlyDictionary<string, string> Templates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["core.plan.nameRequired"] = "Name is required.",
        ["core.plan.nameSpaces"] = "Name must not start or end with spaces.",
        ["core.plan.nameDot"] = "Name must not end with a dot.",
        ["core.plan.namePartial"] = "Name must not end with \".partial\".",
        ["core.plan.nameDeleting"] = "Name must not end with \".deleting\".",
        ["core.plan.nameChars"] = "Name contains characters that are not allowed in folder names.",
        ["core.plan.nameTaken"] = "Another plan is already named \"{name}\".",
        ["core.plan.sourceRequired"] = "Source folder is required.",
        ["core.plan.targetRequired"] = "Target folder is required.",
        ["core.plan.sourceNotAbsolute"] = "Source must be an absolute path.",
        ["core.plan.targetNotAbsolute"] = "Target must be an absolute path.",
        ["core.plan.sourceMissing"] = "Source folder does not exist.",
        ["core.plan.unsupportedLocation"] = "This kind of location is not supported yet.",
        ["core.plan.targetInsideSource"] = "Target must not be inside the source.",
        ["core.plan.sourceInsideTarget"] = "Source must not be inside the target.",
        ["core.plan.sourceConnectionRequired"] = "Choose an S3 connection for the source.",
        ["core.plan.targetConnectionRequired"] = "Choose an S3 connection for the target.",
        ["core.plan.sourceConnectionMissing"] = "The source's S3 connection no longer exists.",
        ["core.plan.targetConnectionMissing"] = "The target's S3 connection no longer exists.",
        ["core.plan.sourcePrefixInvalid"] =
            "The source prefix is not valid: separate folder names with a single /, with no / at the start or end and no \\, . or .. parts.",
        ["core.plan.targetPrefixInvalid"] =
            "The target prefix is not valid: separate folder names with a single /, with no / at the start or end and no \\, . or .. parts.",
        ["core.plan.connectionsUnreadable"] = "The S3 connections could not be read: {error}",
        ["core.plan.sourceDriveConnectionRequired"] = "Choose a Google Drive connection for the source.",
        ["core.plan.targetDriveConnectionRequired"] = "Choose a Google Drive connection for the target.",
        ["core.plan.sourceDriveConnectionMissing"] = "The source's Google Drive connection no longer exists.",
        ["core.plan.targetDriveConnectionMissing"] = "The target's Google Drive connection no longer exists.",
        ["core.plan.sourceDrivePathInvalid"] =
            "The source path is not valid: separate folder names with a single /, with no / at the start or end and no \\, . or .. parts.",
        ["core.plan.targetDrivePathInvalid"] =
            "The target path is not valid: separate folder names with a single /, with no / at the start or end and no \\, . or .. parts.",
        ["core.plan.targetNotSharedDrive"] = "Drive targets must be on a shared drive.",
        ["core.plan.driveConnectionsUnreadable"] = "The Google Drive connections could not be read: {error}",
        ["core.plan.retentionRule"] = "Retention rule {index}: {problem}",
        ["core.plan.trigger"] = "Trigger {index}: {problem}",

        ["core.run.nameUnusable"] = "The plan name \"{name}\" cannot be used: {problem}",
        ["core.run.sourceMissing"] = "Source folder \"{source}\" does not exist.",
        ["core.run.noTarget"] = "No target folder is set.",
        ["core.run.nameNotFolder"] = "The plan name \"{name}\" cannot be used as a folder name.",
        ["core.run.namePartial"] = "The plan name must not end with \".partial\".",
        ["core.run.nameDeleting"] = "The plan name must not end with \".deleting\".",
        ["core.run.targetInsideSource"] = "The target folder is the source folder or inside it.",
        ["core.run.sourceInsideTarget"] = "The source folder is inside the target folder.",
        ["core.run.sourceUnreadable"] = "The source folder could not be read: {error}",
        ["core.run.notEnoughSpace"] = "The backup needs {required} but only {free} is free on the target.",
        ["core.run.targetFull"] = "The target ran out of space during the backup.",
        ["core.run.sourceGone"] = "The source folder is no longer available.",
        ["core.run.sourceEmpty"] = "The source is empty — no version was written and retention did not run.",
        ["core.run.sourceReadDenied"] = "The source refused to open every file: the access key may list but not read them (S3: s3:GetObject, or kms:Decrypt for the bucket's KMS key). No version was written and retention did not run.",
        ["core.run.retentionFailed"] = "Retention was skipped: {error}",
        ["core.run.retentionRulesUnreadable"] = "Retention was skipped: the plan's current rules could not be read: {error}",
        ["core.run.retentionPlanGone"] = "Retention was skipped: the plan no longer exists.",
        ["core.run.retentionDeleteFailed"] = "Retention could not delete \"{version}\": {error}",
        ["core.run.freeSpaceRulesUnreadable"] =
            "Old versions were not deleted to free space: the plan's current rules could not be read: {error}",
        ["core.run.freeSpacePlanGone"] = "Old versions were not deleted to free space: the plan no longer exists.",
        ["core.run.freeSpaceExamineFailed"] = "Old versions could not be examined to free space: {error}",
        ["core.run.freeSpaceDeleteFailed"] = "\"{version}\" could not be deleted to free space: {error}",
        ["core.run.freeSpaceNoCollect"] = "Old versions were not deleted to free space, because the space they hold could not be freed now. {reason}",
        ["core.run.remainsFailed"] = "Remains of an earlier removal could not be deleted (\"{name}\"): {error}",
        ["core.run.removedRemainsLeft"] =
            "\"{version}\" was removed from the versions, but its remains could not be deleted yet: {error}",
        ["core.run.unfinishedFailed"] = "An unfinished backup could not be deleted (\"{name}\"): {error}",
        ["core.run.leftoverForeign"] = "\"{name}\" was left alone: it is marked by another plan.",
        ["core.run.leftoverUnreadable"] = "\"{name}\" was left alone: its marker cannot be read.",
        ["core.run.leftoverRenamed"] = "\"{name}\" was left alone: it is marked by this plan but was renamed.",
        ["core.run.leftoverOtherHostPending"] = "\"{name}\" was left alone: it is being written by {host} since {time}.",
        ["core.run.leftoverOtherHostDeleting"] = "\"{name}\" was left alone: it is being deleted by {host} since {time}.",
        ["core.run.takeOverChanged"] = "\"{name}\" was not taken over: its marker has changed meanwhile.",
        ["core.run.takeOverUnexamined"] = "\"{name}\" could not be examined and was left as it is.",
        ["core.run.leftoverPartialWithManifest"] = "\"{name}\" was left alone: it looks unfinished but holds a manifest.",
        ["core.run.indexFailed"] = "The version index could not be updated: {error}",
        ["core.run.storeFullOnPool"] = "The target holds an incremental pool, but this plan is a full plan. Nothing was changed.",
        ["core.run.storePoolOnFull"] = "This plan is incremental, but the target already holds full versions of it. Nothing was changed.",
        ["core.run.storeOtherPlan"] = "The target's pool belongs to another plan. Nothing was changed.",
        ["core.run.storeUnsupported"] = "The target's storage format is not supported by this version of ReBackup. Nothing was changed.",
        ["core.run.storeNotEmpty"] = "This plan is incremental, but the target already holds backups of another plan. Choose an empty target. Nothing was changed.",
        ["core.run.targetLocked"] = "The target is in use by {host} ({operation}) since {since}. Nothing was changed.",
        ["core.run.lockReleaseFailed"] = "The target's lock could not be released: {error}",
        ["core.run.copyRunning"] = "The plan is being copied (\"Copy plan…\"); the backup was not started.",
        ["core.run.deleteRunning"] = "Versions of the plan are being deleted; the backup was not started.",
        ["core.run.copyUnfinished"] = "The plan is an unfinished copy; finish it with \"Continue copy…\" first. The backup was not started.",

        ["core.gc.skippedMarkers"] = "Unused backup content was not removed: a version is still being written or deleted.",
        ["core.gc.manifestUnreadable"] = "Unused backup content was not removed: the manifest of \"{version}\" cannot be read.",
        ["core.gc.failed"] = "Unused backup content could not be removed: {error}",

        ["core.copy.overlaps"] = "The new target is the plan's target, lies inside it or holds it. Nothing was copied.",
        ["core.copy.failed"] = "The copy stopped: {error}",
        ["core.copy.versionFailed"] = "\"{version}\" was not copied: {error}",
        ["core.copy.fileFailed"] = "\"{path}\" in \"{version}\" was not copied: {error}",
        ["core.copy.manifestUnreadable"] = "\"{version}\" was not copied: its manifest cannot be read.",
        ["core.copy.notOwned"] = "Folders named like versions of the plan that were not copied because they were renamed by hand, belong to another plan or have no manifest: {count}",
        ["core.copy.folderTaken"] = "\"{version}\" was not copied: the new target already holds a folder \"{folder}\".",

        ["core.skip.ignoreFileUnreadable"] = "ignore file could not be read; its patterns were not applied",
        ["core.skip.reservedName"] = "the name is reserved for ReBackup's own files",
        // The text runs before the marker files wrote; kept so that their logs are still recognized and translated.
        ["core.skip.reservedNameLegacy"] = "the name is reserved for the backup manifest",
        ["core.skip.changed"] = "changed while it was copied; the copy may be inconsistent",

        ["core.file.noLongerExists"] = "no longer exists",
        ["core.file.accessDenied"] = "access denied",
        ["core.file.locked"] = "locked by another program",
        ["core.file.archived"] = "archived (e.g. S3 Glacier); it must be restored before it can be read",
        ["core.file.cannotOpen"] = "cannot be opened: {error}",
        ["core.file.notAFolder"] = "\"{name}\" is not a folder (or is a link) and is not removed.",
        ["core.file.inTransit"] = "\"{name}\" is being written or deleted by another run and is not removed.",

        ["core.scan.link"] = "Link is not followed.",
        ["core.scan.tooDeep"] = "Folder nesting too deep.",

        ["core.restore.destinationNotAbsolute"] = "The destination must be an absolute path.",
        ["core.restore.versionMissing"] = "The version folder \"{folder}\" does not exist.",
        ["core.restore.destinationInVersion"] = "The destination must not be the version folder or inside it.",
        ["core.restore.outside"] = "\"{path}\" is outside the version.",
        ["core.restore.throughLink"] = "\"{path}\" goes through the link \"{link}\".",
        ["core.restore.sameName"] = "Two selected items are called \"{name}\"; they would end up in the same place.",
        ["core.restore.manifest"] = "The manifest is not part of the backup.",
        ["core.restore.notInVersion"] = "\"{path}\" does not exist in the version.",
        ["core.restore.folderExists"] = "A folder with this name exists.",
        ["core.restore.destinationLink"] = "the destination folder is a link",
        ["core.restore.fileInPlaceOfFolder"] = "a file stands where the folder \"{folder}\" belongs",
        ["core.restore.notAPath"] = "\"{path}\" is not a path inside the version.",
        ["core.restore.accessDenied"] = "access denied (read-only, or in use by another program)",
        ["core.restore.contentMismatch"] = "the content does not match its checksum in the backup (the backup copy is damaged)",
        ["core.restore.blobMissing"] = "its content is missing from the backup's pool (the backup is damaged)",

        ["core.index.targetUnavailable"] = "The target folder \"{folder}\" is not available.",

        ["core.manifest.empty"] = "The manifest is empty.",
        ["core.manifest.unreadable"] = "The manifest cannot be read: {error}",
        ["core.manifest.endsEarly"] = "The manifest ends too early.",
        ["core.manifest.valueTooLarge"] = "A single value of the manifest is too large.",
        ["core.manifest.notObject"] = "A manifest is a JSON object.",
        ["core.manifest.entryNotObject"] = "A file entry is a JSON object.",
        ["core.manifest.entryNoPath"] = "A file entry has no path.",

        ["core.planFile.empty"] = "File is empty.",
        ["core.planFile.idMismatch"] = "Plan id \"{id}\" does not match file name \"{file}\".",
        ["core.planFile.emptyTrigger"] = "The list of triggers contains an empty entry.",
        ["core.planFile.emptyRule"] = "The list of retention rules contains an empty entry.",

        ["core.config.containsConfiguration"] = "The chosen folder already contains a ReBackup configuration.",
    };

    /// <summary>The message in English; nested messages too. An unknown key is shown as the key.</summary>
    public static string English(Message message) =>
        LabelFormat.Format(Templates.TryGetValue(message.Key, out var template) ? template : message.Key,
            message.Args, CultureInfo.InvariantCulture, English);

    public static string English(string key, params (string Name, object? Value)[] args) => English(Message.Of(key, args));

    /// <summary><see cref="English(Message)"/>, or null for no message.</summary>
    public static string? ToEnglish(this Message? message) => message is null ? null : English(message);

    private static readonly Regex ParameterSuffix = new(@" \(Parameter '[^']*'\)\z", RegexOptions.CultureInvariant);

    /// <summary>The templates <see cref="Recognize"/> tries, the most literal text first (the most specific).</summary>
    private static readonly Lazy<string[]> RecognitionOrder = new(() => Templates
        .Where(pair => LabelFormat.LiteralLength(pair.Value) > 0)
        .OrderByDescending(pair => LabelFormat.LiteralLength(pair.Value))
        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key)
        .ToArray());

    /// <summary>
    /// The message an English text Core produced came from, its text arguments recognized as well; null when no
    /// template matches (e.g. a message from Windows). The <c> (Parameter '…')</c> an <see cref="ArgumentException"/>
    /// appends is ignored.
    /// </summary>
    public static Message? Recognize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        if (MatchTemplate(text) is { } message)
            return message;
        var suffix = ParameterSuffix.Match(text);
        return suffix.Success ? MatchTemplate(text[..suffix.Index]) : null;
    }

    private static Message? MatchTemplate(string text)
    {
        foreach (var key in RecognitionOrder.Value)
        {
            if (LabelFormat.Match(Templates[key], text) is not { } args)
                continue;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in args)
                values[name] = Recognize(value) is { } inner ? inner : value;
            return new Message(key, values);
        }
        return null;
    }
}
