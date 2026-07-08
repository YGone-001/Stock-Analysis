import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original_content = content
    
    # Check if already processed
    if 'ObservableObject' in content:
        return

    # Replace class declaration
    content = re.sub(r'public\s+class\s+([A-Za-z0-9_]+)\s*:\s*INotifyPropertyChanged', r'public partial class \1 : ObservableObject', content)
    
    if content == original_content:
        return # Not an INotifyPropertyChanged class

    # Replace using
    content = content.replace('using System.ComponentModel;', 'using CommunityToolkit.Mvvm.ComponentModel;\nusing CommunityToolkit.Mvvm.Input;')
    if 'CommunityToolkit.Mvvm' not in content:
        content = 'using CommunityToolkit.Mvvm.ComponentModel;\nusing CommunityToolkit.Mvvm.Input;\n' + content

    # 1. Properties
    # private Type _name; public Type Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); } }
    # Let's find full properties
    prop_pattern = r'public\s+([A-Za-z0-9_<>\[\]\?]+)\s+([A-Z][A-Za-z0-9_]*)\s*\{\s*get\s*(?:=>|\{.*?(?:return)?)\s*(_[a-z][A-Za-z0-9_]*)\s*;?\s*\}?\s*set\s*\{[^}]*?[_a-z][A-Za-z0-9_]*\s*=\s*value;[^}]*?OnPropertyChanged\([^)]+\);[^}]*?\}\s*\}'
    
    def repl_prop(m):
        type_str = m.group(1)
        prop_name = m.group(2)
        field_name = m.group(3)
        return f'// REMOVED PROP {prop_name}'
    
    props_to_remove = []
    for m in re.finditer(prop_pattern, content, re.DOTALL):
        prop_name = m.group(2)
        field_name = m.group(3)
        props_to_remove.append((prop_name, field_name))

    for prop_name, field_name in props_to_remove:
        # We delete the property, and we decorate the field.
        # Find the property definition
        pat = r'public\s+([A-Za-z0-9_<>\[\]\?]+)\s+' + prop_name + r'\s*\{\s*get\s*(?:=>|\{).*?\}\s*\}'
        content = re.sub(pat, '', content, flags=re.DOTALL)
        
        # Decorate the field
        field_pat = r'(private\s+[A-Za-z0-9_<>\[\]\?]+\s+' + field_name + r'\s*(?:=.*?)?;)'
        content = re.sub(field_pat, r'[ObservableProperty]\n\t\1', content)

    # 2. Commands
    # private ICommand _cmd; public ICommand Cmd => _cmd ??= new RelayCommand(async delegate(object o) { ... });
    
    cmd_pattern = r'public\s+ICommand\s+([A-Za-z0-9_]+)\s*=>\s*_[A-Za-z0-9_]+\s*\?\?=\s*new\s+(?:Async)?RelayCommand\s*\(\s*(async\s+delegate|delegate|\(object o\) =>|async \(object o\) =>)\s*\([^\)]*\)\s*\{'
    # Actually, replacing commands with regex is too risky for large bodies of code. Let's do properties only, and manually do commands.
    
    # 3. Remove INotifyPropertyChanged event and OnPropertyChanged method
    content = re.sub(r'public\s+event\s+PropertyChangedEventHandler\??\s+PropertyChanged[^;]*;', '', content)
    content = re.sub(r'protected\s+(?:virtual\s+)?void\s+OnPropertyChanged\s*\([^\)]*\)\s*\{[^\}]*\}', '', content)

    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print(f"Updated {filepath}")

for root, dirs, files in os.walk('e:/AIHelper-dev/src/AIHelper.ViewModels'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
