using System.Text.Json;
using System.Text.RegularExpressions;

namespace CalibreBookImporter;

public sealed class CalibreImporter
{
    private static readonly string[] SupportedExtensions =
        [".epub", ".mobi", ".azw3", ".pdf", ".lit", ".rtf", ".html", ".htm"];
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
        string? calibreDirectory = null)
    {
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        var requested = (requestedTitles ?? [])
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

        var groups = new Dictionary<string, List<(string Title, string Author, string Path)>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (requested.Length > 0)
            {
                var fileName = BookMatching.Normalize(Path.GetFileName(path));
                if (!requested.Any(title => fileName.Contains(title, StringComparison.Ordinal) ||
                                            title.Contains(fileName, StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            var metadata = ReadMetadata(path, ebookMeta);
            var title = BookMatching.SanitizeTitle(metadata.Title);
            var author = metadata.Author.Trim();
            var key = BookMatching.BuildGroupKey(title, author);
            if (!groups.TryGetValue(key, out var entries))
            {
                entries = [];
                groups.Add(key, entries);
            }
            entries.Add((title, author, path));
        }

        return groups.Values.Select(entries =>
            {
                var paths = entries.Select(entry => entry.Path).ToArray();
                var preferred = BookMatching.PreferredFile(paths);
                var first = entries[0];
                return new SourceBook(first.Title, first.Author, paths, preferred);
            })
            .OrderBy(book => BookMatching.Normalize(book.Title), StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<BookAction> Compare(
        string sourceDirectory,
        string libraryPath,
        IEnumerable<string>? requestedTitles = null)
    {
        var books = Scan(sourceDirectory, requestedTitles, _calibreDirectory);
        var library = ListLibraryRecords(libraryPath);
        var actions = new List<BookAction>();
        foreach (var book in books)
        {
            var matches = BookMatching.FindMatchingLibraryBooks(library, book.Title, book.Author);
            if (matches.Count > 1)
            {
                actions.Add(new BookAction(book.Title, book.Author, book.PreferredPath, book.PreferredFormat,
                    "ambiguous", Reason: "Multiple possible matches in the Calibre library."));
            }
            else if (matches.Count == 1)
            {
                actions.Add(new BookAction(book.Title, book.Author, book.PreferredPath, book.PreferredFormat,
                    "already_in_calibre", matches[0].Id, Reason: "Title already exists in the Calibre library."));
            }
            else
            {
                actions.Add(new BookAction(book.Title, book.Author, book.PreferredPath, book.PreferredFormat,
                    "missing", Reason: "Not found in the existing Calibre library."));
            }
        }
        return actions;
    }

    public IReadOnlyList<BookAction> Import(
        string sourceDirectory,
        string libraryPath,
        IEnumerable<string> requestedTitles,
        string targetFormat = "azw3",
        bool convertWhenNeeded = false)
    {
        var target = targetFormat.Trim().TrimStart('.').ToLowerInvariant();
        if (target.Length == 0)
        {
            throw new ArgumentException("Target format must not be empty.", nameof(targetFormat));
        }

        var books = Scan(sourceDirectory, requestedTitles, _calibreDirectory);
        var library = ListLibraryRecords(libraryPath);
        var actions = new List<BookAction>();
        foreach (var book in books)
        {
            var matches = BookMatching.FindMatchingLibraryBooks(library, book.Title, book.Author);
            if (matches.Count > 0)
            {
                actions.Add(new BookAction(book.Title, book.Author, book.PreferredPath, book.PreferredFormat,
                    "already_in_calibre", matches[0].Id, Reason: "Title already exists; skipping duplicate import."));
                continue;
            }

            var candidatePath = book.PreferredPath;
            if (!Path.GetExtension(candidatePath).Equals("." + target, StringComparison.OrdinalIgnoreCase))
            {
                if (!convertWhenNeeded)
                {
                    actions.Add(new BookAction(book.Title, book.Author, candidatePath, book.PreferredFormat,
                        "skipped", Reason: $"Source format {Path.GetExtension(candidatePath).ToLowerInvariant()} is not the requested target {target}; conversion disabled."));
                    continue;
                }

                if (_tools.EbookConvert is null)
                {
                    throw new CalibreToolingException(
                        "Could not locate ebook-convert. Install Calibre or pass --calibre-dir.");
                }
                var converted = ConvertToTarget(candidatePath, target, _tools.EbookConvert);
                if (converted is null)
                {
                    actions.Add(new BookAction(book.Title, book.Author, candidatePath, book.PreferredFormat,
                        "failed", OutputPath: null, Reason: $"Conversion to {target} failed."));
                    continue;
                }
                candidatePath = converted;
            }

            var result = CalibreToolPaths.Run(_tools.CalibreDb,
                [$"--with-library={libraryPath}", "add", candidatePath]);
            if (result.ExitCode != 0)
            {
                var reason = FirstNonEmpty(result.StandardError, result.StandardOutput, "Import failed.");
                actions.Add(new BookAction(book.Title, book.Author, book.PreferredPath, book.PreferredFormat,
                    "failed", OutputPath: candidatePath, Reason: reason));
            }
            else
            {
                actions.Add(new BookAction(book.Title, book.Author, book.PreferredPath, book.PreferredFormat,
                    "imported", OutputPath: candidatePath, Reason: $"Imported as {target}."));
                library = ListLibraryRecords(libraryPath);
            }
        }
        return actions;
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
        if (result.ExitCode != 0)
        {
            throw new CalibreToolingException(
                $"Could not list Calibre library: {FirstNonEmpty(result.StandardError, result.StandardOutput, "Unknown calibredb error.")}");
        }

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
                return (string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title, author);
            }
        }
        return (Path.GetFileNameWithoutExtension(path), "");
    }

    private static string? ConvertToTarget(string sourcePath, string targetFormat, string ebookConvert)
    {
        var target = Path.ChangeExtension(sourcePath, "." + targetFormat);
        var output = UniqueOutputPath(target);
        var result = CalibreToolPaths.Run(ebookConvert, [sourcePath, output, "--output-profile=kindle"]);
        return result.ExitCode == 0 && File.Exists(output) ? output : null;
    }

    private static string UniqueOutputPath(string path)
    {
        var candidate = path;
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}_{counter}{Path.GetExtension(path)}");
            counter++;
        }
        return candidate;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

internal static class LineExtensions
{
    public static IEnumerable<string> SplitLines(this string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
