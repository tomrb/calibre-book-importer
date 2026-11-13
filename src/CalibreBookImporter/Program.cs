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
        if (command is not ("search" or "scan" or "compare" or "import"))
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
                    var books = CalibreImporter.Scan(
                        Required(options, "source-dir"),
                        options.GetValues("title"),
                        calibreDirectory);
                    WriteJson(books);
                    return 0;
                }
            case "compare":
                {
                    var actions = new CalibreImporter(calibreDirectory).Compare(
                        Required(options, "source-dir"),
                        Required(options, "library-path"),
                        options.GetValues("title"));
                    WriteJson(actions);
                    return actions.Any(action => action.Status is "ambiguous" or "source_not_found") ? 1 : 0;
                }
            case "import":
                {
                    var titles = options.GetValues("title");
                    if (titles.Count == 0)
                    {
                        throw new ArgumentException("Select at least one book with --title before importing.");
                    }

                    var actions = new CalibreImporter(calibreDirectory).Import(
                        Required(options, "source-dir"),
                        Required(options, "library-path"),
                        titles,
                        options.GetSingle("target-format") ?? "azw3",
                        options.ContainsKey("convert-when-needed"));
                    WriteJson(actions);
                    PrintSummary(actions);
                    return actions.Any(action => action.Status == "failed") ? 1 : 0;
                }
            default:
                throw new InvalidOperationException("Command validation failed.");
        }
    }

    private static Dictionary<string, List<string>> ParseOptions(IEnumerable<string> arguments)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(["convert-when-needed"], StringComparer.OrdinalIgnoreCase);
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
            "scan" => new HashSet<string>(["source-dir", "title"], StringComparer.OrdinalIgnoreCase),
            "compare" => new HashSet<string>(
                ["source-dir", "library-path", "title"], StringComparer.OrdinalIgnoreCase),
            "import" => new HashSet<string>(
                ["source-dir", "library-path", "title", "target-format", "convert-when-needed"],
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
                     ("FOUND IN SOURCE", actions.Count(action => action.Status is "skipped" or "failed")),
                     ("ALREADY IN CALIBRE", actions.Count(action => action.Status == "already_in_calibre")),
                     ("IMPORTED", actions.Count(action => action.Status == "imported")),
                     ("NOT FOUND", actions.Count(action => action.Status == "missing")),
                     ("AMBIGUOUS / NEEDS REVIEW", actions.Count(action => action.Status == "ambiguous"))
                 })
        {
            Console.WriteLine($"{label}: {count}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Calibre Book Importer (.NET 10)

            Usage:
              calibre-book-importer [--calibre-dir DIR] search --query QUERY [--limit N]
              calibre-book-importer [--calibre-dir DIR] scan --source-dir DIR [--title TITLE]
              calibre-book-importer [--calibre-dir DIR] compare --source-dir DIR --library-path DIR [--title TITLE]
              calibre-book-importer [--calibre-dir DIR] import --source-dir DIR --library-path DIR --title TITLE
                  [--target-format FORMAT] [--convert-when-needed]

            Search queries Open Library by title, author, subject, genre, or keywords.
            Imports skip existing Calibre records and never overwrite source files.
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
