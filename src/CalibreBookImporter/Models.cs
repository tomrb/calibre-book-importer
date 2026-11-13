using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace CalibreBookImporter;

public sealed record SourceBook(
    string Title,
    string Author,
    IReadOnlyList<string> SourcePaths,
    string PreferredPath,
    BookMetadata? Metadata = null,
    bool IsPossibleMatch = false)
{
    public string PreferredFormat => Path.GetExtension(PreferredPath).TrimStart('.').ToLowerInvariant();
}

public sealed record LibraryBook(
    string Id,
    string Title,
    string Authors,
    IReadOnlyList<string> Formats);

public sealed record BookAction(
    string Title,
    string Author,
    string Status,
    string Format,
    string Source,
    string? LibraryId = null,
    string? OutputPath = null,
    string Reason = "",
    IReadOnlyList<string>? ExistingFormats = null);

public sealed class BookMetadata
{
    public string Title { get; set; } = "";
    public List<string> Authors { get; set; } = [];
    public string Description { get; set; } = "";
    public List<string> Subjects { get; set; } = [];
    public string Publisher { get; set; } = "";
    public string Published { get; set; } = "";
    public List<string> Isbns { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public string Series { get; set; } = "";
    public string SeriesIndex { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string CoverPath { get; set; } = "";
    public string SourceUrl { get; set; } = "";

    public string AuthorText => string.Join(" & ", Authors);

    public static IReadOnlyList<BookMetadata> ReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Metadata file not found: {fullPath}", fullPath);
        }
        var books = JsonSerializer.Deserialize<List<BookMetadata>>(
                        File.ReadAllText(fullPath),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        if (books.Any(book => string.IsNullOrWhiteSpace(book.Title) || book.Authors.Count == 0))
        {
            throw new ArgumentException("Every metadata entry must include a title and at least one author.");
        }
        if (books.GroupBy(book => BookMatching.Normalize(book.Title), StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Metadata file contains duplicate normalized titles.");
        }
        return books;
    }
}

public sealed record CatalogBook(
    string Id,
    string Title,
    IReadOnlyList<string> Authors,
    string Synopsis,
    IReadOnlyList<string> Subjects,
    string Series,
    string SeriesIndex,
    string FirstPublished,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Isbns,
    IReadOnlyList<string> Languages,
    string CoverUrl,
    string SourceUrl);

public static partial class BookMatching
{
    private static readonly string[] SupportedExtensions =
        [".epub", ".mobi", ".azw3", ".pdf", ".lit", ".rtf", ".html", ".htm"];

    private static readonly Dictionary<string, int> FormatPreference = new(StringComparer.OrdinalIgnoreCase)
    {
        [".epub"] = 0,
        [".azw3"] = 1,
        [".mobi"] = 2,
        [".pdf"] = 3,
        [".lit"] = 4,
        [".rtf"] = 5,
        [".html"] = 6,
        [".htm"] = 6
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var withoutMarks = new string(decomposed.Where(character =>
            CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
        return WhitespaceRegex().Replace(NonWordRegex().Replace(withoutMarks.Replace('_', ' ').Replace('-', ' '), " "), " ").Trim();
    }

    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        var value = title.Trim();
        foreach (var extension in SupportedExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^extension.Length];
                break;
            }
        }
        value = EditionSuffixRegex().Replace(value, "");
        return Regex.Replace(value, @"\s+[-_]+\s*", " ").ReplaceMultipleSpaces();
    }

    public static string NormalizeAuthor(string? author) =>
        Normalize(AuthorSuffixRegex().Replace(author ?? "", "").Trim());

    public static bool ContainsSearchTerm(string? text, string? term)
    {
        var normalizedText = Normalize(text);
        var normalizedTerm = Normalize(term);
        return normalizedText.Length > 0 && normalizedTerm.Length > 0 &&
               $" {normalizedText} ".Contains($" {normalizedTerm} ", StringComparison.Ordinal);
    }

    public static string BuildGroupKey(string title, string? author = null)
    {
        var titleKey = Normalize(SanitizeTitle(title));
        var authorKey = Normalize(author ?? "");
        return titleKey.Length > 0 ? $"{titleKey}|{authorKey}" : $"{authorKey}|unknown";
    }

    public static IReadOnlyList<LibraryBook> FindMatchingLibraryBooks(
        IEnumerable<LibraryBook> records,
        string title,
        string? author = null)
    {
        var targetTitle = Normalize(SanitizeTitle(title));
        var targetAuthor = Normalize(author ?? "");
        return records.Where(record =>
        {
            var libraryTitle = Normalize(SanitizeTitle(record.Title));
            if (libraryTitle.Length == 0)
            {
                return false;
            }

            var titleMatch = libraryTitle == targetTitle ||
                             libraryTitle.Contains(targetTitle, StringComparison.Ordinal) ||
                             targetTitle.Contains(libraryTitle, StringComparison.Ordinal);
            var libraryAuthor = Normalize(record.Authors);
            var authorMatch = targetAuthor.Length == 0 || libraryAuthor.Length == 0 ||
                              libraryAuthor.Contains(targetAuthor, StringComparison.Ordinal) ||
                              targetAuthor.Contains(libraryAuthor, StringComparison.Ordinal);
            return titleMatch && authorMatch;
        }).ToArray();
    }

    public static LibraryMatch ResolveExactMatch(
        IEnumerable<LibraryBook> records,
        string title,
        string? author)
    {
        var titleKey = Normalize(SanitizeTitle(title));
        var titleMatches = records.Where(record =>
            Normalize(SanitizeTitle(record.Title)) == titleKey).ToArray();
        if (titleMatches.Length == 0)
        {
            return new LibraryMatch("missing", null, "No exact normalized title match exists in the Calibre library.");
        }
        if (string.IsNullOrWhiteSpace(author))
        {
            return new LibraryMatch("ambiguous", null, "The author is missing; refusing to guess which Calibre book matches.");
        }
        var authorKey = NormalizeAuthor(author);
        var authorMatches = titleMatches.Where(record =>
            NormalizeAuthor(record.Authors) == authorKey).ToArray();
        return authorMatches.Length switch
        {
            1 => new LibraryMatch("already_in_calibre", authorMatches[0], "Exact normalized title and author match."),
            > 1 => new LibraryMatch("ambiguous", null, "Multiple Calibre books have the same title and author."),
            _ => new LibraryMatch("ambiguous", null, "A title match exists, but its author does not match exactly.")
        };
    }

    public static string PreferredFile(IEnumerable<string> paths) =>
        paths.OrderBy(path => FormatPreference.GetValueOrDefault(Path.GetExtension(path), 99))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();

    [GeneratedRegex(@"[^\p{L}\p{N} ]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+\((?:ebook|epub|mobi|pdf|azw3)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionSuffixRegex();

    [GeneratedRegex(@"\s+\[[^\]]+\]$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorSuffixRegex();
}

public sealed record LibraryMatch(string Status, LibraryBook? Record, string Reason);

internal static class StringExtensions
{
    public static string ReplaceMultipleSpaces(this string value) =>
        Regex.Replace(value, @"\s{2,}", " ").Trim();
}
