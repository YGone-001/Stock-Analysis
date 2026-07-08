import os
import re

files = [
    'e:/AIHelper-dev/src/AIHelper.Helpers/UpdateHelper.cs',
    'e:/AIHelper-dev/src/AIHelper.Helpers/TimeHelper.cs',
    'e:/AIHelper-dev/src/AIHelper.Views/ProxyWindow.cs',
    'e:/AIHelper-dev/src/AIHelper.Services/DataExportEngine.cs',
    'e:/AIHelper-dev/src/AIHelper.Views/ExportControl.cs',
    'e:/AIHelper-dev/src/AIHelper.ViewModels/ChatViewModel.cs',
    'e:/AIHelper-dev/src/AIHelper.ViewModels/MainViewModel.cs'
]

def fix_handler(content):
    pattern = r'DefaultInterpolatedStringHandler\s+(\w+)\s*=\s*new\s*DefaultInterpolatedStringHandler\([^;]+;\s*((?:(?:\1\.AppendLiteral\([^;]+;|\1\.AppendFormatted\([^;]+;)\s*)*)\s*(?:string\s+(\w+)\s*=\s*\1\.ToStringAndClear\(\);|\1\.ToStringAndClear\(\))'
    
    def replacer(match):
        var_name = match.group(1)
        calls_block = match.group(2)
        result_var = match.group(3)
        
        calls = re.findall(rf'{var_name}\.(AppendLiteral|AppendFormatted)\((.*?)\);', calls_block)
        interpolated = '$"'
        for ctype, arg in calls:
            if ctype == 'AppendLiteral':
                if arg.startswith('"') and arg.endswith('"'):
                    val = arg[1:-1].replace('{', '{{').replace('}', '}}')
                    interpolated += val
                else:
                    interpolated += arg
            elif ctype == 'AppendFormatted':
                interpolated += f'{{{arg}}}'
        interpolated += '"'
        if result_var:
            return f'string {result_var} = {interpolated};'
        else:
            return interpolated

    return re.sub(pattern, replacer, content)

for f in files:
    if os.path.exists(f):
        with open(f, 'r', encoding='utf-8') as file:
            c = file.read()
        new_c = fix_handler(c)
        if new_c != c:
            with open(f, 'w', encoding='utf-8') as file:
                file.write(new_c)
            print(f'Fixed {f}')
