import re
import os

log_file = r"C:\Users\pc\.gemini\antigravity\brain\a71ae63d-e28a-4a35-8394-06d41437d048\.system_generated\tasks\task-8.log"

warnings = []
with open(log_file, "r", encoding="utf-8") as f:
    for line in f:
        m = re.search(r"^(E:\\AIHelper-dev[^\(]+)\((\d+),(\d+)\):\s*warning\s+(CS\d+):", line)
        if m:
            filepath = m.group(1).replace("\\\\", "\\")
            line_num = int(m.group(2))
            col_num = int(m.group(3))
            warning_code = m.group(4)
            warnings.append((filepath, line_num, warning_code))

files_to_modify = {}
for w in warnings:
    fp, ln, code = w
    if fp not in files_to_modify:
        files_to_modify[fp] = []
    files_to_modify[fp].append((ln, code))

for fp, file_warnings in files_to_modify.items():
    if not os.path.exists(fp):
        print(f"File not found: {fp}")
        continue
    
    try:
        with open(fp, "r", encoding="utf-8-sig") as f:
            lines = f.readlines()
    except UnicodeDecodeError:
        try:
            with open(fp, "r", encoding="utf-8") as f:
                lines = f.readlines()
        except UnicodeDecodeError:
            with open(fp, "r", encoding="gbk") as f:
                lines = f.readlines()
        
    # Apply CS8618 fixes first
    cs8618_lines = set([ln for ln, code in file_warnings if code == "CS8618"])
    for ln in cs8618_lines:
        idx = ln - 1
        if 0 <= idx < len(lines):
            line_content = lines[idx]
            if "{" in line_content and "}" in line_content and "=" not in line_content and "=>" not in line_content:
                lines[idx] = line_content.rstrip('\r\n') + " = null!;\n"
            elif line_content.strip().endswith(";") and "=" not in line_content and "=>" not in line_content:
                if "{" not in line_content and "}" not in line_content:
                    lines[idx] = line_content.rstrip('\r\n').replace(";", " = null!;\n")
                
    # Suppress other warnings (and un-fixed CS8618 if any) at file level
    codes_to_suppress = set([code for ln, code in file_warnings if code != "CS8618"])
    if cs8618_lines:
        codes_to_suppress.add("CS8618") # Always suppress just in case the regex missed
        
    if codes_to_suppress:
        pragma_str = "#pragma warning disable " + ", ".join(sorted(list(codes_to_suppress)))
        
        insert_idx = 0
        for i, line in enumerate(lines):
            if line.strip().startswith("using ") or line.strip().startswith("#"):
                continue
            if line.strip() == "":
                continue
            insert_idx = i
            break
            
        lines.insert(insert_idx, pragma_str + "\n")
        
    try:
        with open(fp, "w", encoding="utf-8-sig") as f:
            f.writelines(lines)
    except Exception as e:
        print(f"Failed to write {fp}: {e}")

print(f"Processed {len(files_to_modify)} files.")
