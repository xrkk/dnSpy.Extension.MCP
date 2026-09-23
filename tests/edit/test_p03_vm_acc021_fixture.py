"""ACC-021's resource-import sample must exist in its isolated fixture root."""
import tempfile
import unittest
from pathlib import Path

from tests.edit import p03_vm_acc021


class ResourceBlobFixtureTests(unittest.TestCase):
    def test_stages_exact_blob_without_replacing_an_existing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory) / "TestIL.dll"
            fixture.write_bytes(b"fixture")
            blob = p03_vm_acc021.prepare_resource_blob(fixture)
            self.assertEqual(blob, fixture.parent / p03_vm_acc021.BLOB_FILE)
            self.assertEqual(b"T032RESOURCEBLOB", blob.read_bytes())
            self.assertEqual(blob, p03_vm_acc021.prepare_resource_blob(fixture))
            blob.write_bytes(b"foreign")
            with self.assertRaisesRegex(RuntimeError, "unexpected existing resource blob"):
                p03_vm_acc021.prepare_resource_blob(fixture)
            self.assertEqual(b"foreign", blob.read_bytes())


if __name__ == "__main__":
    unittest.main()
