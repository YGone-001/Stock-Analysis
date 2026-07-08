import os
import re

filepath = 'e:/AIHelper-dev/src/AIHelper.Models/AppConfig.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

cleaned_content = re.sub(r'\s*public bool ExportIncludeHoldingPrompt.*?public string ProxyPassword \{ get; set; \} = "";', '', content, flags=re.DOTALL)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(cleaned_content)
