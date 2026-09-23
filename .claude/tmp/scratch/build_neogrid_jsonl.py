import json, os, sys

ROOT = r"C:\Users\elson.lopes\source\repos\LayoutParserApi\.claude\temp\servidor\layoutparser\Examples"
TCL_ROOT = os.path.join(ROOT, "tcl")
XSL_ROOT = os.path.join(ROOT, "xsl")
OUT = r"C:\Users\elson.lopes\source\repos\LayoutParserApi\.claude\tmp\scratch\neogrid_corpus.jsonl"

PRIORITY_ID = "NFe006c_InutNFe_NeoGridToSefaz"

pairs = []
for docroot, dirs, files in os.walk(TCL_ROOT):
    for f in files:
        if not f.endswith(".tcl"):
            continue
        stem = f[:-4]
        rel = os.path.relpath(docroot, TCL_ROOT)  # ex: NFe/2.06b
        xsl_path = os.path.join(XSL_ROOT, rel, stem + ".xsl")
        tcl_path = os.path.join(docroot, f)
        if not os.path.exists(xsl_path):
            continue
        parts = rel.replace("\\", "/").split("/")
        doc_type = parts[0] if len(parts) > 0 else "?"
        version = parts[1] if len(parts) > 1 else "?"
        with open(tcl_path, "r", encoding="utf-8", errors="replace") as fh:
            tcl_content = fh.read()
        with open(xsl_path, "r", encoding="utf-8", errors="replace") as fh:
            xsl_content = fh.read()
        pairs.append({
            "id": f"{doc_type}/{version}/{stem}",
            "doc_type": doc_type,
            "version": version,
            "source_system": None,
            "target_system": None,
            "input_map_tcl_path": tcl_path,
            "input_map_tcl": tcl_content,
            "output_xslt_path": xsl_path,
            "output_xslt": xsl_content,
        })

# Prioriza o caso da tarefa (fica em 1o lugar -> --limit 1 avalia so ele;
# o resto do corpus ainda entra no indice few-shot como held-out).
pairs.sort(key=lambda p: (0 if p["id"].endswith(PRIORITY_ID) else 1, p["id"]))

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as out:
    for p in pairs:
        out.write(json.dumps(p, ensure_ascii=False) + "\n")

print(f"pares={len(pairs)} -> {OUT}")
print("primeiro:", pairs[0]["id"] if pairs else None)
