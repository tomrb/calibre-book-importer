---
name: calibre-ebook-workflow
description: Search book catalogs by title, author, genre, subject, or keywords; help select books; find local ebooks; compare with Calibre; then safely import or replace records with verified metadata, covers, EPUB, and AZW3 formats.
---

# Calibre ebook workflow

Carry out ebook discovery and Calibre library work for the user. The user should not need to invoke the helper CLI themselves. The .NET 10 helper is installed to `%APPDATA%\com.github.githubapp\calibre-book-importer\CalibreBookImporter.csproj`; run it with `dotnet run --project "<helper-project>" -- ...`. The repository-local project at `src\CalibreBookImporter\CalibreBookImporter.csproj` is the development/source copy. This installed helper makes the workflow independent of the current project or worktree.

If the installed helper is missing, check for the repository-local project and use it only when present. If neither exists, do not infer that this is a Python project and do not use or create a Python fallback; report that the .NET helper is unavailable and ask the user to open the importer repository and run `scripts\Install-CalibreCopilotSkill.ps1`. Never run a relative `.csproj` path in an unrelated or stale worktree. The picker canvas remains available globally.

Because PowerShell invocations do not share variables, resolve `$helperProject` at the start of each PowerShell tool call:

```powershell
$helperProject = Join-Path $env:APPDATA "com.github.githubapp\calibre-book-importer\CalibreBookImporter.csproj"
if (-not (Test-Path -LiteralPath $helperProject)) {
    $helperProject = Join-Path (Get-Location) "src\CalibreBookImporter\CalibreBookImporter.csproj"
}
if (-not (Test-Path -LiteralPath $helperProject)) {
    throw "The .NET helper is unavailable. Do not use a Python fallback."
}
```

## Discovery and selection

1. Interpret the request as a catalog search, not just a filename search. Search Open Library with the .NET helper:

   ```powershell
   $helperProject = Join-Path $env:APPDATA "com.github.githubapp\calibre-book-importer\CalibreBookImporter.csproj"
   if (-not (Test-Path -LiteralPath $helperProject)) {
       $helperProject = Join-Path (Get-Location) "src\CalibreBookImporter\CalibreBookImporter.csproj"
   }
   if (-not (Test-Path -LiteralPath $helperProject)) {
       throw "The .NET helper is unavailable. Do not use a Python fallback."
   }
   dotnet run --project $helperProject -- search --query "Honor Harrington" --limit 20
   ```

   Queries can contain a title, author, genre, subject, or keywords. For example, use `--author "David Weber" --genre "Science Fiction" --keywords "Honor Harrington"`. Refine the query when results are too broad. Open Library is a discovery source, not an authoritative metadata source.
2. Verify candidate identity and edition details against reliable publisher/author sources when available. Open `calibre-book-picker` with `extensionId: "user:calibre-book-picker"`, `mode: "search"`, the query, and the catalog candidates. This extension is user-scoped, so use that extension ID in every repository. Assign an unused valid `instanceId` for this search (for example, `calibre-search-20261113-1253`); do not reuse it for the review canvas. The canvas presents covers, synopsis, author, series, publication details, and source links; the user can refine the catalog search or select specific titles.
3. Tell the user to submit their selection in the canvas and then return to chat. After they indicate they are ready, call `get_selection` for that search instance. Continue only when `mode` is `search` and `submitted` is `true`; use only the returned selections. Selecting candidates is not authorization to modify Calibre. Do not select or import an entire series just because the user named it.
4. After the titles are selected, ask which folder to search for their ebook files unless the user already supplied a source folder in the conversation. Reuse an explicitly supplied path; never infer a library path from a source folder.
5. Search for selected books in filenames and directory names. The helper recursively scans the supplied root and all subfolders, matching normalized title or author phrases against the full relative path. When an author-only path hit has ebook metadata, use it to verify the exact selected title and author. A path-only hit that cannot be verified is returned as `possible_source_match`; show its source path and ask the user to identify or verify it, and do not import it. Pass the exact root folder the user supplied; do not restrict the scan to top-level files or silently substitute a narrower folder. Narrow to an author/series subfolder only if the user requests it or the supplied root is so large that you should ask first. If neither a verified source nor a possible path match is found, report which titles are missing and ask to broaden or narrow the search; do not silently treat an empty scan as a successful result.

## Compare and get approval

1. Prepare a temporary candidate metadata JSON file using the schema below. Ask for the exact Calibre library folder if it is not already known.
2. Show every selected title with its source file, exact title/author match status, existing formats, and proposed action. If the helper returns `possible_source_match`, stop before opening review or importing; show the matching paths and ask the user to verify the exact source file and its metadata. Once every source is verified, open the separate `calibre-book-review` canvas with `extensionId: "user:calibre-book-picker"`, `mode: "review"`, and one item per selected title. Assign it a new unique `instanceId` (for example, `calibre-review-20261113-1254`). Never reopen the search canvas instance for review: reopening focuses the existing panel without changing its input. The review canvas has per-book **Add**, **Replace**, and **Skip** buttons. Include `title`, `authors`, `synopsis`, `series`, `firstPublished`, `coverUrl`, `sourceUrl`, `sourcePath`, `existingFormats`, and the exact `status` (`missing`, `already_in_calibre`, or `ambiguous`):
   - no existing title: add as a new Calibre record;
   - exact existing title/author: offer to replace the entire existing Calibre record, or skip it;
   - ambiguous title/author match: stop and ask the user to resolve it.
