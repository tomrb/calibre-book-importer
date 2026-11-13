using System.Text.Json;

namespace CalibreBookImporter;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
                                          or InvalidOperationException or UnauthorizedAccessException
                                          or JsonException or HttpRequestException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var arguments = args.ToList();
        string? calibreDirectory = null;
        if (arguments.Count > 0 && arguments[0].Equals("--calibre-dir", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count < 2 || arguments[1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Option --calibre-dir requires a value.");
            }
            calibreDirectory = arguments[1];
            arguments.RemoveRange(0, 2);
        }

        if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return arguments.Count == 0 ? 2 : 0;
        }

        var command = arguments[0].ToLowerInvariant();
        if (command is not ("search" or "scan" or "compare" or "import" or "update" or "convert"))
        {
            throw new ArgumentException($"Unknown command: {command}");
        }

        var options = ParseOptions(arguments.Skip(1));
        var commandCalibreDirectory = options.GetSingle("calibre-dir");
        if (calibreDirectory is not null && commandCalibreDirectory is not null)
        {
            throw new ArgumentException("--calibre-dir may only be specified once.");
        }
        calibreDirectory ??= commandCalibreDirectory;
        options.Remove("calibre-dir");
        ValidateOptions(command, options);

        switch (command)
        {
            case "search":
                {
                    var query = BuildSearchQuery(options);
                    var results = new CatalogSearch()
                        .SearchAsync(query, ParseLimit(options.GetSingle("limit")))
                        .GetAwaiter()
                        .GetResult();
                    WriteJson(results);
                    return 0;
                }
            case "scan":
                {
                    var metadata = BookMetadata.ReadFile(options.GetSingle("metadata-file"));
                    var books = CalibreImporter.Scan(
                        Required(options, "source-dir"),
                        options.GetValues("title").Count > 0
                            ? options.GetValues("title")
                            : metadata.Select(book => book.Title),
                        calibreDirectory,
                        metadata);
                    WriteJson(books);
                    return 0;
                }
            case "compare":
                {
                    var metadata = BookMetadata.ReadFile(options.GetSingle("metadata-file"));
                    var actions = new CalibreImporter(calibreDirectory).Compare(
                        Required(options, "source-dir"),
                        Required(options, "library-path"),
                        options.GetValues("title").Count > 0
                            ? options.GetValues("title")
                            : metadata.Select(book => book.Title),
                        metadata);
                    WriteJson(actions);
                    return HasFailures(actions) ? 1 : 0;
                }
            case "import":
            case "update":
                {
                    var titles = options.GetValues("title");
                    var metadata = BookMetadata.ReadFile(options.GetSingle("metadata-file"));
                    if (titles.Count == 0)
                    {
                        titles = metadata.Select(book => book.Title).ToArray();
                    }
                    if (titles.Count == 0)
                    {
                        throw new ArgumentException(
                            "Select at least one book with --title or --metadata-file before importing or updating.");
                    }

                    IReadOnlyList<string> targetFormats = options.GetValues("target-format").Count > 0
                        ? options.GetValues("target-format")
                        : ["epub", "azw3"];
                    var actions = new CalibreImporter(calibreDirectory).Import(
                        Required(options, "source-dir"),
                        Required(options, "library-path"),
                        titles,
                        targetFormats,
                        convertWhenNeeded: !options.ContainsKey("no-convert"),
                        dryRun: options.ContainsKey("dry-run"),
                        updateOnly: command == "update",
                        replaceExisting: options.ContainsKey("replace-existing"),
                        selectedBooks: metadata);
                    WriteJson(actions);
                    PrintSummary(actions);
                    return HasFailures(actions) ? 1 : 0;
                }
            case "convert":
                {
                    var output = new CalibreImporter(calibreDirectory).Convert(
                        Required(options, "source-file"),
                        options.GetSingle("target-format") ?? "azw3",
                        options.GetSingle("output-file"));
                    WriteJson(new { status = "converted", output });
                    return 0;
                }
            default:
                throw new InvalidOperationException("Command validation failed.");
        }
    }

    private static Dictionary<string, List<string>> ParseOptions(IEnumerable<string> arguments)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(
            ["dry-run", "no-convert", "replace-existing"],
            StringComparer.OrdinalIgnoreCase);
        using var enumerator = arguments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var token = enumerator.Current;
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                throw new ArgumentException($"Unexpected argument: {token}");
            }

            var name = token[2..];
            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options.Add(name, values);
            }

            if (flags.Contains(name))
            {
                values.Add("true");
                continue;
            }

            if (!enumerator.MoveNext() || enumerator.Current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option --{name} requires a value.");
            }
            values.Add(enumerator.Current);
        }
        return options;
    }

    private static void ValidateOptions(string command, Dictionary<string, List<string>> options)
    {
        var supported = command switch
        {
            "search" => new HashSet<string>(
                ["query", "title", "author", "genre", "subject", "keywords", "limit"],
                StringComparer.OrdinalIgnoreCase),
            "scan" => new HashSet<string>(
                ["source-dir", "title", "metadata-file"], StringComparer.OrdinalIgnoreCase),
            "compare" => new HashSet<string>(
                ["source-dir", "library-path", "title", "metadata-file"], StringComparer.OrdinalIgnoreCase),
            "import" or "update" => new HashSet<string>(
                ["source-dir", "library-path", "title", "target-format", "metadata-file",
                    "dry-run", "no-convert", "replace-existing"],
                StringComparer.OrdinalIgnoreCase),
            "convert" => new HashSet<string>(
                ["source-file", "target-format", "output-file"],
                StringComparer.OrdinalIgnoreCase),
            _ => []
        };
        var unknown = options.Keys.FirstOrDefault(name => !supported.Contains(name));
        if (unknown is not null)
        {
            throw new ArgumentException($"Option --{unknown} is not valid for {command}.");
        }
    }

    private static string Required(Dictionary<string, List<string>> options, string name)
    {
        if (options.TryGetValue(name, out var values) && values.Count > 0 &&
            !string.IsNullOrWhiteSpace(values[^1]))
        {
            return values[^1];
        }
        throw new ArgumentException($"Missing required option --{name}.");
    }

    private static string BuildSearchQuery(Dictionary<string, List<string>> options)
    {
        var terms = new List<string>();
        foreach (var (option, field) in new[]
                 {
                     ("title", "title"),
                     ("author", "author"),
                     ("genre", "subject"),
                     ("subject", "subject")
                 })
        {
            terms.AddRange(options.GetValues(option)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => $"{field}:\"{value.Trim()}\""));
        }
        terms.AddRange(options.GetValues("keywords").Where(value => !string.IsNullOrWhiteSpace(value)));
        terms.AddRange(options.GetValues("query").Where(value => !string.IsNullOrWhiteSpace(value)));
        if (terms.Count == 0)
        {
            throw new ArgumentException(
                "Provide --query or at least one title, author, genre, subject, or keywords search term.");
        }
        return string.Join(" ", terms);
    }

    private static int ParseLimit(string? value)
    {
        if (value is null)
        {
            return 20;
        }
        return int.TryParse(value, out var limit) && limit is >= 1 and <= 50
            ? limit
            : throw new ArgumentException("--limit must be an integer from 1 to 50.");
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintSummary(IReadOnlyList<BookAction> actions)
    {
        Console.WriteLine();
        foreach (var (label, count) in new[]
                 {
                     ("PLANNED / FOUND IN SOURCE", actions.Count(action =>
                         action.Status is "skipped" or "failed" or "would_add" or "would_update" or "would_replace")),
                     ("ALREADY IN CALIBRE", actions.Count(action => action.Status == "already_in_calibre")),
                     ("IMPORTED", actions.Count(action => action.Status == "imported")),
                     ("FORMATS ADDED", actions.Count(action => action.Status == "format_added")),
                     ("REPLACED", actions.Count(action => action.Status == "replaced")),
                     ("NOT FOUND", actions.Count(action => action.Status == "missing")),
                     ("AMBIGUOUS / NEEDS REVIEW", actions.Count(action => action.Status == "ambiguous"))
                 })
        {
            Console.WriteLine($"{label}: {count}");
        }
    }

    private static bool HasFailures(IEnumerable<BookAction> actions) =>
        actions.Any(action => action.Status is "failed" or "ambiguous" or "source_not_found" or
            "missing" or "not_in_library");

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Calibre Book Importer (.NET 10)

            Usage:
              calibre-book-importer [--calibre-dir DIR] search --query QUERY [--limit N]
              calibre-book-importer [--calibre-dir DIR] scan --source-dir DIR [--title TITLE] [--metadata-file FILE]
              calibre-book-importer [--calibre-dir DIR] compare --source-dir DIR --library-path DIR [--title TITLE]
                  [--metadata-file FILE]
              calibre-book-importer [--calibre-dir DIR] import|update --source-dir DIR --library-path DIR
                  [--title TITLE] [--metadata-file FILE] [--target-format FORMAT] [--dry-run]
                  [--no-convert] [--replace-existing]
              calibre-book-importer [--calibre-dir DIR] convert --source-file FILE [--target-format FORMAT]
                  [--output-file FILE]

            Search queries Open Library by title, author, subject, genre, or keywords.
            Imports skip existing Calibre records by default and never overwrite source files.
            """);
    }
}

internal static class OptionExtensions
{
    public static string? GetSingle(this Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : null;

    public static IReadOnlyList<string> GetValues(this Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) ? values : [];
}
