from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

SUPPORTED_EXTENSIONS = {".epub", ".mobi", ".azw3", ".pdf", ".lit", ".rtf", ".html", ".htm"}
FORMAT_PREFERENCE = {".epub": 0, ".azw3": 1, ".mobi": 2, ".pdf": 3, ".lit": 4, ".rtf": 5, ".html": 6, ".htm": 6}


@dataclass
class SourceBook:
    title: str
    author: str
    source_paths: list[Path] = field(default_factory=list)
    preferred_path: Path | None = None
    preferred_format: str = ""
    canonical_title: str = ""
    canonical_author: str = ""


@dataclass
class BookAction:
    title: str
    author: str
    source_path: Path
    format_name: str
    status: str
    library_id: str | None = None
    output_path: str | None = None
    reason: str = ""


class CalibreToolingError(RuntimeError):
    pass


def normalize_text(value: str | None) -> str:
    if value is None:
        return ""
    text = value.strip().lower()
    text = text.replace("_", " ")
    text = text.replace("-", " ")
    text = re.sub(r"[^a-z0-9 ]+", " ", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text


def format_sort_key(path: Path) -> int:
    return FORMAT_PREFERENCE.get(path.suffix.lower(), 99)


def choose_preferred_file(paths: Iterable[Path]) -> Path:
    return sorted(paths, key=lambda p: (format_sort_key(p), str(p).lower()))[0]


def find_calibre_tools(calibre_dir: str | None = None) -> dict[str, str]:
    candidate_dirs: list[Path] = []
    if calibre_dir:
        candidate_dirs.append(Path(calibre_dir))
    if os.name == "nt":
        candidate_dirs.extend([
            Path(r"C:\Program Files\Calibre2"),
            Path(r"C:\Program Files (x86)\Calibre2"),
        ])

    for base in candidate_dirs:
        required = {name: base / name for name in ("calibredb.exe", "ebook-convert.exe", "ebook-meta.exe")}
        if all(path.exists() for path in required.values()):
            return {
                "calibredb": str(required["calibredb.exe"]),
                "ebook_convert": str(required["ebook-convert.exe"]),
                "ebook_meta": str(required["ebook-meta.exe"]),
            }

    for tool in ("calibredb", "ebook-convert", "ebook-meta"):
        resolved = shutil.which(tool)
        if resolved:
            base = Path(resolved).parent
            return {
                "calibredb": str(base / ("calibredb.exe" if os.name == "nt" else "calibredb")),
                "ebook_convert": str(base / ("ebook-convert.exe" if os.name == "nt" else "ebook-convert")),
                "ebook_meta": str(base / ("ebook-meta.exe" if os.name == "nt" else "ebook-meta")),
            }

    raise CalibreToolingError("Could not locate Calibre tools; install Calibre or pass --calibre-dir.")


def run_command(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, capture_output=True, text=True, check=False)


def extract_metadata(path: Path, ebook_meta_exe: str | None = None) -> dict[str, str]:
    metadata: dict[str, str] = {}
    if not path.is_file():
        return metadata

    if ebook_meta_exe:
        result = run_command([ebook_meta_exe, str(path)])
        if result.returncode == 0:
            for line in result.stdout.splitlines():
                if ":" not in line:
                    continue
                key, value = line.split(":", 1)
                key = key.strip().lower()
                value = value.strip()
                if key in {"title", "author", "authors", "publisher", "series", "language", "isbn"}:
                    metadata[key] = value

    if not metadata:
        metadata["title"] = path.stem
        metadata["author"] = ""

    return metadata


def sanitize_title(title: str | None) -> str:
    if not title:
        return ""
    value = title.strip()
    for pattern in (".epub", ".mobi", ".azw3", ".pdf", ".lit", ".rtf", ".html", ".htm"):
        value = value.removesuffix(pattern)
    value = re.sub(r"\s+\((?:ebook|epub|mobi|pdf|azw3)\)", "", value, flags=re.I)
    value = re.sub(r"\s+[-_]+\s*", " ", value)
    value = re.sub(r"\s{2,}", " ", value)
    return value.strip()


def build_group_key(title: str, author: str | None = None) -> str:
    title_key = normalize_text(sanitize_title(title))
    author_key = normalize_text(author or "")
    return f"{title_key}|{author_key}" if title_key else f"{normalize_text(author or '')}|unknown"


def discover_source_books(source_dir: str, requested_titles: Iterable[str] | None = None, calibre_dir: str | None = None) -> list[SourceBook]:
    root = Path(source_dir)
    if not root.exists():
        raise FileNotFoundError(f"Source directory not found: {source_dir}")

    requested_norms = {normalize_text(title) for title in (requested_titles or [])}
    groups: dict[str, SourceBook] = {}
    tool_path = None
    if calibre_dir or shutil.which("ebook-meta") or shutil.which("ebook-meta.exe"):
        try:
            tool_path = find_calibre_tools(calibre_dir).get("ebook_meta")
        except CalibreToolingError:
            tool_path = None

    for file_path in sorted(root.rglob("*")):
        if not file_path.is_file():
            continue
        if file_path.suffix.lower() not in SUPPORTED_EXTENSIONS:
            continue

        if requested_titles:
            file_text = normalize_text(file_path.name)
            if not any(normalize_text(title) in file_text or file_text in normalize_text(title) for title in requested_titles):
                continue

        metadata = extract_metadata(file_path, tool_path)
        title = sanitize_title(metadata.get("title") or file_path.stem)
        author = (metadata.get("author") or metadata.get("authors") or "").strip()
        key = build_group_key(title, author)
        entry = groups.get(key)
        if entry is None:
            entry = SourceBook(title=title, author=author, canonical_title=normalize_text(title), canonical_author=normalize_text(author))
            groups[key] = entry
        entry.source_paths.append(file_path)
        entry.preferred_path = choose_preferred_file(entry.source_paths)
        entry.preferred_format = entry.preferred_path.suffix.lower().lstrip(".")

    ordered = []
    for item in groups.values():
        if not item.preferred_path:
            continue
        item.source_paths = sorted(item.source_paths, key=lambda p: (format_sort_key(p), str(p).lower()))
        item.preferred_path = choose_preferred_file(item.source_paths)
        item.preferred_format = item.preferred_path.suffix.lower().lstrip(".")
        ordered.append(item)
    ordered.sort(key=lambda item: item.canonical_title or item.title.lower())
    return ordered


def parse_library_record(raw: str) -> tuple[str, str, str] | None:
    text = raw.strip()
    if not text:
        return None
    parts = text.split("\t")
    if len(parts) < 3:
        return None
    book_id, title, authors = parts[0], parts[1], parts[2]
    if not book_id.strip().isdigit():
        return None
    return book_id, title, authors


def list_library_records(library_path: str, calibredb_exe: str) -> list[dict[str, str]]:
    result = run_command([
        calibredb_exe,
        "--with-library=" + library_path,
        "list",
        "--template",
        "{id}\t{title}\t{authors}\n",
    ])
    if result.returncode != 0:
        return []

    records: list[dict[str, str]] = []
    for line in result.stdout.splitlines():
        parsed = parse_library_record(line)
        if not parsed:
            continue
        book_id, title, authors = parsed
        records.append({"id": book_id, "title": title, "authors": authors})
    return records


def find_matching_library_books(library_records: list[dict[str, str]], title: str, author: str | None = None) -> list[dict[str, str]]:
    target_title = normalize_text(sanitize_title(title))
    target_author = normalize_text(author or "")
    matches: list[dict[str, str]] = []
    for record in library_records:
        library_title = normalize_text(sanitize_title(record.get("title") or ""))
        library_author = normalize_text(record.get("authors") or "")
        if not library_title:
            continue
        title_match = library_title == target_title or target_title in library_title or library_title in target_title
        author_match = True
        if target_author and library_author:
            author_match = target_author in library_author or library_author in target_author
        if title_match and author_match:
            matches.append(record)
    return matches


def compare_plan(source_dir: str, library_path: str, requested_titles: list[str] | None = None, calibre_dir: str | None = None) -> list[BookAction]:
    tools = find_calibre_tools(calibre_dir)
    discovered = discover_source_books(source_dir, requested_titles, calibre_dir)
    records = list_library_records(library_path, tools["calibredb"])

    actions: list[BookAction] = []
    for book in discovered:
        matches = find_matching_library_books(records, book.title, book.author)
        if len(matches) > 1:
            actions.append(BookAction(book.title, book.author, book.preferred_path or Path(""), book.preferred_format, "ambiguous", reason="Multiple possible matches in the Calibre library."))
        elif matches:
            actions.append(BookAction(book.title, book.author, book.preferred_path or Path(""), book.preferred_format, "already_in_calibre", library_id=matches[0]["id"], reason="Title already exists in the Calibre library."))
        else:
            actions.append(BookAction(book.title, book.author, book.preferred_path or Path(""), book.preferred_format, "missing", reason="Not found in the existing Calibre library."))
    return actions


def unique_output_path(path: Path) -> Path:
    candidate = path
    counter = 1
    while candidate.exists():
        candidate = path.with_name(f"{path.stem}_{counter}{path.suffix}")
        counter += 1
    return candidate


def convert_to_target(source_path: Path, target_format: str, ebook_convert_exe: str) -> Path | None:
    target_suffix = f".{target_format.lower().lstrip('.')}"
    output_path = unique_output_path(source_path.with_suffix(target_suffix))
    result = run_command([
        ebook_convert_exe,
        str(source_path),
        str(output_path),
        "--output-profile=kindle",
    ])
    if result.returncode != 0 or not output_path.exists():
        return None
    return output_path


def import_books(source_dir: str, library_path: str, requested_titles: list[str] | None = None, target_format: str = "azw3", calibre_dir: str | None = None, convert_when_needed: bool = True) -> list[BookAction]:
    tools = find_calibre_tools(calibre_dir)
    discovered = discover_source_books(source_dir, requested_titles, calibre_dir)
    records = list_library_records(library_path, tools["calibredb"])

    actions: list[BookAction] = []
    for book in discovered:
        source_path = book.preferred_path or book.source_paths[0]
        matches = find_matching_library_books(records, book.title, book.author)
        if matches:
            actions.append(BookAction(book.title, book.author, source_path, book.preferred_format, "already_in_calibre", library_id=matches[0]["id"], reason="Title already exists; skipping duplicate import."))
            continue

        target = target_format.lower().lstrip(".")
        candidate_path = source_path
        if source_path.suffix.lower() != f".{target}" and convert_when_needed:
            converted = convert_to_target(source_path, target, tools["ebook_convert"])
            if converted is None:
                actions.append(BookAction(book.title, book.author, source_path, book.preferred_format, "failed", reason=f"Conversion to {target} failed."))
                continue
            candidate_path = converted
        elif source_path.suffix.lower() != f".{target}":
            actions.append(BookAction(book.title, book.author, source_path, book.preferred_format, "skipped", reason=f"Source format {source_path.suffix.lower()} is not the requested target {target}; conversion disabled."))
            continue

        add_result = run_command([
            tools["calibredb"],
            "--with-library=" + library_path,
            "add",
            str(candidate_path),
        ])
        if add_result.returncode != 0:
            actions.append(BookAction(book.title, book.author, source_path, book.preferred_format, "failed", output_path=str(candidate_path), reason=add_result.stderr.strip() or add_result.stdout.strip() or "Import failed."))
        else:
            actions.append(BookAction(book.title, book.author, source_path, book.preferred_format, "imported", output_path=str(candidate_path), reason=f"Imported as {target}."))

    return actions


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Reusable Calibre ebook import and format management tool")
    parser.add_argument("--calibre-dir", help="Optional explicit Calibre installation directory")
    subparsers = parser.add_subparsers(dest="command", required=True)

    scan_parser = subparsers.add_parser("scan", help="Scan a source directory for supported ebooks")
    scan_parser.add_argument("--source-dir", required=True)
    scan_parser.add_argument("--title", dest="titles", action="append", default=[])

    compare_parser = subparsers.add_parser("compare", help="Compare source titles against a Calibre library")
    compare_parser.add_argument("--source-dir", required=True)
    compare_parser.add_argument("--library-path", required=True)
    compare_parser.add_argument("--title", dest="titles", action="append", default=[])

    import_parser = subparsers.add_parser("import", help="Import missing books or attach alternate formats")
    import_parser.add_argument("--source-dir", required=True)
    import_parser.add_argument("--library-path", required=True)
    import_parser.add_argument("--title", dest="titles", action="append", default=[])
    import_parser.add_argument("--target-format", default="azw3")
    import_parser.add_argument("--convert-when-needed", action="store_true")

    return parser


def summarize_results(actions: list[BookAction]) -> dict[str, int | list[str]]:
    found_in_source = 0
    already_in_calibre = 0
    imported = 0
    not_found = 0
    ambiguous = 0
    details: list[str] = []

    for action in actions:
        details.append(action.title)
        if action.status == "already_in_calibre":
            already_in_calibre += 1
        elif action.status == "imported":
            imported += 1
        elif action.status == "missing":
            not_found += 1
        elif action.status == "ambiguous":
            ambiguous += 1
        else:
            found_in_source += 1

    return {
        "FOUND IN SOURCE": found_in_source,
        "ALREADY IN CALIBRE": already_in_calibre,
        "IMPORTED": imported,
        "NOT FOUND": not_found,
        "AMBIGUOUS / NEEDS REVIEW": ambiguous,
        "titles": details,
    }


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        if args.command == "scan":
            books = discover_source_books(args.source_dir, args.titles, args.calibre_dir)
            print(json.dumps([
                {"title": book.title, "author": book.author, "preferred_format": book.preferred_format, "source_paths": [str(path) for path in book.source_paths]}
                for book in books
            ], indent=2))
            return 0

        if args.command == "compare":
            actions = compare_plan(args.source_dir, args.library_path, args.titles, args.calibre_dir)
            print(json.dumps([
                {
                    "title": item.title,
                    "author": item.author,
                    "status": item.status,
                    "format": item.format_name,
                    "source": str(item.source_path),
                    "library_id": item.library_id,
                    "reason": item.reason,
                }
                for item in actions
            ], indent=2))
            return 0

        if args.command == "import":
            actions = import_books(
                source_dir=args.source_dir,
                library_path=args.library_path,
                requested_titles=args.titles,
                target_format=args.target_format,
                calibre_dir=args.calibre_dir,
                convert_when_needed=args.convert_when_needed,
            )
            print(json.dumps([
                {
                    "title": item.title,
                    "author": item.author,
                    "status": item.status,
                    "format": item.format_name,
                    "source": str(item.source_path),
                    "output_path": item.output_path,
                    "library_id": item.library_id,
                    "reason": item.reason,
                }
                for item in actions
            ], indent=2))
            summary = summarize_results(actions)
            print()
            for label, value in (
                ("FOUND IN SOURCE", summary["FOUND IN SOURCE"]),
                ("ALREADY IN CALIBRE", summary["ALREADY IN CALIBRE"]),
                ("IMPORTED", summary["IMPORTED"]),
                ("NOT FOUND", summary["NOT FOUND"]),
                ("AMBIGUOUS / NEEDS REVIEW", summary["AMBIGUOUS / NEEDS REVIEW"]),
            ):
                print(f"{label}: {value}")
            return 0
    except Exception as exc:  # pragma: no cover
        print(f"Error: {exc}", file=sys.stderr)
        return 1

    parser.print_help()
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
