# Calibre Book Importer

A reusable toolkit for scanning a source ebook directory, comparing discovered books against an existing Calibre library, and adding only the missing formats or books while preserving the originals.

This project was designed around the same safety model used for the Honor Harrington import workflow:

- never overwrite the original source files
- never replace existing Calibre entries without explicit opt-in
- prefer EPUB as the source for AZW3 conversion when available
- skip ambiguous title matches instead of guessing
- add only missing titles or missing formats

## Features

- recursive source directory scans
- supported formats: EPUB, MOBI, AZW3, PDF, LIT, HTML, RTF
- title/author normalization for imperfect or inconsistent filenames
- Calibre library detection via `calibredb`
- duplicate detection against the existing library
- conversion management to AZW3
- format attachment using `calibredb add_format`
- reporting for Found / Already in Calibre / Imported / Not Found / Ambiguous

## Quick start

Install the CLI in editable mode:

```powershell
py -m pip install -e .
```

Then run a source scan:

```powershell
calibre-book-importer scan --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library"
```

Compare against the library without importing:

```powershell
calibre-book-importer compare --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library" --title "On Basilisk Station" --author "David Weber"
```

Import or attach missing AZW3 versions for detected books:

```powershell
calibre-book-importer import --source-dir "C:\Users\tombe\OneDrive\eBooks" --library-path "E:\Library" --title "On Basilisk Station" --author "David Weber" --target-format azw3 --convert-when-needed
```

## Typical workflow

1. Scan the source tree.
2. Group files by normalized title and format.
3. Compare against the existing Calibre library.
4. Skip any title already present.
5. Prefer EPUB when available; otherwise choose the cleanest readable source file.
6. Convert only when needed.
7. Attach the resulting format using `calibredb add_format`.
8. Verify the library again.

## Safety rules built in

- no source file is moved, renamed, or overwritten
- no duplicate Calibre logical books are created
- ambiguous matches stop before making changes
- conversion happens only after a title is confirmed missing

## Calibre tool assumptions

This project uses Calibre’s command-line tools:

- calibredb.exe
- ebook-convert.exe
- ebook-meta.exe

The default Windows install path is:

```text
C:\Program Files\Calibre2\
```

## Example report

```text
FOUND IN SOURCE: 12
ALREADY IN CALIBRE: 0
IMPORTED: 12
NOT FOUND: 3
AMBIGUOUS / NEEDS REVIEW: 0
```

## License

MIT
