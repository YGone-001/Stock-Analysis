import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original_content = content

    # Resolve ambiguity first by using alias
    if 'using CommunityToolkit.Mvvm.Input;' in content and 'using AIHelper.Helpers;' in content:
        if 'using RelayCommand = AIHelper.Helpers.RelayCommand;' not in content:
            content = content.replace('using AIHelper.Helpers;', 'using AIHelper.Helpers;\nusing RelayCommand = AIHelper.Helpers.RelayCommand;')

    if content != original_content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated {filepath}")

for root, dirs, files in os.walk('e:/AIHelper-dev/src/AIHelper.ViewModels'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
