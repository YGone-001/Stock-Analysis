import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original_content = content

    # Find properties like:
    # public Type PropName
    # {
    #     get { return _field; }
    #     set { _field = value; OnPropertyChanged(nameof(PropName)); }
    # }
    
    # We can match public [Type] [PropName] { get ... set ... OnPropertyChanged ... }
    prop_pattern = r'public\s+([A-Za-z0-9_<>\[\]\?]+)\s+([A-Z][A-Za-z0-9_]*)\s*\{\s*get\s*\{?\s*(?:=>\s*|return\s+)(_[a-z][A-Za-z0-9_]*)\s*;?\s*\}?\s*set\s*\{[^}]*?[_a-z][A-Za-z0-9_]*\s*=\s*value;[^}]*?OnPropertyChanged\([^)]+\);[^}]*?\}\s*\}'
    
    props_to_remove = []
    for m in re.finditer(prop_pattern, content, re.DOTALL):
        prop_name = m.group(2)
        field_name = m.group(3)
        props_to_remove.append((prop_name, field_name))

    for prop_name, field_name in props_to_remove:
        pat = r'public\s+([A-Za-z0-9_<>\[\]\?]+)\s+' + prop_name + r'\s*\{\s*get\s*\{?\s*(?:=>\s*|return\s+)' + field_name + r'\s*;?\s*\}?\s*set\s*\{[^}]*?[_a-z][A-Za-z0-9_]*\s*=\s*value;[^}]*?OnPropertyChanged\([^)]+\);[^}]*?\}\s*\}'
        content = re.sub(pat, '', content, flags=re.DOTALL)
        
        field_pat = r'(private\s+[A-Za-z0-9_<>\[\]\?]+\s+' + field_name + r'\s*(?:=.*?)?;)'
        # only add [ObservableProperty] if not already there
        if f'[ObservableProperty]\n\tprivate' not in content and f'[ObservableProperty]\n    private' not in content:
            content = re.sub(field_pat, r'[ObservableProperty]\n\t\1', content)
        else:
            # It might already have it if we run it multiple times, but let's be safe.
            def repl(m):
                s = m.group(1)
                if '[ObservableProperty]' in content[m.start()-30:m.start()]: return s
                return f'[ObservableProperty]\n\t{s}'
            content = re.sub(field_pat, repl, content)

    if content != original_content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated {filepath}")

for root, dirs, files in os.walk('e:/AIHelper-dev/src/AIHelper.ViewModels'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
