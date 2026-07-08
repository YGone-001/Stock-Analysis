import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    pattern = r'OnPropertyChanged\(\s*\"([A-Za-z0-9_]+)\"\s*\)'
    new_content = re.sub(pattern, r'OnPropertyChanged(nameof(\1))', content)

    if new_content != content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(new_content)
        print(f'Updated {filepath}')

for root, dirs, files in os.walk('e:/AIHelper-dev/src'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
