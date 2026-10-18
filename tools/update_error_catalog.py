"""
Regenerates src/WUGrid.Core/Errors/error-codes.microsoft.tsv from Microsoft Learn (CC BY 4.0).

    python tools/update_error_catalog.py

Sources (see THIRD-PARTY-NOTICES.md):
  - Windows Update error code list by component  -> every WU_E_* code with its description
  - Common Windows Update errors                 -> description + mitigation for the codes seen most
  - Fix Windows Update corruptions and installation failures -> CBS / SxS corruption codes
  - .NET Framework 3.5 deployment errors         -> CBS feature-install codes with resolution steps
  - WMI Error Constants                          -> WBEM_E_* codes (what a remote WMI call can fail with)

Microsoft does not publish a complete list of CBS_E_* codes; only the ones documented on these pages are included.
Codes WUGrid itself runs into are explained in the hand-written error-codes.wugrid.tsv next to the output.
Standard library only.
"""
import re
import sys
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

LEARN = "https://learn.microsoft.com/en-us/"
SOURCES = {
    "wu-reference": LEARN + "windows/deployment/update/windows-update-error-reference",
    "wu-common": LEARN + "troubleshoot/windows-client/deployment/common-windows-update-errors",
    "fix-corruptions": LEARN + "troubleshoot/windows-server/deployment/fix-windows-update-errors",
    "netfx35": LEARN + "windows-hardware/manufacture/desktop/net-framework-35-deployment-errors-and-resolution-steps",
    "wmi": LEARN + "windows/win32/wmisdk/wmi-error-constants",
}
OUT = Path(__file__).resolve().parent.parent / "src" / "WUGrid.Core" / "Errors" / "error-codes.microsoft.tsv"
CODE = re.compile(r"0[xX][0-9A-Fa-f]{8}")


