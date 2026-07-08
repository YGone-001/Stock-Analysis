import os, re
search_dir = 'src'
for root, dirs, files in os.walk(search_dir):
    if 'obj' in root or 'bin' in root: continue
    for file in files:
        if file.endswith('.cs'):
            filepath = os.path.join(root, file)
            with open(filepath, 'r', encoding='utf-8') as f:
                content = f.read()
            def repl(m):
                return 'catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Swallowed exception in ' + file + ' : {ex}"); }'
            new_content = re.sub(r'catch\s*(?:\([^)]+\))?\s*\{\s*\}', repl, content)
            if new_content != content:
                with open(filepath, 'w', encoding='utf-8') as f:
                    f.write(new_content)
                print(f'Patched {filepath}')
