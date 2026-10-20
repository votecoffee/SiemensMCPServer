"""Exercise production net48 validation workers with synthetic native-shaped XML only."""
import argparse
import base64
import hashlib
import json
from pathlib import Path

from test_comfort_screen_texts import worker, packet


def probe(bundle):
    xml = ("<Document><Engineering version='V15.1'/><Hmi.TextGraphicList.TextList ID='0'><AttributeList><Name>Choices</Name></AttributeList><ObjectList>"
           "<Hmi.TextGraphicList.TextListEntry ID='1' CompositionName='Entries'><AttributeList><DefaultEntry>false</DefaultEntry><EntryType>SingleValue</EntryType><From>7</From><To>7</To></AttributeList>"
           "<ObjectList><MultilingualText ID='2' CompositionName='Text'><ObjectList><MultilingualTextItem ID='3' CompositionName='Items'><AttributeList><Culture>en-US</Culture><Text><body><p>Ready</p></body></Text></AttributeList>"
           "</MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.TextGraphicList.TextListEntry></ObjectList></Hmi.TextGraphicList.TextList></Document>")
    raw = ("<?xml version='1.0' encoding='utf-8'?>\r\n"+xml).encode()
    screen = ("<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Main</Name></AttributeList><ObjectList>"
              "<Hmi.Screen.SymbolicIOField ID='1' CompositionName='ScreenItems'><AttributeList><ObjectName>State</ObjectName></AttributeList><LinkList><TextList TargetID='@OpenLink'><Name>Choices_1</Name></TextList></LinkList>"
              "</Hmi.Screen.SymbolicIOField></ObjectList></Hmi.Screen.Screen></Document>")
    hello, good, bad, bindings, wrong, changed = worker(bundle, [packet(1, '$hello', {}),
        packet(2, 'validate_native_text_list', {'xmlBytesBase64': base64.b64encode(raw).decode(), 'expectedSha256': hashlib.sha256(raw).hexdigest()}),
        packet(3, 'validate_native_text_list', {'xmlBytesBase64': base64.b64encode(raw.replace(b'utf-8', b'utf8')).decode()}),
        packet(4, 'validate_text_list_bindings', {'xml': screen, 'knownTextLists': ['Choices', 'Choices_1'], 'unchangedScreenAfterXml': screen}),
        packet(5, 'validate_text_list_bindings', {'xml': screen, 'knownTextLists': ['Choices']}),
        packet(6, 'edit_text_list', {'edits': [{'entry': '7', 'culture': 'en-US', 'expectedText': 'Ready', 'newText': 'Reviewed'}]}, xml)])
    assert all(not r.get('error') for r in (hello, good, bad, bindings, wrong, changed)), (hello, good, bad, bindings, wrong, changed)
    assert good['result']['canPublish'] and not bad['result']['canPublish']
    assert bindings['result']['validation']['canPublish'] and not wrong['result']['validation']['canPublish']
    assert bindings['result']['references'][0]['controlName'] == 'State'
    names = {op['name'] for op in hello['result']['operations']}
    assert {'validate_native_text_list', 'validate_text_list_delta', 'validate_text_list_bindings', 'validate_text_list_workflow'} <= names
    candidate = changed['result']['candidate']
    initial = {'beforeXml': xml, 'candidateXml': candidate, 'expectedBeforeFingerprint': '0'*64,
               'edits': [{'entry': '7', 'culture': 'en-US', 'expectedText': 'Ready', 'newText': 'Reviewed'}]}
    stale = worker(bundle, [packet(1, 'validate_text_list_delta', initial)])[0]['result']
    assert not stale['validation']['canPublish']
    initial['expectedBeforeFingerprint'] = stale['beforeFingerprint']
    delta, receipt = worker(bundle, [packet(1, 'validate_text_list_delta', initial), packet(2, 'validate_text_list_workflow', {
        'projectPath': r'C:\Example\Demo.ap15_1', 'processId': 1234, 'hmiId': 'example', 'lists': [{'delta': initial}]})])
    assert delta['result']['validation']['canPublish'] and receipt['result']['validation']['canPublish']
    assert not receipt['result']['suppliedWorkflowComplete'] and receipt['result']['suppliedNativeStages']['compile'] == 'not-run'
    return {'workerVersion': hello['result']['version'], 'checks': ['raw-byte encoding', 'native metadata', 'exact suffixed binding', 'owner evidence', 'reviewed delta', 'stale fingerprint rejection', 'missing native stages remain not-run'], 'projectAttached': False, 'nativeOperationPerformed': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    root = args.executable.resolve().parent/'extensions'
    registry = json.loads((root/'active.json').read_text(encoding='utf-8-sig'))
    report = probe(root/registry['comfort'])
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))
