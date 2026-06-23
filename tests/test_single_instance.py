import sys
import unittest
import uuid
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from only_fans import SingleInstance


class SingleInstanceTests(unittest.TestCase):
    def test_second_instance_signals_primary_and_exits(self) -> None:
        token = uuid.uuid4().hex
        mutex_name = rf"Local\NhanAZTools.OnlyFansControl.Tests.{token}.Mutex"
        event_name = rf"Local\NhanAZTools.OnlyFansControl.Tests.{token}.Activate"
        primary = SingleInstance(mutex_name, event_name)
        secondary = None
        replacement = None
        try:
            secondary = SingleInstance(mutex_name, event_name)
            self.assertTrue(primary.is_primary)
            self.assertFalse(secondary.is_primary)
            self.assertTrue(primary.activation_requested())
            self.assertFalse(primary.activation_requested())

            primary.close()
            replacement = SingleInstance(mutex_name, event_name)
            self.assertTrue(replacement.is_primary)
        finally:
            if replacement:
                replacement.close()
            if secondary:
                secondary.close()
            primary.close()


if __name__ == "__main__":
    unittest.main()
