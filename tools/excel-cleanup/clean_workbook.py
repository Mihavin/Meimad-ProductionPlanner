"""Build a cleaned, faster copy of a bloated .xlsx workbook. The original is never modified.

What it does
  1. Conditional formatting: drops rules that can never fire (formula is just #REF!),
     merges the thousands of copies that row copy/insert creates back into one rule per
     distinct rule, and trims ranges that run past the used columns (e.g. L4:XFD4).
     Rule order (priority) is rebuilt so every cell keeps exactly the same formatting;
     the script verifies this cell by cell and reports any difference.
  2. Styles: rebuilds the conditional-format style list (dxfs) with only what is used.
  3. Pictures: removes exact duplicate pictures stacked on top of each other and
     downscales embedded images that are far larger than their size on the sheet.
  4. Reports external workbook links (not changed) and whether their targets exist.

Usage
  python clean_workbook.py "\\\\server\\share\\Working plane.xlsx"
  python clean_workbook.py input.xlsx -o output.xlsx [--ppi 220] [--keep-images]
                           [--no-trim-columns] [--force]

Needs Python 3.9+ and Pillow (only for image downscaling).
Run it on a copy made while nobody is saving the file.
"""
import argparse
import bisect
import collections
import hashlib
import io
import os
import posixpath
import re
import sys
import tempfile
import time
import urllib.parse
import zipfile
from xml.etree import ElementTree as ET

MAX_ROWS = 1048576
MAX_COLS = 16384
EMU_PER_PX = 9525                   # at 96 dpi
X14_CF_EXT_URI = "{78C0D931-6437-407d-A8EE-F0AAD7539E65}"

# ----------------------------------------------------------------------------- A1 helpers

def col_to_num(s):
    n = 0
    for ch in s:
        n = n * 26 + ord(ch) - 64
    return n


def num_to_col(n):
    s = ""
    while n:
        n, r = divmod(n - 1, 26)
        s = chr(65 + r) + s
    return s


REF_RE = re.compile(r"^\$?([A-Z]{1,3})?\$?(\d+)?$")


def parse_ref(ref):
    """'A1' / 'A1:C5' / 'A:A' / '3:3' -> (r1, c1, r2, c2), 1-based inclusive."""
    parts = ref.split(":")
    a = REF_RE.match(parts[0])
    b = REF_RE.match(parts[-1])
    if not a or not b:
        raise ValueError(f"unsupported range {ref!r}")
    c1 = col_to_num(a.group(1)) if a.group(1) else 1
    r1 = int(a.group(2)) if a.group(2) else 1
    c2 = col_to_num(b.group(1)) if b.group(1) else MAX_COLS
    r2 = int(b.group(2)) if b.group(2) else MAX_ROWS
    if len(parts) == 1:
        c2, r2 = c1, r1
    return min(r1, r2), min(c1, c2), max(r1, r2), max(c1, c2)


def fmt_rect(r1, c1, r2, c2):
    a = f"{num_to_col(c1)}{r1}"
    return a if (r1, c1) == (r2, c2) else f"{a}:{num_to_col(c2)}{r2}"


def union_rects(rects):
    """Union of rectangles -> list of non-overlapping rectangles (column-strip merge)."""
    if not rects:
        return []
    cuts = sorted({c for _, c1, _, c2 in rects for c in (c1, c2 + 1)})
    strips = []  # (c_start, c_end, row intervals)
    for cs, ce in zip(cuts, cuts[1:]):
        iv = sorted((r1, r2) for r1, c1, r2, c2 in rects if c1 <= cs and c2 >= ce - 1)
        merged = []
        for r1, r2 in iv:
            if merged and r1 <= merged[-1][1] + 1:
                merged[-1][1] = max(merged[-1][1], r2)
            else:
                merged.append([r1, r2])
        merged = tuple(tuple(m) for m in merged)
        if strips and strips[-1][2] == merged and strips[-1][1] == cs - 1:
            strips[-1] = (strips[-1][0], ce - 1, merged)
        elif merged:
            strips.append((cs, ce - 1, merged))
    out = [(r1, cs, r2, ce) for cs, ce, iv in strips for r1, r2 in iv]
    return sorted(out, key=lambda r: (r[1], r[0]))


# ----------------------------------------------------------------------------- XML helpers

