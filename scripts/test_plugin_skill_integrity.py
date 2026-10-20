"""Reject hashed but unusable/truncated skill entrypoints before release."""
import unittest
from verify_plugin import validate_skill_content


class SkillIntegrityTests(unittest.TestCase):
    def test_accepts_complete_lf_or_crlf_entrypoint(self):
        text = b'---\nname: example\ndescription: Example workflow\n---\n\nUse the workflow.\n'
        for data in (text, text.replace(b'\n', b'\r\n')):
            self.assertEqual(validate_skill_content({'skills/example/SKILL.md': data}), 1)

    def test_rejects_missing_or_truncated_skills(self):
        for files in ({}, {'skills/example/SKILL.md': b'---'},
                      {'skills/example/SKILL.md': b'---\nname: example\ndescription: example\n---\n'},
                      {'skills/example/SKILL.md': b'---\nname: example\n---\nbody'}):
            with self.assertRaises(ValueError):
                validate_skill_content(files)


if __name__ == '__main__':
    unittest.main()
