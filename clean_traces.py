import os
import re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    # We will use regex to find the blocks.
    # Pattern: 
    # DefaultInterpolatedStringHandler varName = new DefaultInterpolatedStringHandler(...);
    # (varName.AppendLiteral(...) | varName.AppendFormatted(...))*
    # varName.ToStringAndClear()
    
    # Actually, a state machine is easier.
    lines = content.split('\n')
    out_lines = []
    
    i = 0
    changed = False
    while i < len(lines):
        line = lines[i]
        
        # match: DefaultInterpolatedStringHandler varName = new ...
        m = re.search(r'DefaultInterpolatedStringHandler\s+(\w+)\s*=\s*new\s+DefaultInterpolatedStringHandler', line)
        if m:
            var_name = m.group(1)
            # collect following lines
            j = i + 1
            interpolated_parts = []
            
            # optionally, there might be a redundant variable assignment before or after, like `ExportControl exportControl = this;`
            
            valid_block = True
            while j < len(lines):
                next_line = lines[j]
                
                if next_line.strip() == '':
                    j += 1
                    continue
                
                m_lit = re.search(fr'{var_name}\.AppendLiteral\((.*?)\);', next_line)
                if m_lit:
                    # extract string literal without quotes
                    lit = m_lit.group(1)
                    if lit.startswith('"') and lit.endswith('"'):
                        lit = lit[1:-1]
                        # escape braces
                        lit = lit.replace('{', '{{').replace('}', '}}')
                    elif lit.startswith('$"') and lit.endswith('"'):
                        lit = lit[2:-1] # roughly
                    interpolated_parts.append(lit)
                    j += 1
                    continue
                
                m_fmt = re.search(fr'{var_name}\.AppendFormatted\((.*?)\);', next_line)
                if m_fmt:
                    fmt = m_fmt.group(1)
                    # fmt could be `value, "yyyy-MM-dd"`
                    if ', "' in fmt:
                        parts = fmt.split(', "')
                        val = parts[0]
                        format_str = parts[1].rstrip('"')
                        interpolated_parts.append(f"{{{val}:{format_str}}}")
                    else:
                        interpolated_parts.append(f"{{{fmt}}}")
                    j += 1
                    continue
                
                m_end = re.search(fr'(.*){var_name}\.ToStringAndClear\(\)(.*)', next_line)
                if m_end:
                    prefix = m_end.group(1)
                    suffix = m_end.group(2)
                    
                    # replace the line
                    final_string = ''.join(interpolated_parts)
                    replacement = f'{prefix}$"{final_string}"{suffix}'
                    out_lines.append(replacement)
                    changed = True
                    i = j + 1
                    break
                
                # If we encounter something else, maybe it's not a valid block
                # wait, sometimes there's `AnalyticsService.Log("4", varName.ToStringAndClear());`
                if var_name in next_line and 'ToStringAndClear' in next_line:
                    # handled by m_end
                    pass
                elif var_name in next_line:
                    print(f"Warning: Unexpected usage of {var_name} at {filepath}:{j}")
                    valid_block = False
                    break
                else:
                    # some other line interrupting?
                    # e.g., `mainViewModel2.LatencyText = ...`
                    pass
                
                # if we drift too far
                if j - i > 20:
                    valid_block = False
                    break
                    
                # if not matched above, we just continue searching or break?
                # Actually, sometimes we see varName declared but used later.
                # It's safer to just do a strict block match where lines strictly use varName.
                # If there are other lines, we just output them if they don't use varName? No, it might change order.
                break # strict break
                
            if valid_block and m_end:
                continue
            else:
                # rollback
                pass

        # Also remove `[DebuggerNonUserCode]`
        if '[DebuggerNonUserCode]' in line or '[GeneratedCode("PresentationBuildTasks"' in line:
            changed = True
            i += 1
            continue

        out_lines.append(line)
        i += 1
        
    if changed:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write('\n'.join(out_lines))
        print(f"Cleaned: {filepath}")

for root, dirs, files in os.walk('src'):
    for f in files:
        if f.endswith('.cs'):
            process_file(os.path.join(root, f))