ATTR_RE = re.compile(r'([\w:]+)="([^"]*)"')


def attrs_of(tag_text):
    return dict(ATTR_RE.findall(tag_text))


def set_attr(tag_text, name, value):
    if re.search(rf'\b{name}="[^"]*"', tag_text):
        return re.sub(rf'\b{name}="[^"]*"', f'{name}="{value}"', tag_text, count=1)
    return tag_text.replace(" ", f' {name}="{value}" ', 1)


def unescape(s):
    return (s.replace("&quot;", '"').replace("&apos;", "'").replace("&lt;", "<")
             .replace("&gt;", ">").replace("&amp;", "&"))


DEAD_FORMULA_RE = re.compile(r"=?(?:(?:'(?:[^']|'')+'|[^'!\s]+)!)?#REF!")
CONST_FORMULA_RE = re.compile(r'-?\d+(?:\.\d+)?(?:[Ee][+-]?\d+)?|"(?:[^"]|"")*"|TRUE|FALSE')


def is_dead_formula(f):
    return bool(DEAD_FORMULA_RE.fullmatch(unescape(f).strip()))


def is_const_formula(f):
    return bool(CONST_FORMULA_RE.fullmatch(unescape(f).strip()))


# ----------------------------------------------------------------------------- CF model

class Rule:
    __slots__ = ("kind", "open_tag", "inner", "attrs", "priority", "dxf", "rects",
                 "raw_sqref", "block_open", "formulas", "unit", "eff")

    def __init__(self, **kw):
        for k, v in kw.items():
            setattr(self, k, v)


CF_BLOCK_RE = re.compile(r"<conditionalFormatting( [^>]*)?>(.*?)</conditionalFormatting>", re.S)
CF_RULE_RE = re.compile(r"(<cfRule\b[^>]*?)(?:/>|>(.*?)</cfRule>)", re.S)
X14_BLOCK_RE = re.compile(r"(<x14:conditionalFormatting\b[^>]*>)(.*?)</x14:conditionalFormatting>", re.S)
X14_RULE_RE = re.compile(r"(<x14:cfRule\b[^>]*?)(?:/>|>(.*?)</x14:cfRule>)", re.S)


def clip_rects(rects, max_col):
    out = []
    for r1, c1, r2, c2 in rects:
        if max_col and c1 > max_col:
            continue
        out.append((r1, c1, r2, min(c2, max_col) if max_col else c2))
    return out


class Grid:
    """Coordinate-compressed grid: each grid cell is a rectangle that no rule boundary crosses."""

    def __init__(self, rects):
        self.rows = sorted({v for r1, _, r2, _ in rects for v in (r1, r2 + 1)})
        self.cols = sorted({v for _, c1, _, c2 in rects for v in (c1, c2 + 1)})
        self.nc = len(self.cols)

    def cells(self, rect):
        r1, c1, r2, c2 = rect
        i1, i2 = bisect.bisect_left(self.rows, r1), bisect.bisect_left(self.rows, r2 + 1)
        j1, j2 = bisect.bisect_left(self.cols, c1), bisect.bisect_left(self.cols, c2 + 1)
        return [i * self.nc + j for i in range(i1, i2) for j in range(j1, j2)]

    def rect(self, cell):
        i, j = divmod(cell, self.nc)
        return self.rows[i], self.cols[j], self.rows[i + 1] - 1, self.cols[j + 1] - 1

    def area(self, cell):
        r1, c1, r2, c2 = self.rect(cell)
        return (r2 - r1 + 1) * (c2 - c1 + 1)

    def name(self, cell):
        r1, c1, _, _ = self.rect(cell)
        return fmt_rect(r1, c1, r1, c1)


def rule_orders(grid, entries):
    """entries: (priority, unit, rects) -> {grid cell: units in firing order}. Only the first
    occurrence of a unit counts: a later identical rule cannot change the cell's result."""
    per_cell = collections.defaultdict(list)
    for prio, unit, rects in entries:
        for rc in rects:
            for c in grid.cells(rc):
                per_cell[c].append((prio, unit))
    out = {}
    for c, lst in per_cell.items():
        lst.sort()
        seen, seq = set(), []
        for _, u in lst:
            if u not in seen:
                seen.add(u)
                seq.append(u)
        out[c] = tuple(seq)
    return out


