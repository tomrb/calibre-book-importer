using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CalibreBookImporter;

public sealed class CalibreImporter
{
    private static readonly string[] SupportedExtensions =
        [".epub", ".mobi", ".azw3", ".pdf", ".lit", ".rtf", ".html", ".htm"];
    private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CalibreToolPaths _tools;
    private readonly string? _calibreDirectory;

    public CalibreImporter(string? calibreDirectory = null)
    {
        _calibreDirectory = calibreDirectory;
        _tools = CalibreToolPaths.Find(calibreDirectory);
    }

    public static IReadOnlyList<SourceBook> Scan(
        string sourceDirectory,
        IEnumerable<string>? requestedTitles = null,
        string? calibreDirectory = null,
        IReadOnlyList<BookMetadata>? selectedBooks = null,
        IEnumerable<string>? requestedAuthors = null)
    {
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        var requestedInput = requestedTitles?.Select(BookMatching.Normalize).Where(value => value.Length > 0).ToArray();
        var selected = (selectedBooks ?? [])
            .Where(book => requestedInput is null || requestedInput.Length == 0 ||
                           requestedInput.Contains(BookMatching.Normalize(book.Title), StringComparer.Ordinal))
            .ToArray();
        var requested = (requestedInput is { Length: > 0 }
                ? requestedInput
                : selected.Select(book => BookMatching.Normalize(book.Title)))
            .ToArray();
        var requestedAuthorsNormalized = (requestedAuthors ?? [])
            .Select(BookMatching.Normalize)
            .Where(value => value.Length > 0)
            .ToArray();
        string? ebookMeta = null;
        try
        {
            ebookMeta = CalibreToolPaths.Find(calibreDirectory).EbookMeta;
        }
        catch (CalibreToolingException)
        {
            if (!string.IsNullOrWhiteSpace(calibreDirectory))
            {
                throw;
            }
        }

        var groups = new Dictionary<string, List<SourceEntry>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var relativePathKey = BookMatching.Normalize(
                BookMatching.SanitizeTitle(Path.GetRelativePath(root, path)));
            var titleCandidates = selected
                .Where(book => BookMatching.ContainsSearchTerm(relativePathKey, book.Title))
                .ToArray();
            var authorCandidates = selected
                .Where(book => book.Authors.Any(author =>
                    BookMatching.ContainsSearchTerm(relativePathKey, author)))
                .ToArray();
            BookMetadata? metadata = null;
            var possibleMatch = false;
            if (selected.Length > 0)
            {
                if (titleCandidates.Length == 0 && authorCandidates.Length == 0)
                {
                    continue;
                }
            }
            else if ((requested.Length > 0 || requestedAuthorsNormalized.Length > 0) &&
                     !requested.Concat(requestedAuthorsNormalized).Any(term =>
                         BookMatching.ContainsSearchTerm(relativePathKey, term)))
            {
                continue;
            }

            var extracted = ReadMetadata(path, ebookMeta);
            if (selected.Length > 0)
            {
                var extractedTitleKey = BookMatching.Normalize(
                    BookMatching.SanitizeTitle(extracted.Title));
                metadata = selected.FirstOrDefault(book =>
                    extractedTitleKey == BookMatching.Normalize(book.Title) &&
                    !string.IsNullOrWhiteSpace(extracted.Author) &&
                    BookMatching.NormalizeAuthor(extracted.Author) ==
                    BookMatching.NormalizeAuthor(book.AuthorText));

                if (metadata is null && titleCandidates.Length == 1 &&
                    extractedTitleKey == BookMatching.Normalize(titleCandidates[0].Title) &&
                    (string.IsNullOrWhiteSpace(extracted.Author) ||
                     BookMatching.NormalizeAuthor(extracted.Author) ==
                     BookMatching.NormalizeAuthor(titleCandidates[0].AuthorText)))
                {
                    metadata = titleCandidates[0];
                }

                possibleMatch = metadata is null;
            }
            var title = metadata?.Title ?? BookMatching.SanitizeTitle(extracted.Title);
            var author = metadata?.AuthorText ?? extracted.Author.Trim();
            var key = BookMatching.BuildGroupKey(title, author) + (possibleMatch ? "|possible" : "");
            if (!groups.TryGetValue(key, out var entries))
            {
                entries = [];
                groups.Add(key, entries);
            }
            entries.Add(new SourceEntry(title, author, path, metadata, possibleMatch));
        }

