# Calibre Book Importer

A .NET 10 command-line application and Visual Studio solution for searching Open Library, scanning ebook folders, comparing books with a Calibre library, and importing books without overwriting source files.

## Requirements

- .NET 10 SDK (Visual Studio 2026 supports the solution)
- Calibre command-line tools for library comparison and importing: `calibredb`; `ebook-meta` and `ebook-convert` are used when available or required

## Build and test

```powershell
dotnet build CalibreBookImporter.sln
dotnet test CalibreBookImporter.sln
```

Run commands from the repository root with:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- <command> [options]
```

Add `--calibre-dir "C:\Program Files\Calibre2"` before the command when Calibre is not on `PATH`.

## Commands

Search the Open Library catalog:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- search --query "Honor Harrington" --limit 20
```

Scan a source collection:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- scan --source-dir "C:\Users\tombe\OneDrive\eBooks"
```

Compare selected titles against a Calibre library without importing:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- compare --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library" --title "On Basilisk Station"
```

Import a selected title; conversion is opt-in:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- import --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library" --title "On Basilisk Station" --target-format azw3 --convert-when-needed
```

Search and scan output are JSON. Compare and import report per-title statuses, including existing Calibre records, ambiguous matches, failures, and import outcomes.

## Safety

- Source ebooks are never moved, renamed, or overwritten.
- Existing Calibre titles are skipped rather than replaced.
- Conversion output is written to a new path and never overwrites an existing file.
- Calibre errors are reported rather than treated as an empty library.

## License

MIT