def clean_sheet_cf(xml, sheet_name, dxfs, trim_columns):
    """Returns (new_xml, list of dxf contents used in order, stats) or None if no CF."""
    blocks = list(CF_BLOCK_RE.finditer(xml))
    x14_ext = re.search(rf'<ext uri="{re.escape(X14_CF_EXT_URI)}"[^>]*><x14:conditionalFormattings>'
                        r'.*?</x14:conditionalFormattings></ext>', xml, re.S)
    if not blocks and not x14_ext:
        return None
    for a, b in zip(blocks, blocks[1:]):
        if xml[a.end():b.start()].strip():
            raise RuntimeError(f"[{sheet_name}] conditional formatting blocks are not contiguous; not touching this sheet")

    max_col = 0
    if trim_columns:                                  # last column that holds a cell
        dim = re.search(r'<dimension ref="([^"]+)"', xml)
        data = xml.split("</sheetData>")[0]
        cols = {m for m in re.findall(r'<c r="([A-Z]+)\d+"', data)}
        max_col = max([col_to_num(c) for c in cols] + ([parse_ref(dim.group(1))[3]] if dim else []), default=0)
        if max_col >= MAX_COLS:
            max_col = 0

    rules, dead, kept_raw_x14 = [], 0, 0
    for b in blocks:
        battrs = attrs_of(b.group(1) or "")
        rects = [parse_ref(r) for r in battrs.get("sqref", "").split()]
        for m in CF_RULE_RE.finditer(b.group(2)):
            open_tag, inner = m.group(1), m.group(2) or ""
            a = attrs_of(open_tag)
            formulas = re.findall(r"<formula>(.*?)</formula>", inner, re.S)
            if a.get("type") in ("cellIs", "expression") and any(is_dead_formula(f) for f in formulas):
                dead += 1
                continue
            rules.append(Rule(kind="classic", open_tag=open_tag, inner=inner, attrs=a,
                              priority=int(a.get("priority", "0")),
                              dxf=dxfs[int(a["dxfId"])] if "dxfId" in a else None,
                              rects=rects, raw_sqref=battrs.get("sqref", ""),
                              block_open=b.group(1) or "", formulas=formulas, unit=None))
    if x14_ext:
        for b in X14_BLOCK_RE.finditer(x14_ext.group(0)):
            sq = re.search(r"<xm:sqref>(.*?)</xm:sqref>", b.group(2), re.S)
            rects = [parse_ref(r) for r in sq.group(1).split()] if sq else []
            for m in X14_RULE_RE.finditer(b.group(2)):
                open_tag, inner = m.group(1), m.group(2) or ""
                a = attrs_of(open_tag)
                formulas = re.findall(r"<xm:f>(.*?)</xm:f>", inner, re.S)
                if a.get("type") in ("cellIs", "expression") and formulas and any(is_dead_formula(f) for f in formulas):
                    dead += 1
                    continue
                kept_raw_x14 += 1
                rules.append(Rule(kind="x14", open_tag=open_tag, inner=inner, attrs=a,
                                  priority=int(a.get("priority", "0")), dxf=None, rects=rects,
                                  raw_sqref=sq.group(1) if sq else "", block_open=b.group(1),
                                  formulas=formulas, unit=None))
    before = len(rules) + dead

    # ---- group rules into units: identical, position-independent rules merge into one.
    # Anything else (relative formulas, duplicate/top/average rules, data bars...) is "pinned":
    # it keeps its exact original range, because changing the range would change its meaning.
    units = []
    by_key = {}
    for r in rules:
        mergeable = (r.kind == "classic" and r.attrs.get("type") == "cellIs"
                     and "<extLst" not in r.inner and r.formulas
                     and all(is_const_formula(f) for f in r.formulas)
                     and "pivot" not in attrs_of(r.block_open))
        if mergeable:
            sig = tuple(sorted((k, v) for k, v in r.attrs.items() if k not in ("priority", "dxfId")))
            key = ("m", sig, r.inner, r.dxf)
            u = by_key.get(key)
            if u is None:
                u = by_key[key] = {"idx": len(units), "rules": [], "mergeable": True}
                units.append(u)
        else:
            u = {"idx": len(units), "rules": [], "mergeable": False}
            units.append(u)
        u["rules"].append(r)
        r.unit = u["idx"]
        r.eff = clip_rects(r.rects, max_col) if mergeable else r.rects
    pinned = {u["idx"] for u in units if not u["mergeable"]}
    first_prio = [min(r.priority for r in u["rules"]) for u in units]

    grid = Grid([rc for r in rules for rc in r.eff])
    old = rule_orders(grid, [(r.priority, r.unit, r.eff) for r in rules])

    # ---- Copy/paste has left cells whose rules fire in different orders, and order can
    # matter (a "stop if true" rule can hide a later one). Cells are split into layers; one
    # priority order fits every cell of a layer, and each layer gets its own copy of the
    # rules it needs. Layers never overlap, so every cell keeps exactly its current order.
    remaining = dict(old)
    layers = []
    while remaining:
        weight = collections.Counter(remaining.values())
        edges = collections.Counter()
        for seq, n in weight.items():
            for a, b in zip(seq, seq[1:]):
                edges[(a, b)] += n
        if not layers:                                # pinned rules exist once, in layer 0
            for e in list(edges):
                if e[0] in pinned or e[1] in pinned:
                    edges[e] += 10 ** 9
        top = max(weight, key=weight.get)             # guarantees progress
        for e in zip(top, top[1:]):
            edges[e] += 10 ** 15
        order = topo_order(len(units), edges, first_prio)
        pos = {u: i for i, u in enumerate(order)}
        take = {c for c, seq in remaining.items()
                if all(pos[a] < pos[b] for a, b in zip(seq, seq[1:]))
                or (not layers and pinned.intersection(seq))}
        layers.append((order, take))
        for c in take:
            del remaining[c]

    emitted = []                                      # (priority, unit, rects)
    for order, take in layers:
        cells_of = collections.defaultdict(list)
        for c in take:
            for u in old[c]:
                cells_of[u].append(c)
        for u in order:
            if u not in cells_of:
                continue
            rects = units[u]["rules"][0].rects if u in pinned else union_rects([grid.rect(c) for c in cells_of[u]])
            emitted.append((len(emitted) + 1, u, rects))

    # ---- independent check: re-read the ranges as written and compare every formatted cell
    written = [(p, u, [parse_ref(s) for s in (units[u]["rules"][0].raw_sqref if u in pinned
                                              else " ".join(fmt_rect(*rc) for rc in rects)).split()])
               for p, u, rects in emitted]
    vgrid = Grid([rc for r in rules for rc in r.eff] + [rc for _, _, rs in written for rc in rs])
    before_map = rule_orders(vgrid, [(r.priority, r.unit, r.eff) for r in rules])
    after_map = rule_orders(vgrid, written)
    mismatched = [c for c in set(before_map) | set(after_map) if before_map.get(c) != after_map.get(c)]
    checked = sum(vgrid.area(c) for c in before_map)

    # ---- emit XML
    classic_out, x14_out = [], []
    used_dxfs = []
    dxf_index = {}
    for prio, u, rects in emitted:
        r0 = units[u]["rules"][0]
        if r0.kind == "x14":
            open_tag = set_attr(r0.open_tag, "priority", str(prio))
            body = f"{open_tag}>{r0.inner}</x14:cfRule>" if r0.inner else f"{open_tag}/>"
            x14_out.append(f"{r0.block_open}{body}<xm:sqref>{r0.raw_sqref}</xm:sqref></x14:conditionalFormatting>")
            continue
        if u in pinned:
            sqref = r0.raw_sqref
            block_open = re.sub(r'\s*sqref="[^"]*"', "", r0.block_open)
        else:
            sqref = " ".join(fmt_rect(*rc) for rc in rects)
            block_open = ""
        open_tag = set_attr(r0.open_tag, "priority", str(prio))
        if r0.dxf is not None:
            if r0.dxf not in dxf_index:
                dxf_index[r0.dxf] = len(used_dxfs)
                used_dxfs.append(r0.dxf)
            open_tag = set_attr(open_tag, "dxfId", str(dxf_index[r0.dxf]))
        body = f"{open_tag}>{r0.inner}</cfRule>" if r0.inner else f"{open_tag}/>"
        classic_out.append(f'<conditionalFormatting sqref="{sqref}"{block_open}>{body}</conditionalFormatting>')

    new_xml = xml
    if blocks:
        new_xml = xml[:blocks[0].start()] + "".join(classic_out) + xml[blocks[-1].end():]
    if x14_ext:
        ext_text = x14_ext.group(0)
        if x14_out:
            inner = "<x14:conditionalFormattings>" + "".join(x14_out) + "</x14:conditionalFormattings>"
            new_ext = re.sub(r"<x14:conditionalFormattings>.*</x14:conditionalFormattings>", lambda _: inner, ext_text, flags=re.S)
        else:
            new_ext = ""
        new_xml = new_xml.replace(ext_text, new_ext, 1)
        new_xml = new_xml.replace("<extLst></extLst>", "")

    stats = dict(before=before, dead=dead, after=len(emitted), orders=len(set(old.values())),
                 layers=len(layers), mismatched=len(mismatched), checked_cells=checked, trimmed_to=max_col,
                 mismatch_examples=[vgrid.name(c) for c in mismatched[:10]])
    return new_xml, used_dxfs, stats


