using System.Net.Http.Headers;
using System.Text.Json;

namespace CalibreBookImporter;

public sealed class CatalogSearch
{
    private const string Fields =
        "key,title,author_name,first_publish_year,subject,series,cover_i,edition_count,first_sentence,isbn,publisher,language";
    private static readonly HttpClient Client = CreateClient();

    public async Task<IReadOnlyList<CatalogBook>> SearchAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query must not be empty.", nameof(query));
        }
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be from 1 to 50.");
        }

        var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString(Fields)}&limit={limit}";
        using var response = await Client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Open Library returned an unexpected search response.");
        }

        using var semaphore = new SemaphoreSlim(5);
        var tasks = docs.EnumerateArray()
            .Where(book => GetString(book, "title").Length > 0)
            .Select(async book =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    return await ConvertBookAsync(book, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        return await Task.WhenAll(tasks);
    }

    private static async Task<CatalogBook> ConvertBookAsync(JsonElement book, CancellationToken cancellationToken)
    {
        var key = GetString(book, "key");
        var subjects = GetStrings(book, "subject").Take(10).ToArray();
        var synopsis = GetFirstString(book, "first_sentence");
        if (key.StartsWith("/works/", StringComparison.Ordinal))
        {
            using var response = await Client.GetAsync($"https://openlibrary.org{key}.json", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var details = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (details.RootElement.TryGetProperty("description", out var description))
            {
                synopsis = description.ValueKind switch
                {
                    JsonValueKind.String => description.GetString() ?? synopsis,
                    JsonValueKind.Object when description.TryGetProperty("value", out var value) => value.GetString() ?? synopsis,
                    _ => synopsis
                };
            }
            if (subjects.Length == 0 && details.RootElement.TryGetProperty("subjects", out var detailSubjects) &&
                detailSubjects.ValueKind == JsonValueKind.Array)
            {
                subjects = detailSubjects.EnumerateArray()
                    .Select(item => item.GetString() ?? "")
                    .Where(item => item.Length > 0)
                    .Take(10)
                    .ToArray();
            }
        }

        var coverId = GetString(book, "cover_i");
        return new CatalogBook(
            key.Length > 0 ? key : $"{GetString(book, "title")}|{string.Join(",", GetStrings(book, "author_name"))}",
            GetString(book, "title"),
            GetStrings(book, "author_name"),
            synopsis,
            subjects,
            GetStrings(book, "series").FirstOrDefault() ?? "",
            "",
            GetString(book, "first_publish_year"),
            GetStrings(book, "publisher").Take(5).ToArray(),
            GetStrings(book, "isbn").Take(5).ToArray(),
            GetStrings(book, "language").Take(10).ToArray(),
            coverId.Length > 0 ? $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg" : "",
            key.StartsWith("/works/", StringComparison.Ordinal) ? $"https://openlibrary.org{key}" : "https://openlibrary.org");
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static string GetFirstString(JsonElement element, string propertyName) =>
        GetStrings(element, propertyName).FirstOrDefault() ?? GetString(element, propertyName);

    private static IReadOnlyList<string> GetStrings(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return [];
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? [] : [text];
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString())
            .Where(text => text.Length > 0)
            .ToArray();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CalibreBookImporter/1.0");
        return client;
    }
}
