import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original_content = content

    # Find the property block
    # public [Type] [PropName] \n { \n get \n { \n return _field; \n } \n set \n { \n _field = value; \n OnPropertyChanged(nameof(PropName)); \n } \n }
    prop_pattern = r'public\s+([A-Za-z0-9_<>\[\]\?]+)\s+([A-Z][A-Za-z0-9_]*)\s*\{\s*get\s*\{\s*return\s+(_[a-z][A-Za-z0-9_]*);\s*\}\s*set\s*\{\s*_[a-z][A-Za-z0-9_]*\s*=\s*value;\s*OnPropertyChanged\([^)]+\);\s*\}\s*\}'
    
    props_to_remove = []
    for m in re.finditer(prop_pattern, content):
        prop_name = m.group(2)
        field_name = m.group(3)
        props_to_remove.append((prop_name, field_name))

    for prop_name, field_name in props_to_remove:
        # replace property with empty string
        pat = r'public\s+([A-Za-z0-9_<>\[\]\?]+)\s+' + prop_name + r'\s*\{\s*get\s*\{\s*return\s+' + field_name + r';\s*\}\s*set\s*\{\s*_[a-z][A-Za-z0-9_]*\s*=\s*value;\s*OnPropertyChanged\([^)]+\);\s*\}\s*\}'
        content = re.sub(pat, '', content)
        
        # add [ObservableProperty] to field
        field_pat = r'(private\s+[A-Za-z0-9_<>\[\]\?]+\s+' + field_name + r'\s*(?:=.*?)?;)'
        if f'[ObservableProperty]\n\tprivate' not in content and f'[ObservableProperty]\n    private' not in content:
            content = re.sub(field_pat, r'[ObservableProperty]\n\t\1', content)

    # ICommand properties:
    # private ICommand? _loginCommand; public ICommand LoginCommand => _loginCommand ??= new RelayCommand(...);
    # Since RelayCommand code is usually complex, maybe just leave it, or do it by hand.

    if content != original_content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated {filepath}")

for root, dirs, files in os.walk('e:/AIHelper-dev/src/AIHelper.ViewModels'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