        return groups.Values.Select(entries =>
            {
                var paths = entries.Select(entry => entry.Path).ToArray();
                var preferred = BookMatching.PreferredFile(paths);
                var first = entries[0];
                return new SourceBook(first.Title, first.Author, paths, preferred, first.Metadata,
                    entries.Any(entry => entry.IsPossibleMatch));
            })
            .OrderBy(book => BookMatching.Normalize(book.Title), StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<BookAction> Compare(
        string sourceDirectory,
        string libraryPath,
        IEnumerable<string>? requestedTitles = null,
        IReadOnlyList<BookMetadata>? selectedBooks = null)
    {
        var metadata = SelectMetadata(selectedBooks, requestedTitles);
        var books = Scan(sourceDirectory, requestedTitles, _calibreDirectory, metadata);
        var library = ListLibraryRecords(libraryPath);
        var actions = new List<BookAction>();
        foreach (var book in books)
        {
            if (book.IsPossibleMatch)
            {
                actions.Add(new BookAction(
                    book.Title,
                    book.Author,
                    "possible_source_match",
                    book.PreferredFormat,
                    book.PreferredPath,
                    Reason: "The path matched a selected title or author, but ebook metadata did not verify the exact selected book."));
                continue;
            }
            var match = ResolveMatch(library, book);
            actions.Add(new BookAction(
                book.Title,
                book.Author,
                match.Status,
                book.PreferredFormat,
                book.PreferredPath,
                match.Record?.Id,
                Reason: match.Reason,
                ExistingFormats: match.Record?.Formats ?? []));
        }

        AddMissingSourceActions(actions, metadata);
        return actions;
    }

    public IReadOnlyList<BookAction> Import(
        string sourceDirectory,
        string libraryPath,
        IEnumerable<string> requestedTitles,
        IReadOnlyList<string> targetFormats,
        bool convertWhenNeeded,
        bool dryRun,
        bool updateOnly,
        bool replaceExisting,
        IReadOnlyList<BookMetadata>? selectedBooks = null)
    {
        if (replaceExisting && updateOnly)
        {
            throw new ArgumentException("--replace-existing cannot be used with update.");
        }

        var formats = targetFormats.Select(NormalizeFormat).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (formats.Length == 0)
        {
            throw new ArgumentException("Select at least one target format.");
        }

        var metadata = SelectMetadata(selectedBooks, requestedTitles);
        var books = Scan(sourceDirectory, requestedTitles, _calibreDirectory, metadata);
        var library = ListLibraryRecords(libraryPath);
        var actions = new List<BookAction>();

        foreach (var book in books)
        {
            if (book.IsPossibleMatch)
            {
                actions.Add(new BookAction(
                    book.Title,
                    book.Author,
                    "possible_source_match",
                    book.PreferredFormat,
                    book.PreferredPath,
                    Reason: "The path matched a selected title or author, but ebook metadata did not verify the exact selected book."));
                continue;
            }
            var match = ResolveMatch(library, book);
            var record = match.Record;
            if (match.Status == "ambiguous")
            {
                actions.Add(new BookAction(book.Title, book.Author, "ambiguous", book.PreferredFormat,
                    book.PreferredPath, Reason: match.Reason));
                continue;
            }
            if (updateOnly && record is null)
            {
                actions.Add(new BookAction(book.Title, book.Author, "not_in_library", book.PreferredFormat,
                    book.PreferredPath, Reason: "Update only attaches formats to an exact existing Calibre match."));
                continue;
            }
            if (!updateOnly && record is not null && !replaceExisting)
            {
                actions.Add(new BookAction(book.Title, book.Author, "already_in_calibre", book.PreferredFormat,
                    book.PreferredPath, record.Id, Reason: "Existing Calibre record skipped; replacement was not requested.",
                    ExistingFormats: record.Formats));
                continue;
            }

            var pending = BuildFormatPlan(book, record, formats, convertWhenNeeded, dryRun, updateOnly, replaceExisting);
            if (dryRun || (pending.Files.Count == 0 && !updateOnly))
            {
                actions.AddRange(pending.Actions);
                continue;
            }

            string? coverPath = null;
            string? backupDirectory = null;
            string? targetId = record?.Id;
            var preserveBackup = false;
            try
            {
                coverPath = PrepareCover(book.Metadata);
                if (record is not null && replaceExisting)
                {
                    backupDirectory = BackupAndRemove(libraryPath, record.Id);
                    targetId = null;
                }
                if (record is null || replaceExisting)
                {
                    AddNewRecord(libraryPath, book, pending.Files[0].Path);
                    library = ListLibraryRecords(libraryPath);
                    var added = BookMatching.ResolveExactMatch(library, book.Title, book.Author);
                    if (added.Status != "already_in_calibre" || added.Record is null)
                    {
                        throw new CalibreToolingException(
                            $"Could not verify the new Calibre record for '{book.Title}': {added.Reason}");
                    }
                    targetId = added.Record.Id;
                    ApplyMetadata(libraryPath, targetId, book.Metadata, coverPath);
                    var firstActionIndex = pending.Files[0].ActionIndex;
                    pending.Actions[firstActionIndex] = pending.Actions[firstActionIndex] with
                    {
                        Status = replaceExisting ? "replaced" : "imported",
                        LibraryId = targetId
                    };
                    for (var index = 1; index < pending.Files.Count; index++)
                    {
                        AddFormat(libraryPath, targetId, pending.Files[index]);
                        var actionIndex = pending.Files[index].ActionIndex;
                        pending.Actions[actionIndex] = pending.Actions[actionIndex] with
                        {
                            Status = "format_added",
                            LibraryId = targetId
                        };
                    }
                }
                else if (targetId is not null)
                {
                    foreach (var file in pending.Files)
                    {
                        AddFormat(libraryPath, targetId, file);
                        pending.Actions[file.ActionIndex] = pending.Actions[file.ActionIndex] with
                        {
                            Status = "format_added",
                            LibraryId = targetId
                        };
                    }
                    ApplyMetadata(libraryPath, targetId, book.Metadata, coverPath);
                }

                library = ListLibraryRecords(libraryPath);
                VerifyFormats(library, book, targetId, pending.Files.Select(file => file.Format));
                if (book.Metadata is not null && targetId is not null)
                {
                    VerifyMetadata(libraryPath, targetId, book.Metadata, coverPath);
                }
            }
            catch (Exception exception) when (backupDirectory is not null)
            {
                try
                {
                    if (targetId is null)
                    {
                        var current = ListLibraryRecords(libraryPath);
                        var partialRecord = BookMatching.ResolveExactMatch(current, book.Title, book.Author).Record;
                        targetId = partialRecord?.Id;
                    }
                    if (targetId is not null)
                    {
                        EnsureSuccess(RunCalibreDb(libraryPath, ["remove", targetId]), "calibredb rollback");
                    }
                    RestoreBackup(libraryPath, backupDirectory);
                }
                catch (Exception restoreException)
                {
                    preserveBackup = true;
                    throw new InvalidOperationException(
                        $"Replacement failed for '{book.Title}' and restoring its backup also failed. " +
                        $"The original backup remains at '{backupDirectory}'. {restoreException.Message}",
                        new AggregateException(exception, restoreException));
                }
                throw;
            }
            finally
            {
                if (!preserveBackup && backupDirectory is not null && Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
                if (coverPath is not null && string.IsNullOrWhiteSpace(book.Metadata?.CoverPath) &&
                    Directory.Exists(Path.GetDirectoryName(coverPath)))
                {
                    Directory.Delete(Path.GetDirectoryName(coverPath)!, recursive: true);
                }
                foreach (var file in pending.Files.Where(file => file.Temporary))
                {
                    var directory = Path.GetDirectoryName(file.Path);
                    if (directory is not null && Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
            }
            actions.AddRange(pending.Actions);
        }

        AddMissingSourceActions(actions, metadata);
        return actions;
    }

    public string Convert(string sourceFile, string targetFormat = "azw3", string? outputFile = null)
    {
        var source = Path.GetFullPath(sourceFile);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Source ebook not found: {source}", source);
        }
        if (_tools.EbookConvert is null)
        {
            throw new CalibreToolingException("Could not locate ebook-convert. Install Calibre or pass --calibre-dir.");
        }
        var target = NormalizeFormat(targetFormat);
        var output = outputFile is null
            ? Path.Combine(
                Path.GetTempPath(),
                "CalibreBookImporter-convert-" + Guid.NewGuid().ToString("N"),
                Path.GetFileNameWithoutExtension(source) + "." + target)
            : Path.GetFullPath(outputFile);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Conversion output must not overwrite the source ebook.");
        }
        if (File.Exists(output))
        {
            throw new IOException($"Conversion output already exists: {output}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = CalibreToolPaths.Run(_tools.EbookConvert, [source, output, "--output-profile=kindle"]);
        EnsureSuccess(result, "ebook-convert");
        if (!File.Exists(output))
        {
            throw new CalibreToolingException($"Conversion did not create the expected output: {output}");
        }
        return output;
    }

    public IReadOnlyList<LibraryBook> ListLibraryRecords(string libraryPath)
    {
        var fullLibraryPath = Path.GetFullPath(libraryPath);
        if (!Directory.Exists(fullLibraryPath))
        {
            throw new DirectoryNotFoundException($"Calibre library not found: {libraryPath}");
        }

        var result = CalibreToolPaths.Run(_tools.CalibreDb,
            [$"--with-library={fullLibraryPath}", "list", "--for-machine", "--fields=title,authors,formats"]);
        EnsureSuccess(result, "calibredb list");
        return ParseLibraryListing(result.StandardOutput);
    }

    public static IReadOnlyList<LibraryBook> ParseLibraryListing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var records = new List<LibraryBook>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            records.AddRange(document.RootElement.EnumerateArray().Select(item => ParseLibraryBook(item, null)));
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            records.AddRange(document.RootElement.EnumerateObject()
                .Select(item => ParseLibraryBook(item.Value, item.Name)));
        }
        else
        {
            throw new JsonException("Expected a JSON array or object from calibredb.");
        }
        return records.Where(record => record.Id.Length > 0).ToArray();
    }

    private static (string Status, LibraryBook? Record, string Reason) ResolveMatch(
        IReadOnlyList<LibraryBook> library,
        SourceBook book)
    {
        var exact = BookMatching.ResolveExactMatch(library, book.Title, book.Author);
        return (exact.Status, exact.Record, exact.Reason);
    }

    private FormatPlan BuildFormatPlan(
        SourceBook book,
        LibraryBook? existing,
        IReadOnlyList<string> targetFormats,
        bool convertWhenNeeded,
        bool dryRun,
        bool updateOnly,
        bool replaceExisting)
    {
        var files = new List<PlannedFormat>();
        var actions = new List<BookAction>();
        foreach (var format in targetFormats)
        {
            if (updateOnly && existing?.Formats.Contains(format, StringComparer.OrdinalIgnoreCase) == true)
            {
                actions.Add(new BookAction(book.Title, book.Author, "already_has_format", format,
                    book.PreferredPath, existing.Id, Reason: $"Calibre already has the {format} format.",
                    ExistingFormats: existing.Formats));
                continue;
            }

            var path = book.SourcePaths.FirstOrDefault(candidate =>
                Path.GetExtension(candidate).Equals("." + format, StringComparison.OrdinalIgnoreCase));
            if (path is null)
            {
                if (!convertWhenNeeded)
                {
                    actions.Add(new BookAction(book.Title, book.Author, "skipped", format, book.PreferredPath,
                        existing?.Id, Reason: $"No {format} source exists and conversion is disabled.",
                        ExistingFormats: existing?.Formats));
                    continue;
                }
                var directory = Path.Combine(
                    Path.GetTempPath(),
                    dryRun
                        ? "CalibreBookImporter-convert-dry-run"
                        : "CalibreBookImporter-convert-" + Guid.NewGuid().ToString("N"));
                path = Path.Combine(directory, Path.GetFileNameWithoutExtension(book.PreferredPath) + "." + format);
                if (!dryRun)
                {
                    var converted = ConvertSource(book.PreferredPath, path);
                    path = converted;
                }
            }

            var actionIndex = actions.Count;
            actions.Add(new BookAction(book.Title, book.Author,
                dryRun
                    ? replaceExisting ? "would_replace" : updateOnly ? "would_update" : "would_add"
                    : "pending",
                format,
                book.PreferredPath,
                existing?.Id,
                path,
                dryRun
                    ? $"Would add {format} to the Calibre record."
                    : $"Planned {format} import.",
                existing?.Formats));
            files.Add(new PlannedFormat(format, path, actionIndex, !dryRun && !book.SourcePaths.Contains(path)));
        }
        return new FormatPlan(files, actions);
    }

    private string ConvertSource(string source, string output)
    {
        if (_tools.EbookConvert is null)
        {
            throw new CalibreToolingException("Could not locate ebook-convert. Install Calibre or pass --calibre-dir.");
        }
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        try
        {
            var result = CalibreToolPaths.Run(_tools.EbookConvert, [source, output, "--output-profile=kindle"]);
            EnsureSuccess(result, "ebook-convert");
            if (!File.Exists(output))
            {
                throw new CalibreToolingException($"Conversion did not create the expected output: {output}");
            }
            return output;
        }
        catch
        {
            if (Path.GetFileName(directory).StartsWith("CalibreBookImporter-convert-", StringComparison.Ordinal) &&
                Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            throw;
        }
    }

    private void AddNewRecord(string libraryPath, SourceBook book, string firstFormatPath)
    {
        var metadata = book.Metadata;
        var arguments = new List<string>
        {
            $"--with-library={Path.GetFullPath(libraryPath)}",
            "add",
            "--title", metadata?.Title ?? book.Title,
            "--authors", metadata?.AuthorText ?? book.Author
        };
        if (metadata is not null)
        {
            foreach (var isbn in metadata.Isbns.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                arguments.AddRange(["--identifier", $"isbn:{isbn}"]);
            }
            AddOption(arguments, "--tags", string.Join(",", metadata.Subjects));
            AddOption(arguments, "--series", metadata.Series);
            AddOption(arguments, "--series-index", metadata.SeriesIndex);
            AddOption(arguments, "--languages", string.Join(",", metadata.Languages));
        }
        arguments.Add(firstFormatPath);
        EnsureSuccess(CalibreToolPaths.Run(_tools.CalibreDb, arguments), "calibredb add");
    }

    private void AddFormat(string libraryPath, string id, PlannedFormat file)
    {
        EnsureSuccess(RunCalibreDb(libraryPath,
            ["add_format", "--dont-replace", id, file.Path]), $"calibredb add_format ({file.Format})");
    }

    private string? PrepareCover(BookMetadata? metadata)
    {
        if (metadata is null || (string.IsNullOrWhiteSpace(metadata.CoverPath) && string.IsNullOrWhiteSpace(metadata.CoverUrl)))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(metadata.CoverPath))
        {
            var path = Path.GetFullPath(metadata.CoverPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Cover image not found: {path}", path);
            }
            ValidateCoverDimensions(path);
            return path;
        }

        if (!Uri.TryCreate(metadata.CoverUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("covers.openlibrary.org", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cover URLs must use HTTPS on covers.openlibrary.org.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "CalibreBookImporter-cover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "cover" + Path.GetExtension(uri.AbsolutePath));
            var bytes = CoverClient.GetByteArrayAsync(uri).GetAwaiter().GetResult();
            File.WriteAllBytes(output, bytes);
            ValidateCoverDimensions(output);
            return output;
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private void ApplyMetadata(string libraryPath, string id, BookMetadata? metadata, string? coverPath)
    {
        if (metadata is null)
        {
            return;
        }
        var arguments = new List<string>
        {
            $"--with-library={Path.GetFullPath(libraryPath)}",
            "set_metadata",
            id
        };
        AddField(arguments, "title", metadata.Title);
        AddField(arguments, "authors", metadata.AuthorText);
        AddField(arguments, "comments", metadata.Description);
        AddField(arguments, "publisher", metadata.Publisher);
        AddField(arguments, "pubdate", metadata.Published);
        AddField(arguments, "tags", string.Join(",", metadata.Subjects));
        AddField(arguments, "languages", string.Join(",", metadata.Languages));
        AddField(arguments, "series", metadata.Series);
        AddField(arguments, "series_index", metadata.SeriesIndex);
        if (metadata.Isbns.Count > 0)
        {
            AddField(arguments, "identifiers", "isbn:" + metadata.Isbns[0]);
        }
        AddField(arguments, "cover", coverPath ?? "");
        if (arguments.Count > 3)
        {
            EnsureSuccess(CalibreToolPaths.Run(_tools.CalibreDb, arguments), "calibredb set_metadata");
        }
    }

    private void VerifyMetadata(string libraryPath, string id, BookMetadata metadata, string? coverPath)
    {
        var result = RunCalibreDb(libraryPath, ["show_metadata", "--as-opf", id]);
        EnsureSuccess(result, "calibredb show_metadata");
        var document = XDocument.Parse(result.StandardOutput);
        var dc = XNamespace.Get("http://purl.org/dc/elements/1.1/");
        var title = document.Descendants(dc + "title").Select(element => element.Value).FirstOrDefault() ?? "";
        if (BookMatching.Normalize(title) != BookMatching.Normalize(metadata.Title))
        {
            throw new CalibreToolingException($"Calibre did not verify the selected title metadata for '{metadata.Title}'.");
        }
        var authors = document.Descendants(dc + "creator").Select(element => element.Value).ToArray();
        if (BookMatching.NormalizeAuthor(string.Join(" & ", authors)) != BookMatching.NormalizeAuthor(metadata.AuthorText))
        {
            throw new CalibreToolingException($"Calibre did not verify the selected author metadata for '{metadata.Title}'.");
        }

        VerifyField(document, dc + "description", metadata.Description, "description", metadata.Title);
        VerifyField(document, dc + "publisher", metadata.Publisher, "publisher", metadata.Title);
        VerifyField(document, dc + "date", metadata.Published, "publication date", metadata.Title);
        foreach (var subject in metadata.Subjects)
        {
            VerifyField(document, dc + "subject", subject, "subject", metadata.Title);
        }
        foreach (var language in metadata.Languages)
        {
            VerifyField(document, dc + "language", language, "language", metadata.Title);
        }
        if (!string.IsNullOrWhiteSpace(metadata.Series) &&
            !document.Descendants().Any(element =>
                element.Name.LocalName == "meta" &&
                element.Attribute("name")?.Value == "calibre:series" &&
                BookMatching.Normalize(element.Attribute("content")?.Value) == BookMatching.Normalize(metadata.Series)))
        {
            throw new CalibreToolingException($"Calibre did not verify the selected series metadata for '{metadata.Title}'.");
        }
        if (!string.IsNullOrWhiteSpace(coverPath) &&
            !document.Descendants().Any(element =>
                element.Name.LocalName == "meta" &&
                element.Attribute("name")?.Value == "cover"))
        {
            throw new CalibreToolingException($"Calibre did not verify the selected cover for '{metadata.Title}'.");
        }
    }

    private static void VerifyField(
        XDocument document,
        XName fieldName,
        string expected,
        string displayName,
        string title)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }
        if (!document.Descendants(fieldName)
            .Any(element => BookMatching.Normalize(element.Value) == BookMatching.Normalize(expected)))
        {
            throw new CalibreToolingException($"Calibre did not verify the selected {displayName} for '{title}'.");
        }
    }

    private void VerifyFormats(
        IReadOnlyList<LibraryBook> library,
        SourceBook book,
        string? id,
        IEnumerable<string> formats)
    {
        var record = library.FirstOrDefault(item =>
            (id is not null && item.Id == id) ||
            (BookMatching.Normalize(item.Title) == BookMatching.Normalize(book.Title) &&
             BookMatching.NormalizeAuthor(item.Authors) == BookMatching.NormalizeAuthor(book.Author)));
        if (record is null)
        {
            throw new CalibreToolingException($"Could not verify the Calibre record for '{book.Title}'.");
        }
        foreach (var format in formats)
        {
            if (!record.Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                throw new CalibreToolingException($"Calibre did not verify the {format} format for '{book.Title}'.");
            }
        }
    }

    private string BackupAndRemove(string libraryPath, string id)
    {
        var backup = Path.Combine(Path.GetTempPath(), "CalibreBookImporter-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var exported = false;
        try
        {
            EnsureSuccess(RunCalibreDb(libraryPath, ["export", "--to-dir", backup, id]), "calibredb export");
            if (!Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any())
            {
                throw new CalibreToolingException($"Calibre export did not create a backup for record {id}.");
            }
            exported = true;
            EnsureSuccess(RunCalibreDb(libraryPath, ["remove", id]), "calibredb remove");
            return backup;
        }
        catch (Exception exception)
        {
            if (exported)
            {
                try
                {
                    var remaining = ListLibraryRecords(libraryPath);
                    if (remaining.All(record => record.Id != id))
                    {
                        RestoreBackup(libraryPath, backup);
                    }
                }
                catch (Exception restoreException)
                {
                    throw new InvalidOperationException(
                        $"Could not safely finish replacement setup. The original backup remains at '{backup}'.",
                        new AggregateException(exception, restoreException));
                }
            }
            Directory.Delete(backup, recursive: true);
            throw;
        }
    }

    private void RestoreBackup(string libraryPath, string backupDirectory) =>
        EnsureSuccess(RunCalibreDb(libraryPath,
            ["add", "--one-book-per-directory", "--recurse", backupDirectory]), "calibredb backup restore");

    private ProcessResult RunCalibreDb(string libraryPath, IReadOnlyList<string> arguments) =>
        CalibreToolPaths.Run(_tools.CalibreDb,
            [$"--with-library={Path.GetFullPath(libraryPath)}", .. arguments]);

    private static LibraryBook ParseLibraryBook(JsonElement item, string? keyId)
    {
        var id = GetValue(item, "id");
        if (id.Length == 0)
        {
            id = keyId ?? "";
        }
        var title = GetValue(item, "title");
        var authors = GetValue(item, "authors");
        var formats = item.TryGetProperty("formats", out var formatsValue)
            ? formatsValue.ValueKind switch
            {
                JsonValueKind.Array => formatsValue.EnumerateArray().Select(value => value.ToString()),
                JsonValueKind.String => (formatsValue.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _ => []
            }
            : [];
        return new LibraryBook(id, title, authors,
            formats.Select(value => Path.GetExtension(value) is { Length: > 1 } extension
                    ? extension[1..].ToUpperInvariant()
                    : value.ToUpperInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static string GetValue(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(element => element.ToString())),
            _ => ""
        };
    }

    private static (string Title, string Author) ReadMetadata(string path, string? ebookMeta)
    {
        if (!string.IsNullOrWhiteSpace(ebookMeta))
        {
            var result = CalibreToolPaths.Run(ebookMeta, [path]);
            if (result.ExitCode == 0)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in result.StandardOutput.SplitLines())
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    var key = line[..separator].Trim();
                    if (key is "Title" or "Author(s)" or "Authors" or "Author")
                    {
                        values[key] = line[(separator + 1)..].Trim();
                    }
                }
                var title = values.GetValueOrDefault("Title");
                var author = values.GetValueOrDefault("Author(s)") ??
                             values.GetValueOrDefault("Authors") ??
                             values.GetValueOrDefault("Author") ?? "";
                author = Regex.Replace(author, @"\s+\[[^\]]+\]$", "").Trim();
                if (author.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    author.Equals("Unknown author", StringComparison.OrdinalIgnoreCase))
                {
                    author = "";
                }
                return (string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title, author);
            }
        }
        return (Path.GetFileNameWithoutExtension(path), "");
    }

    private static IReadOnlyList<BookMetadata>? SelectMetadata(
        IReadOnlyList<BookMetadata>? selectedBooks,
        IEnumerable<string>? requestedTitles)
    {
        if (selectedBooks is null || requestedTitles is null)
        {
            return selectedBooks;
        }
        var requested = requestedTitles.Select(BookMatching.Normalize).ToHashSet(StringComparer.Ordinal);
        return selectedBooks.Where(book => requested.Contains(BookMatching.Normalize(book.Title))).ToArray();
    }

    private static void AddMissingSourceActions(List<BookAction> actions, IReadOnlyList<BookMetadata>? selectedBooks)
    {
        if (selectedBooks is null)
        {
            return;
        }
        var found = actions
            .Where(action => action.Status != "possible_source_match")
            .Select(action => BookMatching.Normalize(action.Title))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var metadata in selectedBooks.Where(book => !found.Contains(BookMatching.Normalize(book.Title))))
        {
            actions.Add(new BookAction(metadata.Title, metadata.AuthorText, "source_not_found", "",
                "", Reason: "No source ebook filename matched the selected title."));
        }
    }

    private static void AddOption(List<string> arguments, string option, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add(option);
            arguments.Add(value);
        }
    }

    private static void AddField(List<string> arguments, string field, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add("--field");
            arguments.Add($"{field}:{value}");
        }
    }

    private static void EnsureSuccess(ProcessResult result, string command)
    {
        if (result.ExitCode != 0)
        {
            throw new CalibreToolingException(
                $"{command} failed: {FirstNonEmpty(result.StandardError, result.StandardOutput, "Unknown error.")}");
        }
    }

    private static string NormalizeFormat(string format)
    {
        var value = format.Trim().TrimStart('.').ToLowerInvariant();
        return value.Length > 0
            ? value
            : throw new ArgumentException("Format must not be empty.", nameof(format));
    }

    private static void ValidateCoverDimensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var dimensions = ImageDimensions.Read(bytes);
        if (dimensions.Width < 250 || dimensions.Height < 350)
        {
            throw new InvalidDataException("Cover images must be at least 250x350 pixels.");
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed record SourceEntry(
        string Title,
        string Author,
        string Path,
        BookMetadata? Metadata,
        bool IsPossibleMatch);
    private sealed record PlannedFormat(string Format, string Path, int ActionIndex, bool Temporary);
    private sealed record FormatPlan(List<PlannedFormat> Files, List<BookAction> Actions);

    private static class ImageDimensions
    {
        public static (int Width, int Height) Read(byte[] data)
        {
            if (data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                return (ReadBigEndianInt32(data, 16), ReadBigEndianInt32(data, 20));
            }
            if (data.Length >= 30 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                data.AsSpan(8, 4).SequenceEqual("WEBP"u8) &&
                data.AsSpan(12, 4).SequenceEqual("VP8X"u8))
            {
                var width = data[24] | data[25] << 8 | data[26] << 16;
                var height = data[27] | data[28] << 8 | data[29] << 16;
                return (width + 1, height + 1);
            }
            if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
            {
                return ReadJpeg(data);
            }
            throw new InvalidDataException("Cover must be a readable PNG, JPEG, or WebP image.");
        }

        private static (int Width, int Height) ReadJpeg(byte[] data)
        {
            var offset = 2;
            while (offset + 9 < data.Length)
            {
                if (data[offset++] != 0xff)
                {
                    continue;
                }
                var marker = data[offset++];
                if (marker is 0xd8 or 0xd9 or 0x01 || marker is >= 0xd0 and <= 0xd7)
                {
                    continue;
                }
                if (offset + 2 > data.Length)
                {
                    break;
                }
                var length = data[offset] << 8 | data[offset + 1];
                if (length < 2 || offset + length > data.Length)
                {
                    break;
                }
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    return (data[offset + 5] << 8 | data[offset + 6], data[offset + 3] << 8 | data[offset + 4]);
                }
                offset += length;
            }
            throw new InvalidDataException("Could not read the JPEG cover dimensions.");
        }

        private static int ReadBigEndianInt32(byte[] data, int offset) =>
            data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];
    }
}

internal static class LineExtensions
{
    public static IEnumerable<string> SplitLines(this string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
