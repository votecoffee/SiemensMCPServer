"""Probe native-shaped Comfort XML on the real net48 worker and host compatibility gate."""
import argparse
import json
from pathlib import Path
import tempfile
import xml.etree.ElementTree as ET

from test_comfort_behaviors import BEHAVIORS, change
from test_comfort_screen_texts import host_probe, packet, worker


def native_xml():
    doc = ET.fromstring(BEHAVIORS)
    for node in doc.iter():
        if node.get('CompositionName') == 'ScreenItems':
            attributes = node.find('AttributeList')
            attributes.find('Name').tag = 'ObjectName'
            for name, value in [('Left', '10'), ('Top', '20'), ('Width', '80'), ('Height', '30')]:
                if attributes.find(name) is None:
                    ET.SubElement(attributes, name).text = value
        if node.get('TargetID') and node.get('TargetID') != '@OpenLink':
            node.set('TargetID', '#' + node.get('TargetID'))
    # Root-resource links occur in native function parameters as well as control links.
    ET.SubElement(doc.find('.//Hmi.Event.Event/LinkList'), 'Value', TargetID='#0')
    return ET.tostring(doc, encoding='unicode')


def named(doc, name):
    return next(node for node in doc.iter() if node.findtext('AttributeList/ObjectName') == name)


def probe(bundle):
    xml = native_xml()
    hello, controls, behaviors, refs, layout, captions = worker(bundle, [packet(1, '$hello', {}),
        packet(2, 'inspect_screen_items', {'xml': xml}), packet(3, 'inspect_behaviors', {'xml': xml}),
        packet(4, 'inspect_references', {'xml': xml}), packet(5, 'inspect_screen_layout', {'xml': xml}),
        packet(6, 'inspect_screen_texts', {'xml': xml})])
    assert all(op['snapshotKind'] == 'classicReferences' for op in hello['result']['operations'] if op['route'] == 'classicXml')
    items = controls['result']['items']; assert [r['name'] for r in items] == ['A', 'Group', 'G', 'H', 'C']
    assert all(r['cloneAvailable'] for r in items)
    a, c = items[0], items[-1]; behavior = behaviors['result']['objects'][0]
    assert behavior['ownerName'] == 'A' and behavior['ownerLinks'][0]['targetName'] == 'A'
    assert not behavior['standaloneRemovalAvailable']
    tag = next(r for r in refs['result']['references'] if r['linkType'] == 'Tag')
    assert tag['editable'] and tag['controlName'] == 'A'
    assert next(r for r in refs['result']['references'] if r['linkType'] == 'Value')['targetName'] == 'Screen Ü'
    assert all(r['geometryAvailable'] for r in layout['result']['items'])
    caption = captions['result']['runs'][0]; assert caption['ownerName'] == 'A'
    clone = {'action': 'clone', 'selection': a['selection'], 'destinationXPath': c['containerXPath'],
             'names': [{'sourceXPath': a['xPath'], 'newName': 'Copy'}]}
    event = change(behavior, c['xPath'])
    owner = behavior['ownerLinks'][0]
    event['referenceTargets'] = [{'xPath': owner['xPath'], 'expectedTargetXPath': owner['expectedTargetXPath'], 'newTargetXPath': c['xPath']}]
    invalid = xml.replace('TargetID="#0"', 'TargetID="#missing"')
    replies = worker(bundle, [packet(1, 'change_screen_items', {'changes': [clone]}, xml),
        packet(2, 'change_behaviors', {'changes': [event]}, xml),
        packet(3, 'rebind_references', {'edits': [{'selection': tag['selection'], 'newName': 'Tag_Copy'}]}, xml),
        packet(4, 'arrange_screen_items', {'action': 'move', 'items': [layout['result']['items'][0]['selection']], 'deltaX': 5, 'deltaY': -3}, xml),
        packet(5, 'edit_screen_texts', {'edits': [{'selection': caption['selection'], 'newText': 'Ready'}]}, xml),
        packet(6, 'change_behaviors', {'changes': [change(behavior)]}, xml),
        packet(7, 'inspect_screen_items', {'xml': invalid})])
    assert not any(r.get('error') for r in replies[:5]), replies
    doc = ET.fromstring(replies[0]['result']['candidateXml']); copied = named(doc, 'Copy')
    assert copied.find('AttributeList/Name') is None
    assert copied.find('.//Owner').get('TargetID') == '#' + copied.get('ID')
    assert copied.find('.//Function').get('TargetID') == '#' + copied.find('.//Hmi.Event.Function').get('ID')
    assert copied.find('.//Value').get('TargetID') == '#0'
    assert copied.find('.//Layer').get('TargetID') == '#1' and copied.find('.//b').tail == ' '
    event_copy = named(ET.fromstring(replies[1]['result']['candidateXml']), 'C').findall('.//Hmi.Event.Event')[-1]
    assert event_copy.find('.//Owner').get('TargetID') == '#B' and event_copy.find('.//Value').get('TargetID') == '#0'
    assert ET.fromstring(replies[2]['result']['candidateXml']).find('.//Tag/Name').text == 'Tag_Copy'
    assert named(ET.fromstring(replies[3]['result']['candidateXml']), 'A').findtext('AttributeList/Left') == '15'
    assert ET.fromstring(replies[4]['result']['candidateXml']).find('.//b').text == 'Ready'
    assert replies[5].get('error') and not replies[6]['result']['items'][0]['cloneAvailable']
    return hello['result']['version']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', action='append', type=Path, default=[])
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent/'extensions'/registry['comfort']; version = probe(bundle)
    with tempfile.TemporaryDirectory(prefix='comfort-native-xml-') as temporary:
        root = Path(temporary); hosts = [host_probe(executable, bundle, root/'current', repo)]
        for index, old in enumerate(args.previous_host):
            hosts.append(host_probe(old.resolve(), bundle, root/f'old-{index}', repo, old=True))
    report = {'workerVersion': version, 'checks': [
        'Native ObjectName controls and hash-prefixed internal/owner/resource references resolve',
        'Control and behavior copies remap internal links and preserve external links and authored spaces',
        'Symbolic rebinding, layout and captions work on native-shaped input; unsafe removals/unknown targets are blocked',
        'Current host reload retains PID/hash/tool schemas; older hosts reject the new contract atomically'],
        'hosts': hosts, 'projectAttached': False, 'nativeImportAcceptance': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
