import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from calibre_book_importer.cli import compare_plan, discover_source_books, import_books


class CliTests(unittest.TestCase):
    def test_scan_groups_multiple_formats_by_title(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            (base / "Honor of the Queen.epub").write_text("epub")
            (base / "Honor of the Queen.pdf").write_text("pdf")

            books = discover_source_books(str(base), None)

            self.assertEqual(len(books), 1)
            self.assertEqual(books[0].preferred_format, "epub")
            self.assertEqual({p.suffix.lower() for p in books[0].source_paths}, {".epub", ".pdf"})

    def test_compare_plan_marks_existing_titles(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            (base / "On Basilisk Station.epub").write_text("book")

            with patch("calibre_book_importer.cli.find_calibre_tools", return_value={
                "calibredb": "calibredb",
                "ebook_convert": "ebook-convert",
                "ebook_meta": "ebook-meta",
            }), patch("calibre_book_importer.cli.list_library_records", return_value=[
                {"id": "123", "title": "On Basilisk Station", "authors": "David Weber"},
            ]):
                actions = compare_plan(str(base), "E:\\Library")

            self.assertEqual(len(actions), 1)
            self.assertEqual(actions[0].status, "already_in_calibre")
            self.assertEqual(actions[0].library_id, "123")

    def test_import_books_skips_duplicates_without_importing(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            (base / "Mission of Honor.epub").write_text("book")

            converted = base / "Mission of Honor.azw3"
            converted.write_text("converted")
            with patch("calibre_book_importer.cli.find_calibre_tools", return_value={
                "calibredb": "calibredb",
                "ebook_convert": "ebook-convert",
                "ebook_meta": "ebook-meta",
            }), patch("calibre_book_importer.cli.list_library_records", return_value=[]), patch("calibre_book_importer.cli.convert_to_target", return_value=converted), patch("calibre_book_importer.cli.run_command", return_value=type("Result", (), {"returncode": 0, "stderr": "", "stdout": ""})()):
                actions = import_books(str(base), "E:\\Library", target_format="azw3", convert_when_needed=True)

            self.assertEqual(len(actions), 1)
            self.assertEqual(actions[0].status, "imported")


if __name__ == "__main__":
    unittest.main()
