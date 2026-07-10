import os, re
for r, d, files in os.walk('src'):
    for f in files:
        if f.endswith('.cs'):
            path = os.path.join(r, f)
            with open(path, 'r', encoding='utf-8') as f_in:
                text = f_in.read()
            new_text = re.sub(r'(new Uri\("[^"]+)\.baml(", UriKind\.Relative\))', r'\1.xaml\2', text)
            if new_text != text:
                with open(path, 'w', encoding='utf-8') as f_out:
                    f_out.write(new_text)
                print(f'Reverted {path}')