3. Ask the user to choose an action for each title using the review canvas buttons, confirm the actions, then return to chat. Call `get_selection` with the review canvas's exact `instanceId` (not the search instance) and proceed only when `mode` is `review` and `submitted` is `true`. Every returned selected item has an `action` of `add` or `replace`; skipped items are returned separately. Ambiguous items cannot be imported or replaced from the canvas and must be resolved first. Replacing an existing record removes all its existing formats and metadata; only pass `--replace-existing` for titles the user explicitly selected for full replacement. Never replace a record just because it has an older format.
4. Confirm which catalog metadata and cover will be used. Prefer the selected edition's official/publisher-quality cover; use the Open Library large cover only when it is a good match and sufficiently clear. The helper accepts Open Library cover URLs or a local `coverPath`; for other verified cover sources, download the image locally and pass its path. Images must be at least 250x350 pixels. If metadata or cover quality cannot be confirmed, state the gap rather than inventing details.

## Import and verify

1. Use this JSON shape for the temporary metadata file, with one object per selected candidate. Include verified values; omit unknown values rather than guessing:

   ```json
   [
     {
       "title": "Book title",
       "authors": ["Author Name"],
       "description": "Verified synopsis",
       "subjects": ["Science Fiction"],
       "publisher": "Publisher",
       "published": "2025",
       "isbns": ["9780000000000"],
       "languages": ["eng"],
       "series": "Series name",
       "seriesIndex": "1",
       "coverUrl": "https://...",
       "coverPath": "C:\\path\\to\\verified-cover.jpg",
       "sourceUrl": "https://..."
     }
   ]
   ```

2. Before asking for final approval, scan for selected filenames, compare the selected source files and Calibre records, and generate a dry-run plan. A selected metadata file filters filenames first to avoid reading metadata from every file in a large collection:

   ```powershell
   $helperProject = Join-Path $env:APPDATA "com.github.githubapp\calibre-book-importer\CalibreBookImporter.csproj"
   if (-not (Test-Path -LiteralPath $helperProject)) {
       $helperProject = Join-Path (Get-Location) "src\CalibreBookImporter\CalibreBookImporter.csproj"
   }
   if (-not (Test-Path -LiteralPath $helperProject)) {
       throw "The .NET helper is unavailable. Do not use a Python fallback."
   }
   dotnet run --project $helperProject -- scan --source-dir "<source>" --metadata-file "<metadata.json>"
   dotnet run --project $helperProject -- compare --source-dir "<source>" --library-path "<library>" --metadata-file "<metadata.json>"
   dotnet run --project $helperProject -- import --source-dir "<source>" --library-path "<library>" --metadata-file "<metadata.json>" --target-format epub --target-format azw3 --replace-existing --dry-run
   ```

   The replacement flag in this command is only a simulation because `--dry-run` prevents writes. It shows the full-record replacement plan for exact existing matches.
3. Present one list combining scan results, Calibre matches, existing formats, and dry-run outcomes. Ask the user to select new imports, exact full-record replacements, and skipped titles. Do not write to Calibre until they confirm this exact list. Filter the final metadata file and `--title` arguments to only those confirmed for import or replacement.
4. After the user confirms the displayed actions, run the import command without `--dry-run`. Add `--replace-existing` only when the user explicitly chose full-record replacement:

   ```powershell
   $helperProject = Join-Path $env:APPDATA "com.github.githubapp\calibre-book-importer\CalibreBookImporter.csproj"
   if (-not (Test-Path -LiteralPath $helperProject)) {
       $helperProject = Join-Path (Get-Location) "src\CalibreBookImporter\CalibreBookImporter.csproj"
   }
   if (-not (Test-Path -LiteralPath $helperProject)) {
       throw "The .NET helper is unavailable. Do not use a Python fallback."
   }
   dotnet run --project $helperProject -- import --source-dir "<source>" --library-path "<library>" --metadata-file "<metadata.json>" --target-format epub --target-format azw3
   ```

5. The helper converts as needed, applies and verifies selected metadata, downloads/sets and verifies a selected cover, imports EPUB and AZW3, and verifies the formats in Calibre. Source files are not modified. Report exact per-title outcomes, missing files, ambiguous matches, replacements, and errors; do not claim success without verification.

## Safety and errors

- Match on normalized exact title and author. Never guess based on a title substring.
- Do not write to a Calibre library until the user has confirmed the exact titles and actions.
- Do not replace existing books or formats unless the user selected that operation. `--replace-existing` means remove the whole old Calibre record before adding the selected book and formats.
- Use a temporary output for conversion. Never overwrite source files.
- If Calibre is not on `PATH`, pass `--calibre-dir "C:\Program Files\Calibre2"` before the command.
- If the helper reports a failed or unverified operation, explain it and do not claim that it succeeded.
