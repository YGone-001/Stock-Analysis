import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    
    original_content = content
    
    # 1. Replace empty catch { } -> catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }
    content = re.sub(r'catch\s*\{\s*\}', 'catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }', content)
    
    # 2. Replace empty catch (Exception ex) { } -> catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }
    # Also handle catch (Exception) { }
    content = re.sub(r'catch\s*\(\s*Exception\s*\)\s*\{\s*\}', 'catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }', content)
    content = re.sub(r'catch\s*\(\s*System\.Exception\s*\)\s*\{\s*\}', 'catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }', content)
    
    # Empty catch (Exception ex) { }
    content = re.sub(r'catch\s*\(\s*(?:System\.)?Exception\s+([A-Za-z0-9_]+)\s*\)\s*\{\s*\}', r'catch (System.Exception \1) { Serilog.Log.Error(\1, "Swallowed exception"); }', content)

    # 3. Replace System.Diagnostics.Trace.WriteLine($"Swallowed exception in ... : {ex}");
    # with Serilog.Log.Error(ex, "Swallowed exception");
    content = re.sub(r'System\.Diagnostics\.Trace\.WriteLine\([^;]*Swallowed exception[^;]*\);', r'Serilog.Log.Error(ex, "Swallowed exception");', content)
    
    if content != original_content:
        # Add using Serilog if not present
        if 'using Serilog;' not in content and 'Serilog.Log' in content:
            # Insert after the last using
            using_matches = list(re.finditer(r'^using\s+[\w\.]+;$', content, re.MULTILINE))
            if using_matches:
                last_using_pos = using_matches[-1].end()
                content = content[:last_using_pos] + '\nusing Serilog;' + content[last_using_pos:]
            else:
                content = 'using Serilog;\n' + content

        # Replace Serilog.Log with Log now that we have using Serilog;
        content = content.replace('Serilog.Log.', 'Log.')

        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated {filepath}")

for root, dirs, files in os.walk('e:/AIHelper-dev/src'):
    if 'obj' in root or 'bin' in root:
        continue
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
