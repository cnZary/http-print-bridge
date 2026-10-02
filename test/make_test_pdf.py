"""Generate a minimal 3-page PDF (no third-party deps) for print testing."""
import sys

def page_content(n):
    # Simple vector content so rendering has visible output per page.
    return (
        f"BT /F1 36 Tf 72 720 Td (http-print-bridge test page {n}) Tj ET\n"
        f"2 w 72 680 m 523 680 l S\n"
        f"0.2 0.5 0.9 rg 72 200 200 200 re f\n"
        f"0.9 0.3 0.2 rg 300 400 150 150 re f\n"
    ).encode("latin-1")

def build(pages=3):
    objs = []
    # 1: catalog, 2: pages, 3: font
    objs.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    kids = " ".join(f"{4+i} 0 R" for i in range(pages))
    objs.append(f"<< /Type /Pages /Kids [{kids}] /Count {pages} >>".encode())
    objs.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")

    content_ids = []
    for i in range(pages):
        page_id = 4 + i
        content_id = 4 + pages + i
        content_ids.append(content_id)
        objs.append(
            f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] "
            f"/Resources << /Font << /F1 3 0 R >> >> /Contents {content_id} 0 R >>".encode()
        )
    for i in range(pages):
        data = page_content(i + 1)
        objs.append(b"<< /Length %d >>\nstream\n" % len(data) + data + b"endstream")

    out = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for i, body in enumerate(objs, start=1):
        offsets.append(len(out))
        out += f"{i} 0 obj\n".encode() + body + b"\nendobj\n"
    xref_pos = len(out)
    out += f"xref\n0 {len(objs)+1}\n".encode()
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += f"{off:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {len(objs)+1} /Root 1 0 R >>\nstartxref\n{xref_pos}\n%%EOF\n".encode()
    return bytes(out)

if __name__ == "__main__":
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    path = sys.argv[1]
    data = build(n)
    open(path, "wb").write(data)
    print(f"wrote {path}: {len(data)} bytes, {n} pages")