def topo_order(n, edges, tie_key):
    """Kahn's algorithm; cycles are broken by dropping the weakest edge."""
    import heapq
    edges = dict(edges)
    while True:
        indeg = [0] * n
        out = collections.defaultdict(list)
        for (a, b) in edges:
            out[a].append(b)
            indeg[b] += 1
        heap = [(tie_key[i], i) for i in range(n) if indeg[i] == 0]
        heapq.heapify(heap)
        order = []
        while heap:
            _, a = heapq.heappop(heap)
            order.append(a)
            for b in out[a]:
                indeg[b] -= 1
                if indeg[b] == 0:
                    heapq.heappush(heap, (tie_key[b], b))
        if len(order) == n:
            return order
        stuck = {i for i in range(n) if indeg[i] > 0}
        weakest = min((e for e in edges if e[0] in stuck and e[1] in stuck), key=edges.get)
        del edges[weakest]


# ----------------------------------------------------------------------------- styles

DXFS_RE = re.compile(r"<dxfs\b[^>]*?(?:/>|>(.*?)</dxfs>)", re.S)
DXF_RE = re.compile(r"<dxf>(.*?)</dxf>|<dxf/>", re.S)


def read_dxfs(styles_xml):
    m = DXFS_RE.search(styles_xml)
    if not m or not m.group(1):
        return []
    return [d if d is not None else "" for d in DXF_RE.findall(m.group(1))]


