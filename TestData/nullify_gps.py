#!/usr/bin/env python3
"""
nullify_gps.py — strip GPS / position / orientation data from Space Engineers
blueprints (.sbc) while preserving cargo contents.

Place this script in the folder with your .sbc blueprints and run:

    python3 nullify_gps.py            # process every *.sbc in this folder
    python3 nullify_gps.py --dry-run  # show what would change, write nothing
    python3 nullify_gps.py file.sbc   # process specific files only

What it zeroes (everything that is NOT cargo):
    grid PositionAndOrientation (Position/Forward/Up + quaternion),
    block Min positions, BlockOrientation, conveyor line Start/EndPosition +
    Start/EndDirection, LinearVelocity/AngularVelocity, bone positions/offsets,
    colors, projection offsets, packed orientation values.

What it preserves (cargo + identity, byte-for-byte):
    CustomName, EntityId, SubtypeName, inventory Volume/Mass/MaxItemCount,
    every MyObjectBuilder_InventoryItem (Amount, PhysicalContent, ItemId).

Line endings and encoding are preserved exactly (no BOM is added or removed;
CRLF files stay CRLF, LF files stay LF). The script is idempotent: running it
twice changes nothing the second time. After each file it verifies the cargo
item count is unchanged and that the XML still parses; anything off is
reported as a failure and the file is left untouched.

The first half of the file is a unit-testable core (function over a string);
the second half is the CLI.
"""

import re
import sys
import os
import xml.etree.ElementTree as ET
from pathlib import Path

# ---------------------------------------------------------------------------
# core: sanitize one document (string -> string)
# ---------------------------------------------------------------------------

_NUM = r'-?[0-9]+(?:\.[0-9]+)?(?:[Ee]-?[0-9]+)?'

# attribute-style vectors: <Tag x=".." y=".." z=".." ...>
_ZERO_XYZ_TAGS = [
    'Position', 'Forward', 'Up', 'Min', 'BonePosition', 'BoneOffset',
    'LinearVelocity', 'AngularVelocity', 'StartPosition', 'EndPosition',
    'ColorMaskHSV', 'ProjectionOffset', 'PositionOffset',
]

# element-style numeric scalars/quaternions: <X>..</X> etc.
_ZERO_ELEM_TAGS = ['X', 'Y', 'Z', 'W']


def _zero_xyz(m):
    return re.sub(r'x="[^"]*" y="[^"]*" z="[^"]*"',
                  'x="0" y="0" z="0"', m.group(0))


def sanitize_document(data):
    """Zero all non-cargo positional data in a blueprint document string.
    Returns (new_data, stats_dict)."""
    stats = {}
    for tag in _ZERO_XYZ_TAGS:
        data, n = re.subn(rf'<{tag} x="[^"]*" y="[^"]*" z="[^"]*"',
                          _zero_xyz, data)
        stats[tag] = n
    for el in _ZERO_ELEM_TAGS:
        data, n = re.subn(rf'<{el}>{_NUM}</{el}>', f'<{el}>0</{el}>', data)
        stats[f'<{el}>'] = n
    data, n = re.subn(r'<BlockOrientation Forward="\w+" Up="\w+" />',
                      '<BlockOrientation Forward="Forward" Up="Up" />', data)
    stats['BlockOrientation'] = n
    data, n = re.subn(r'<StartDirection>\w+</StartDirection>',
                      '<StartDirection>Left</StartDirection>', data)
    stats['StartDirection'] = n
    data, n = re.subn(r'<EndDirection>\w+</EndDirection>',
                      '<EndDirection>Left</EndDirection>', data)
    stats['EndDirection'] = n
    return data, stats


def count_items(data):
    return data.count('MyObjectBuilder_InventoryItem')


def verify_document(data):
    """Checks the sanitized XML parses and cargo survived."""
    try:
        ET.fromstring(data)
    except ET.ParseError as e:
        return f"XML parse failed: {e}"
    return None


def sanitize_file(path, dry_run=False):
    """Sanitize one blueprint file in place. Returns a report string."""
    path = Path(path)
    raw = path.read_bytes()

    # preserve BOM presence/absence, strip for processing
    bom = raw.startswith(b'\xef\xbb\xbf')
    body = raw[3:] if bom else raw

    try:
        text = body.decode('utf-8')
    except UnicodeDecodeError as e:
        return f"SKIP  {path.name}: not valid UTF-8 ({e})"

    items_before = count_items(text)
    sanitized, stats = sanitize_document(text)
    items_after = count_items(sanitized)

    if items_before != items_after:
        return (f"FAIL  {path.name}: cargo item count changed "
                f"({items_before} -> {items_after}); file left untouched")

    err = verify_document(sanitized)
    if err:
        return f"FAIL  {path.name}: {err}; file left untouched"

    if sanitized == text:
        return f"OK    {path.name}: no GPS data found (already clean?)"

    if dry_run:
        changed = sum(1 for v in stats.values() if v)
        return (f"DRYRUN {path.name}: {changed} tag types would be zeroed "
                f"({sum(stats.values())} occurrences)")
    else:
        out = sanitized.encode('utf-8')
        if bom:
            out = b'\xef\xbb\xbf' + out
        path.write_bytes(out)
        return (f"DONE  {path.name}: zeroed {sum(stats.values())} values "
                f"({items_before} cargo items preserved)")


def main(argv):
    args = [a for a in argv if not a.startswith('-')]
    dry_run = '--dry-run' in argv
    files = [Path(a) for a in args] if args else sorted(Path.cwd().glob('*.sbc'))

    if not files:
        print("No .sbc files found in", Path.cwd())
        return 1

    failures = 0
    for f in files:
        if not f.is_file():
            print(f"SKIP  {f}: not a file")
            continue
        report = sanitize_file(f, dry_run=dry_run)
        print(report)
        if report.startswith(('FAIL', 'SKIP')):
            failures += 1

    if dry_run:
        print(f"\nDry run complete: {len(files)} file(s) checked, nothing written.")
    else:
        print(f"\nDone: {len(files)} file(s) processed.")
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
