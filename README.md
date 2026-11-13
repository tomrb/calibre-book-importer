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

Search a source collection recursively by title or author text in file and folder names:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- scan --source-dir "C:\Users\tombe\OneDrive\eBooks" --author "David Weber"
```

Compare selected titles against a Calibre library without importing:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- compare --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library" --title "On Basilisk Station"
```

Plan an import without writing to Calibre. Imports default to EPUB and AZW3, converting missing formats to temporary files:

```powershell
dotnet run --project src\CalibreBookImporter\CalibreBookImporter.csproj -- import --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library" --title "On Basilisk Station" --dry-run
```

Use `--metadata-file` with a JSON array containing selected titles, authors, and any verified metadata. `compare`, `scan`, `import`, and `update` accept this option; scan searches the complete relative path for selected title or author terms and checks ebook metadata when available. Author-only filename/path hits that cannot be verified as the exact selected book are reported as possible matches and are not imported. `--target-format` can be repeated. `--no-convert` skips formats without a matching source file. Imports skip existing records by default.

After reviewing a dry-run, perform the import by omitting `--dry-run`. To update formats and metadata on an exact existing title-and-author match, use `update`. To fully replace an existing record, explicitly use `import --replace-existing`; this exports a backup, removes the old record, adds the selected book and formats, and restores the backup if replacement fails. Replacement is not permitted with `update`.

The standalone conversion command is `convert --source-file FILE [--target-format FORMAT] [--output-file FILE]`. Without `--output-file`, conversion writes to a new temporary-directory path and never overwrites the source.

Search and scan output are JSON. Compare and import report per-title statuses, including exact Calibre matches, existing formats, ambiguous matches, failures, and import outcomes.

## Install for GitHub Copilot

From the repository root, run:

```powershell
.\scripts\Install-CalibreCopilotSkill.ps1
```

The installer publishes the .NET helper to `%APPDATA%\com.github.githubapp\calibre-book-importer` and installs the repository's `calibre-ebook-workflow` skill under the app's `app-skills` directory. Existing installations are retained as timestamped backups.

## Safety

- Source ebooks are never moved, renamed, or overwritten.
- Existing Calibre titles are skipped by default; exact title-and-author matching is required for updates or replacements.
- Replacement exports a full-record backup and attempts restoration after a failed operation.
- Conversion output is written to a new path and never overwrites an existing file.
- Calibre errors are reported rather than treated as an empty library.

## License

MIT