def write_dxfs(styles_xml, dxfs):
    body = "".join(f"<dxf>{d}</dxf>" if d else "<dxf/>" for d in dxfs)
    new = f'<dxfs count="{len(dxfs)}">{body}</dxfs>' if dxfs else '<dxfs count="0"/>'
    return DXFS_RE.sub(lambda _: new, styles_xml, count=1)


# ----------------------------------------------------------------------------- pictures

ANCHOR_RE = re.compile(r"<xdr:(twoCellAnchor|oneCellAnchor|absoluteAnchor)\b[^>]*>.*?</xdr:\1>", re.S)


def rels_map(z, part):
    d, f = posixpath.split(part)
    rp = posixpath.join(d, "_rels", f + ".rels")
    if rp not in z:
        return {}, rp
    x = z[rp].decode("utf-8")
    out = {}
    for rel in re.findall(r"<Relationship\b[^>]*>", x):
        a = attrs_of(rel)
        if a.get("TargetMode") == "External":
            out[a["Id"]] = ("external", a["Target"])
        else:
            out[a["Id"]] = ("internal", posixpath.normpath(posixpath.join(d, a["Target"])))
    return out, rp


def clean_drawing(xml, rels):
    """Remove exact duplicate pictures (same image, same place, same size); keep the top one.
    Returns (new_xml, removed_count, {media_part: [(w_px, h_px, crop_w, crop_h, in_group)]})."""
    anchors = list(ANCHOR_RE.finditer(xml))
    keys = []
    for m in anchors:
        a = m.group(0)
        if "<xdr:pic>" not in a or "<xdr:grpSp>" in a:
            keys.append(None)
            continue
        emb = re.search(r'r:embed="([^"]+)"', a)
        target = rels.get(emb.group(1), (None, None))[1] if emb else None
        # identical except for the object id/name, creation GUID and relationship id
        norm = re.sub(r"<a:extLst>.*?</a:extLst>", "", a, flags=re.S)
        norm = re.sub(r'\s(?:id|name|r:embed)="[^"]*"', "", norm)
        keys.append((target, norm) if target else None)
    last = {}
    for i, k in enumerate(keys):
        if k:
            last[k] = i
    drop = {i for i, k in enumerate(keys) if k and last[k] != i}
    if "AlternateContent" in xml:
        drop = set()                                  # anchors may be wrapped; do not cut them

    usage = collections.defaultdict(list)
    for m in anchors:
        a = m.group(0)
        in_group = "<xdr:grpSp>" in a
        for pic in re.findall(r"<xdr:pic>.*?</xdr:pic>", a, re.S):
            emb = re.search(r'r:embed="([^"]+)"', pic)
            ext = re.search(r'<a:xfrm\b[^>]*>.*?<a:ext cx="(\d+)" cy="(\d+)"', pic, re.S)
            if not emb or emb.group(1) not in rels or rels[emb.group(1)][0] != "internal":
                continue
            src_rect = re.search(r"<a:srcRect\b[^>]*/>", pic)
            crop = attrs_of(src_rect.group(0)) if src_rect else {}
            keep_w = 1 - (int(crop.get("l", 0)) + int(crop.get("r", 0))) / 100000
            keep_h = 1 - (int(crop.get("t", 0)) + int(crop.get("b", 0))) / 100000
            w = int(ext.group(1)) / EMU_PER_PX if ext else 0
            h = int(ext.group(2)) / EMU_PER_PX if ext else 0
            usage[rels[emb.group(1)][1]].append((w, h, keep_w, keep_h, in_group or not ext))
    if drop:
        parts, pos = [], 0
        for i, m in enumerate(anchors):
            if i in drop:
                parts.append(xml[pos:m.start()])
                pos = m.end()
        parts.append(xml[pos:])
        xml = "".join(parts)
    return xml, len(drop), usage