class Blocks(HTMLParser):
    """Flattens <main> into (kind, text) blocks: h2/h3 headings, table rows (list of cells), dt/dd items."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.main = False
        self.open = None
        self.buf = []
        self.row = None
        self.blocks = []

    def handle_starttag(self, tag, attrs):
        if tag == "main":
            self.main = True
        if not self.main:
            return
        if tag == "tr":
            self.row = []
        elif tag in ("h2", "h3", "dt", "dd", "th", "td") and self.open is None:
            self.open, self.buf = tag, []
        elif tag == "br" and self.open:
            self.buf.append(" ")

    def handle_endtag(self, tag):
        if not self.main:
            return
        if tag == "main":
            self.main = False
        elif tag == self.open:
            text = " ".join("".join(self.buf).split())
            if tag in ("th", "td"):
                if self.row is not None:
                    self.row.append(text)
            else:
                self.blocks.append((tag, text))
            self.open = None
        elif tag == "tr" and self.row is not None:
            self.blocks.append(("tr", self.row))
            self.row = None

    def handle_data(self, data):
        if self.open:
            self.buf.append(data)


def blocks(url):
    req = urllib.request.Request(url, headers={"User-Agent": "WUGrid-catalog-update"})
    with urllib.request.urlopen(req, timeout=60) as r:
        p = Blocks()
        p.feed(r.read().decode("utf-8", errors="replace"))
        return p.blocks


def family(name, code):
    if name.startswith(("CBS_", "PSFX_")):
        return "CBS"
    if name.startswith("WU_"):
        return "Windows Update"
    if name.startswith("WBEM_"):
        return "WMI"
    if code.startswith("0x8007") or name.startswith("ERROR_"):
        return "Win32"
    return "Windows Update" if code.startswith(("0x8024", "0x8020", "0x80D0")) else "Other"


def table_rows(bs):
    """Yields (header, row) for every data row, header being the lower-cased column names of its table."""
    header = None
    for kind, val in bs:
        if kind != "tr":
            continue
        if any(c.lower() in ("error code", "code", "message", "requirement") for c in val[:1]) or not CODE.search(" ".join(val) + "x") and header is None:
            header = [c.lower() for c in val]
            continue
        if header:
            yield header, val


def col(header, row, *names):
    for n in names:
        if n in header and header.index(n) < len(row):
            return row[header.index(n)]
    return ""


def main():
    entries = {}   # (code, name) -> dict

    def add(code, name, desc, mitigation, source):
        code = "0x" + code[2:].upper()
        # The common-errors page writes "NAME; message text" in its Message column
        name, _, message = name.partition(";")
        name, message = name.strip(), message.strip()
        if message and message.rstrip(".").lower() not in desc.lower():
            desc = f"{message.rstrip('.')}. {desc}" if desc else message
        key = (code, name)
        e = entries.setdefault(key, {"code": code, "name": name, "desc": "", "mitigation": "", "sources": []})
        if desc and len(desc) > len(e["desc"]):
            e["desc"] = desc
        if mitigation and not e["mitigation"]:
            e["mitigation"] = mitigation
        if source not in e["sources"]:
            e["sources"].append(source)

    # Reference tables: Error code | Message (= name) | Description
    for header, row in table_rows(blocks(SOURCES["wu-reference"])):
        code = col(header, row, "error code")
        if CODE.fullmatch(code):
            add(code, col(header, row, "message"), col(header, row, "description"), "", "wu-reference")

    # Common errors: <h2>0x... [or 0x...]</h2> then Message | Description | Mitigation
    codes = []
    for kind, val in blocks(SOURCES["wu-common"]):
        if kind == "h2":
            codes = CODE.findall(val)
        elif kind == "tr" and codes and val and val[0].lower() != "message":
            names = val[0].split() if val[0] else [""]
            for i, code in enumerate(codes):
                name = names[i] if len(names) == len(codes) else val[0]
                add(code, name, val[1] if len(val) > 1 else "", val[2] if len(val) > 2 else "", "wu-common")

    for header, row in table_rows(blocks(SOURCES["fix-corruptions"])):
        code = col(header, row, "code")
        if CODE.fullmatch(code):
            add(code, col(header, row, "error"), col(header, row, "description"), "", "fix-corruptions")

    for header, row in table_rows(blocks(SOURCES["netfx35"])):
        code = col(header, row, "error code")
        if CODE.fullmatch(code):
            add(code, col(header, row, "name"), col(header, row, "description"), col(header, row, "resolution steps"), "netfx35")

    # WMI constants: <dt>WBEM_E_X</dt> <dd>2147749889 (0x80041001) Description</dd>
    name = None
    for kind, val in blocks(SOURCES["wmi"]):
        if kind == "dt":
            name = val if re.fullmatch(r"WBEM_[A-Z_]+", val) else None
        elif kind == "dd" and name and CODE.search(val):
            m = CODE.search(val)
            add(m.group(0), name, val[m.end():].lstrip(") ").strip(), "", "wmi")
            name = None

    rows = sorted(entries.values(), key=lambda e: (e["code"], e["name"]))
    clean = lambda s: " ".join(s.replace("\t", " ").split())
    with OUT.open("w", encoding="utf-8", newline="\n") as f:
        f.write("# Generated by tools/update_error_catalog.py from Microsoft Learn (CC BY 4.0) - do not edit by hand.\n")
        f.write("# Sources and licence: THIRD-PARTY-NOTICES.md\n")
        f.write("code\tname\tfamily\tdescription\tmitigation\tsource\n")
        for e in rows:
            f.write("\t".join([e["code"], e["name"], family(e["name"], e["code"]), clean(e["desc"]), clean(e["mitigation"]),
                               ",".join(e["sources"])]) + "\n")
    by_family = {}
    for e in rows:
        by_family[family(e["name"], e["code"])] = by_family.get(family(e["name"], e["code"]), 0) + 1
    print(f"{len(rows)} entries -> {OUT}  {by_family}")


if __name__ == "__main__":
    sys.exit(main())
