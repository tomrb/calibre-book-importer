using CalibreBookImporter;

namespace CalibreBookImporter.Tests;

public sealed class BookMatchingTests
{
    [Theory]
    [InlineData("  Honor_of-the-Queen (EPUB) ", "honor of the queen")]
    [InlineData("L'Étranger", "l etranger")]
    public void NormalizeRemovesFilenameNoiseAndDiacritics(string input, string expected)
    {
        Assert.Equal(expected, BookMatching.Normalize(BookMatching.SanitizeTitle(input)));
    }

    [Fact]
    public void PreferredFileChoosesEpubBeforeOtherFormats()
    {
        var paths = new[]
        {
            @"C:\Books\Honor of the Queen.pdf",
            @"C:\Books\Honor of the Queen.azw3",
            @"C:\Books\Honor of the Queen.epub"
        };

        Assert.Equal(paths[2], BookMatching.PreferredFile(paths));
    }

    [Fact]
    public void FindMatchingLibraryBooksUsesNormalizedTitleAndAuthor()
    {
        var records = new[]
        {
            new LibraryBook("1", "Honor of the Queen", "David Weber", ["EPUB"]),
            new LibraryBook("2", "Honor of the Queen", "Other Author", ["PDF"])
        };

        var matches = BookMatching.FindMatchingLibraryBooks(records, "Honor_of_the_Queen (ebook)", "David Weber");

        Assert.Single(matches);
        Assert.Equal("1", matches[0].Id);
    }

    [Fact]
    public void FindMatchingLibraryBooksRetainsPossiblePartialTitleMatches()
    {
        var records = new[]
        {
            new LibraryBook("1", "The Honor of the Queen", "David Weber", ["EPUB"])
        };

        var matches = BookMatching.FindMatchingLibraryBooks(records, "Honor of the Queen", "David Weber");

        Assert.Single(matches);
    }

    [Fact]
    public void ResolveExactMatchRequiresExactNormalizedTitleAndAuthor()
    {
        var records = new[]
        {
            new LibraryBook("1", "Honor of the Queen", "David Weber", ["EPUB"]),
            new LibraryBook("2", "Honor of the Queen", "Other Author", ["PDF"])
        };

        var match = BookMatching.ResolveExactMatch(records, "Honor_of_the_Queen (ebook)", "David Weber");
        var mismatch = BookMatching.ResolveExactMatch(records, "Honor of the Queen", "Another Author");
        var partialTitle = BookMatching.ResolveExactMatch(records, "The Honor of the Queen", "David Weber");

        Assert.Equal("already_in_calibre", match.Status);
        Assert.Equal("1", match.Record?.Id);
        Assert.Equal("ambiguous", mismatch.Status);
        Assert.Equal("missing", partialTitle.Status);
    }

    [Fact]
    public void ReadMetadataFileRejectsDuplicateNormalizedTitles()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """
            [
              { "title": "Honor of the Queen", "authors": ["David Weber"] },
              { "title": "Honor_of_the_Queen", "authors": ["David Weber"] }
            ]
            """);
        try
        {
            Assert.Throws<ArgumentException>(() => BookMetadata.ReadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseLibraryListingReadsMachineReadableRecords()
    {
        const string listing = """
            [
              {
                "id": 42,
                "title": "Book title",
                "authors": "Author",
                "formats": ["/library/Book title.epub", "/library/Book title.pdf"]
              }
            ]
            """;

        var book = Assert.Single(CalibreImporter.ParseLibraryListing(listing));

        Assert.Equal("42", book.Id);
        Assert.Equal("Book title", book.Title);
        Assert.Equal("Author", book.Authors);
        Assert.Equal(["EPUB", "PDF"], book.Formats);
    }

    [Fact]
    public void ScanGroupsAvailableFormatsAndPrefersEpub()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "A Book.pdf"), "pdf");
            var nestedDirectory = Path.Combine(directory, "author", "series");
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(Path.Combine(nestedDirectory, "A Book.epub"), "epub");

            var books = CalibreImporter.Scan(directory);

            var book = Assert.Single(books);
            Assert.Equal("A Book", book.Title);
            Assert.Equal("epub", book.PreferredFormat);
            Assert.Equal(2, book.SourcePaths.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScanSurfacesButDoesNotAutoMatchTitleSubstring()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "The Honor of the Queen.epub"), "epub");
            var metadata = new[]
            {
                new BookMetadata { Title = "Honor of the Queen", Authors = ["David Weber"] }
            };

            var books = CalibreImporter.Scan(directory, selectedBooks: metadata);

            var book = Assert.Single(books);
            Assert.True(book.IsPossibleMatch);
            Assert.Equal(Path.Combine(directory, "The Honor of the Queen.epub"), book.PreferredPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScanSearchesNestedPathsForSelectedAuthor()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var authorDirectory = Path.Combine(directory, "David Weber", "Honor Harrington");
        Directory.CreateDirectory(authorDirectory);
        try
        {
            var path = Path.Combine(authorDirectory, "unknown-name.epub");
            File.WriteAllText(path, "epub");
            var metadata = new[]
            {
                new BookMetadata { Title = "On Basilisk Station", Authors = ["David Weber"] }
            };

            var book = Assert.Single(CalibreImporter.Scan(directory, selectedBooks: metadata));

            Assert.True(book.IsPossibleMatch);
            Assert.Equal(path, book.PreferredPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScanCanSearchByAuthorWithoutSelectedMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var authorDirectory = Path.Combine(directory, "David Weber", "Honor Harrington");
        Directory.CreateDirectory(authorDirectory);
        try
        {
            var path = Path.Combine(authorDirectory, "unknown-name.epub");
            File.WriteAllText(path, "epub");

            var book = Assert.Single(CalibreImporter.Scan(directory, requestedAuthors: ["David Weber"]));

            Assert.Equal(path, book.PreferredPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScanAcceptsAnExactTitleFilenameWhenEmbeddedAuthorIsUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "On Basilisk Station.epub"), "epub");
            var metadata = new[]
            {
                new BookMetadata { Title = "On Basilisk Station", Authors = ["David Weber"] }
            };

            var book = Assert.Single(CalibreImporter.Scan(directory, selectedBooks: metadata));

            Assert.False(book.IsPossibleMatch);
            Assert.Equal("On Basilisk Station", book.Metadata?.Title);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScanFindsTitleWithinFilenameAndMarksUnverifiedMetadataAsPossible()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "David Weber - On Basilisk Station.epub");
            File.WriteAllText(path, "epub");
            var metadata = new[]
            {
                new BookMetadata { Title = "On Basilisk Station", Authors = ["David Weber"] }
            };

            var book = Assert.Single(CalibreImporter.Scan(directory, selectedBooks: metadata));

            Assert.True(book.IsPossibleMatch);
            Assert.Equal(path, book.PreferredPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