def downscale(data, uses, ppi):
    """Return smaller image bytes or None. `uses`: every place the image is shown."""
    from PIL import Image
    if any(u[4] for u in uses):
        return None
    need_w = max(u[0] * ppi / 96 / max(u[2], 0.05) for u in uses)
    need_h = max(u[1] * ppi / 96 / max(u[3], 0.05) for u in uses)
    try:
        im = Image.open(io.BytesIO(data))
        im.load()
    except Exception:
        return None
    fmt = im.format
    if fmt not in ("PNG", "JPEG") or im.mode not in ("1", "L", "LA", "P", "RGB", "RGBA"):
        return None                                   # CMYK, 16-bit etc.: leave alone
    if fmt == "JPEG" and im.getexif().get(0x0112, 1) != 1:
        return None                                   # rotated via EXIF; leave alone
    w, h = im.size
    scale = max(need_w / w, need_h / h)
    if scale > 0.8 or len(data) < 30000:
        return None
    size = (max(1, round(w * scale)), max(1, round(h * scale)))
    icc = im.info.get("icc_profile")
    try:
        if im.mode in ("1", "P"):
            im = im.convert("RGBA")
        small = im.resize(size, Image.LANCZOS)
        buf = io.BytesIO()
        if fmt == "PNG":
            small.save(buf, "PNG", optimize=True, icc_profile=icc)
        else:
            small.convert("RGB" if small.mode != "L" else "L").save(
                buf, "JPEG", quality=85, optimize=True, icc_profile=icc)
    except Exception:
        return None
    out = buf.getvalue()
    return out if len(out) < len(data) * 0.9 else None


# ----------------------------------------------------------------------------- external links

def external_links_report(z, workbook_dir, sheet_xml_by_name):
    wb = z["xl/workbook.xml"].decode("utf-8")
    wrels, _ = rels_map(z, "xl/workbook.xml")
    lines = []
    ext_ids = re.findall(r'<externalReference r:id="([^"]+)"', wb)
    for idx, rid in enumerate(ext_ids, 1):
        part = wrels.get(rid, (None, None))[1]
        lrels, _ = rels_map(z, part) if part else ({}, None)
        targets = [t for kind, t in lrels.values()]
        target = urllib.parse.unquote(targets[0]) if targets else "?"
        if target.startswith("file:///"):
            local = target[8:].replace("/", "\\")
        elif re.match(r"^[A-Za-z]:\\|^\\\\", target):
            local = target
        else:
            local = os.path.normpath(os.path.join(workbook_dir, target.replace("/", os.sep)))
        exists = os.path.exists(local)
        uses = []
        for sname, sx in sheet_xml_by_name.items():
            data = sx.split("</sheetData>")[0]
            hits = re.findall(rf'<c r="([A-Z]+\d+)"[^>]*>(?:(?!</c>).)*?<f[^>]*>[^<]*\[{idx}\]', data, re.S)
            if hits:
                uses.append(f"{sname}: {len(hits)} formulas (e.g. {', '.join(hits[:4])})")
        lines.append(f"  [{idx}] {'FOUND  ' if exists else 'MISSING'} {target}")
        lines.append(f"        resolves to: {local}")
        lines += [f"        used in {u}" for u in uses] or ["        not used by any cell formula (candidate to remove)"]
    return lines


