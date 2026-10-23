"""Exercise a supplied direct-offline executable over real MCP stdio, using synthetic content only.

No installation, Codex settings, customer files, TIA or feature workers are used.
"""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import unittest

EXE = None
LIMIT = 4 * 1024 * 1024


def analysis(padding=0):
    return {"analysis": {"resources": [{"path": "Screens/minimal.xml", "xml":
        '<?xml version="1.0"?><Document><Engineering version="V15.1"/>'
        '<!--Café 日本語 😀' + 'x' * padding + '--><Hmi.Screen.Screen ID="0">'
        '<AttributeList><Name>Overview</Name></AttributeList></Hmi.Screen.Screen></Document>'}]},
        "view": "references", "maxItems": 1}


class OfflineProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.process = subprocess.Popen([str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        cls.messages = queue.Queue()
        cls.errors = []
        cls.next_id = 0
        cls.addClassCleanup(cls.close)

        def read():
            try:
                for line in cls.process.stdout:
                    cls.messages.put(json.loads(line))
            except Exception as exc:
                cls.messages.put(exc)
            finally:
                cls.messages.put(None)

        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=lambda: cls.errors.extend(cls.process.stderr), daemon=True).start()
        initialized = cls.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "offline-argument-regression", "version": "1.0"}})
        if initialized["serverInfo"]["name"] != "siemens-mcp-server-offline":
            raise AssertionError("Expected the closed direct-offline executable")
        cls.send("notifications/initialized", {}, notification=True)

    @classmethod
    def close(cls):
        cls.process.stdin.close()
        try:
            cls.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.process.kill()  # exact process handle created by this fixture
            cls.process.wait(timeout=5)
            raise AssertionError("Offline fixture failed to drain on EOF")
        finally:
            cls.process.stdout.close()
            cls.process.stderr.close()
        if cls.process.returncode != 0:
            raise AssertionError(f"Offline exit {cls.process.returncode}: {b''.join(cls.errors)!r}")

    @classmethod
    def send(cls, method, params, notification=False):
        cls.next_id += 1
        packet = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            packet["id"] = cls.next_id
        cls.process.stdin.write((json.dumps(packet, ensure_ascii=False) + '\n').encode('utf-8'))
        cls.process.stdin.flush()
        if notification:
            return None
        deadline = time.monotonic() + 30
        while True:
            try:
                message = cls.messages.get(timeout=max(.01, deadline - time.monotonic()))
            except queue.Empty:
                raise AssertionError(f"Offline MCP timeout: {b''.join(cls.errors)!r}")
            if isinstance(message, Exception):
                raise message
            if message is None:
                raise AssertionError(f"Offline MCP closed stdout: {b''.join(cls.errors)!r}")
            if message.get('id') == cls.next_id:
                # Preserve protocol argument failures as well as tool error results.
                return message.get('result', {"isError": True, "content": [{"text":
                    json.dumps(message.get('error'))}]})

    @classmethod
    def call(cls, name, **arguments):
        return cls.send("tools/call", {"name": name, "arguments": arguments})

    def analyze(self, value, operation="analyze_hmi_references"):
        return self.call("siemens_offline_analyze", operation=operation, input=value)

    def success(self, response):
        self.assertFalse(response.get('isError'), response)
        envelope = response['structuredContent']
        self.assertEqual(envelope['profile'], 'direct-offline')
        self.assertFalse(envelope['nativeOperationsPerformed'])
        return envelope['result']

    def error(self, response, text):
        self.assertTrue(response.get('isError'), response)
        self.assertIn(text, ' '.join(item.get('text', '') for item in response['content']))

    def test_advertised_string_contract_matches_minimal_reference_call(self):
        tools = {tool['name']: tool for tool in self.send('tools/list', {})['tools']}
        self.assertEqual(set(tools), {'siemens_offline_catalog', 'siemens_offline_describe', 'siemens_offline_analyze'})
        schema = tools['siemens_offline_analyze']['inputSchema']
        self.assertEqual(schema['properties']['input']['type'], 'string')
        self.assertTrue({'operation', 'input'} <= set(schema['required']))
        result = self.success(self.analyze(json.dumps(analysis(), ensure_ascii=False)))
        self.assertEqual(result['summary']['resources'], 1)
        self.assertFalse(result['summary']['nativeBindingVerified'])

    def test_catalog_and_describe_distinguish_wire_string_from_decoded_schema(self):
        for tool, args in [('siemens_offline_catalog', {}),
                ('siemens_offline_describe', {'operation': 'analyze_hmi_references'}),
                ('siemens_offline_describe', {'operation': 'inspect_text_list'})]:
            response = self.call(tool, **args)
            self.assertFalse(response.get('isError'), response)
            # Plain JsonElement tool returns are also represented in text by the SDK.
            data = json.loads(response['content'][0]['text'])
            self.assertEqual(data['inputContract']['wireType'], 'string')
            self.assertEqual(data['inputContract']['maxDecodedUtf8Bytes'], LIMIT)
            if tool.endswith('describe'):
                self.assertEqual(data['inputSchema']['type'], 'object')

    def test_large_serialized_export_and_unicode_retain_analysis_hash(self):
        value = analysis(853000)
        compact = json.dumps(value, ensure_ascii=False, separators=(',', ':'))
        escaped = json.dumps(value, ensure_ascii=True, indent=2)
        self.assertGreater(len(compact.encode()), 852905)
        first = self.success(self.analyze(compact))
        second = self.success(self.analyze(escaped))
        self.assertEqual(first['summary']['analysisSha256'], second['summary']['analysisSha256'])

    def test_malformed_or_nonobject_json_is_rejected_without_recursive_decoding(self):
        for value in ['{', '', '[]', 'null', 'true', '123', json.dumps(json.dumps(analysis()))]:
            with self.subTest(value=value[:40]):
                self.error(self.analyze(value), 'JSON object')

    def test_duplicate_keys_and_alternate_envelopes_still_fail(self):
        self.error(self.analyze('{"analysis":{"resources":[],"resources":[]}}'), 'Duplicate JSON property: resources')
        self.error(self.analyze('{"arguments":{"xml":"<Document/>"}}', 'inspect_xml'), 'arguments directly')
        self.error(self.analyze('{}', 'tia_attach'), 'unavailable in the direct offline profile')

    def test_decoded_utf8_limit_includes_whitespace_and_multibyte_content(self):
        # Each request stays below the separate 8 MiB wire-frame bound.
        for value in [' ' * LIMIT + '{}', json.dumps({'xml': 'é' * (LIMIT // 2)}, ensure_ascii=False)]:
            self.assertGreater(len(value.encode('utf-8')), LIMIT)
            self.error(self.analyze(value, 'inspect_xml'), '4 MiB')
        self.success(self.analyze(json.dumps(analysis())))


if __name__ == '__main__':
    EXE = Path(sys.argv.pop(1)).resolve()
    unittest.main(verbosity=2)