# ----------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input")
    ap.add_argument("-o", "--output")
    ap.add_argument("--ppi", type=int, default=220, help="target picture resolution (default 220 = print quality)")
    ap.add_argument("--keep-images", action="store_true", help="do not touch pictures")
    ap.add_argument("--no-trim-columns", action="store_true", help="keep conditional-format ranges beyond the used columns")
    ap.add_argument("--force", action="store_true", help="overwrite the output file if it exists")
    args = ap.parse_args()

    src = os.path.abspath(args.input)
    out = os.path.abspath(args.output) if args.output else os.path.splitext(src)[0] + " - cleaned.xlsx"
    if os.path.normcase(src) == os.path.normcase(out):
        sys.exit("Output must be different from the input; the original is never modified.")
    if os.path.exists(out) and not args.force:
        sys.exit(f"{out} already exists (use --force to overwrite).")
    if not os.path.isdir(os.path.dirname(out)):
        sys.exit(f"Output folder {os.path.dirname(out)} does not exist.")

    t0 = time.time()
    with open(src, "rb") as fh:                      # read once so the share file is not held open
        raw = fh.read()
    zin = zipfile.ZipFile(io.BytesIO(raw))
    infos = zin.infolist()
    z = {i.filename: zin.read(i.filename) for i in infos}
    report = [f"Source : {src}", f"Output : {out}", f"Size   : {len(raw)/1e6:.1f} MB", ""]

    wb = z["xl/workbook.xml"].decode("utf-8")
    wrels, _ = rels_map(z, "xl/workbook.xml")
    sheets = [(unescape(n), wrels[rid][1]) for n, rid in re.findall(r'<sheet [^>]*?name="([^"]+)"[^>]*?r:id="([^"]+)"', wb)]
    sheet_xml = {n: z[p].decode("utf-8") for n, p in sheets if p in z}

    # ---- safety: dxfId must only be used by conditional formatting in worksheets
    styles = z["xl/styles.xml"].decode("utf-8")
    dxfs = read_dxfs(styles)
    other_dxf_users = [n for n, b in z.items() if n.endswith(".xml") and b"dxfId" in b
                       and not n.startswith("xl/worksheets/sheet")]
    for n, p in sheets:
        body = CF_BLOCK_RE.sub("", sheet_xml[n])
        if "dxfId" in body:
            other_dxf_users.append(p)
    if re.search(r"dxfId", styles.split("</dxfs>")[-1]):
        other_dxf_users.append("xl/styles.xml")
    if other_dxf_users:
        sys.exit(f"dxfId is also used outside conditional formatting in {other_dxf_users}; this script does not handle that.")

    # ---- conditional formatting
    report.append("Conditional formatting")
    new_dxfs, remap_parts, total_before, total_after, all_ok = [], {}, 0, 0, True
    dxf_pos = {}
    for n, p in sheets:
        res = clean_sheet_cf(sheet_xml[n], n, dxfs, not args.no_trim_columns)
        if res is None:
            continue
        new_xml, used, st = res
        # sheet-local dxf ids -> workbook-wide ids
        local_to_global = {}
        for i, d in enumerate(used):
            if d not in dxf_pos:
                dxf_pos[d] = len(new_dxfs)
                new_dxfs.append(d)
            local_to_global[i] = dxf_pos[d]
        new_xml = re.sub(r'(<cfRule\b[^>]*?\bdxfId=")(\d+)"',
                         lambda m: f'{m.group(1)}{local_to_global[int(m.group(2))]}"', new_xml)
        remap_parts[p] = new_xml
        sheet_xml[n] = new_xml
        total_before += st["before"]
        total_after += st["after"]
        ok = st["mismatched"] == 0
        all_ok &= ok
        trim = f", trimmed to columns A:{num_to_col(st['trimmed_to'])}" if st["trimmed_to"] else ""
        report.append(f"  {n}: {st['before']:,} rules -> {st['after']:,}  "
                      f"(removed {st['dead']:,} broken #REF! rules; {st['orders']} different per-cell "
                      f"rule orders kept in {st['layers']} groups{trim})")
        report.append(f"      check: all {st['checked_cells']:,} formatted cells compared, "
                      + ("formatting identical" if ok else
                         f"{st['mismatched']:,} regions differ, e.g. {st['mismatch_examples']}"))
    report.append(f"  total: {total_before:,} -> {total_after:,} rules; "
                  f"conditional-format styles {len(dxfs):,} -> {len(new_dxfs):,}")
    z["xl/styles.xml"] = write_dxfs(styles, new_dxfs).encode("utf-8")
    for p, x in remap_parts.items():
        z[p] = x.encode("utf-8")

    # ---- pictures
    report.append("")
    report.append("Pictures")
    if args.keep_images:
        report.append("  skipped (--keep-images)")
    else:
        usage = collections.defaultdict(list)
        removed = 0
        for p in [n for n in z if re.match(r"xl/drawings/drawing\d+\.xml$", n)]:
            rels, _ = rels_map(z, p)
            new_xml, dropped, u = clean_drawing(z[p].decode("utf-8"), rels)
            removed += dropped
            for k, v in u.items():
                usage[k] += v
            if dropped:
                z[p] = new_xml.encode("utf-8")
        report.append(f"  duplicate stacked pictures removed: {removed}")
        # images referenced from anything other than a drawing are left alone
        other_refs = set()
        for n in z:
            if n.endswith(".rels") and "/drawings/_rels/drawing" not in n:
                for kind, t in rels_map(z, n.replace("_rels/", "").replace(".rels", ""))[0].values():
                    other_refs.add(t)
        try:
            import PIL  # noqa: F401
            have_pil = True
        except ImportError:
            have_pil = False
            report.append("  Pillow not installed: image downscaling skipped (pip install pillow)")
        if have_pil:
            n_small, saved = 0, 0
            for media, uses in usage.items():
                if media in other_refs or media not in z:
                    continue
                new = downscale(z[media], uses, args.ppi)
                if new:
                    saved += len(z[media]) - len(new)
                    z[media] = new
                    n_small += 1
            report.append(f"  images downscaled to {args.ppi} ppi of their on-sheet size: {n_small} "
                          f"(saved {saved/1e6:.1f} MB)")

    # ---- external links
    report.append("")
    report.append("External workbook links (not changed; fix with Data > Edit Links)")
    report += external_links_report(z, os.path.dirname(src), sheet_xml) or ["  none"]

    # ---- validate modified XML and write the new package
    for n in list(remap_parts) + ["xl/styles.xml"] + [n for n in z if n.startswith("xl/drawings/drawing")]:
        try:
            ET.fromstring(z[n])
        except ET.ParseError as e:
            sys.exit(f"internal error: produced invalid XML in {n}: {e}")
    fd, tmp = tempfile.mkstemp(suffix=".xlsx", dir=os.path.dirname(out))
    os.close(fd)
    try:
        with zipfile.ZipFile(tmp, "w") as zo:
            for i in infos:
                zi = zipfile.ZipInfo(i.filename, date_time=i.date_time)
                zi.compress_type = i.compress_type
                zi.external_attr = i.external_attr
                zo.writestr(zi, z[i.filename], compresslevel=6 if i.compress_type == zipfile.ZIP_DEFLATED else None)
        os.replace(tmp, out)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)

    size = os.path.getsize(out)
    report[3:3] = [f"Result : {size/1e6:.1f} MB ({100 - size*100/len(raw):.0f}% smaller) in {time.time()-t0:.0f}s"]
    if not all_ok:
        report.append("")
        report.append("WARNING: the conditional-formatting check found differences (listed above). "
                      "Do not use the cleaned file; please report this.")
    text = "\n".join(report)
    print(text)
    with open(out + ".report.txt", "w", encoding="utf-8") as fh:
        fh.write(text + "\n")


if __name__ == "__main__":
    main()
