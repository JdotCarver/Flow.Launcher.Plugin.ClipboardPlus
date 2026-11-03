# inspect_whisper_writer.py
"""Inspect one Python source file without emitting source, paths or user data."""
import ast
import os
import sys
from pathlib import Path


def contains_name(node, name):
    if node is None:
        return False
    return any(isinstance(child, (ast.Name, ast.Attribute)) and
               (getattr(child, 'id', None) == name or getattr(child, 'attr', None) == name)
               for child in ast.walk(node))


def inspect_source(source):
    tree = ast.parse(source)
    functions = [node for node in ast.walk(tree) if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))]
    paste = next((node for node in functions if node.name == '_paste_with_clipboard_preservation'), None)
    if paste is None:
        return {'paste_function': False}

    by_name = {node.name: node for node in functions}
    reachable = []
    pending = [paste]
    seen = set()
    while pending:
        function = pending.pop()
        if function.name in seen:
            continue
        seen.add(function.name)
        reachable.append(function)
        for node in ast.walk(function):
            if isinstance(node, ast.Call):
                name = node.func.attr if isinstance(node.func, ast.Attribute) else getattr(node.func, 'id', None)
                if name in by_name and name not in seen:
                    pending.append(by_name[name])

    def has_sequence_guard(function):
        variables = set()
        for node in ast.walk(function):
            if isinstance(node, (ast.Assign, ast.AnnAssign)) and contains_name(node.value, 'GetClipboardSequenceNumber'):
                targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                variables.update(child.id for target in targets for child in ast.walk(target)
                                 if isinstance(child, ast.Name))
        conditions = [node.test for node in ast.walk(function) if isinstance(node, (ast.If, ast.While, ast.IfExp))]
        return any(contains_name(test, 'GetClipboardSequenceNumber') or
                   any(contains_name(test, name) for name in variables) for test in conditions)

    def reachable_name(name):
        return any(contains_name(function, name) for function in reachable)

    # Follow a sequence number passed into locally defined helper parameters.
    tainted = {function.name: set() for function in reachable}
    changed = True
    while changed:
        changed = False
        for function in reachable:
            names = tainted[function.name]
            for node in ast.walk(function):
                if isinstance(node, (ast.Assign, ast.AnnAssign)) and node.value is not None and (
                    contains_name(node.value, 'GetClipboardSequenceNumber') or
                    any(contains_name(node.value, name) for name in names)
                ):
                    targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                    for target in targets:
                        for item in ast.walk(target):
                            if isinstance(item, ast.Name) and item.id not in names:
                                names.add(item.id)
                                changed = True
                if not isinstance(node, ast.Call):
                    continue
                called_name = node.func.attr if isinstance(node.func, ast.Attribute) else getattr(node.func, 'id', None)
                target = by_name.get(called_name)
                if target is None or called_name not in tainted:
                    continue
                parameters = target.args.args
                if isinstance(node.func, ast.Attribute) and parameters and parameters[0].arg in ('self', 'cls'):
                    parameters = parameters[1:]
                for argument, parameter in zip(node.args, parameters):
                    if contains_name(argument, 'GetClipboardSequenceNumber') or any(contains_name(argument, name) for name in names):
                        if parameter.arg not in tainted[called_name]:
                            tainted[called_name].add(parameter.arg)
                            changed = True
                for keyword in node.keywords:
                    if keyword.arg and (contains_name(keyword.value, 'GetClipboardSequenceNumber') or
                                        any(contains_name(keyword.value, name) for name in names)):
                        if keyword.arg not in tainted[called_name]:
                            tainted[called_name].add(keyword.arg)
                            changed = True

    def guard_checks_sequence(function):
        return any(any(contains_name(node.test, name) for name in tainted[function.name])
                   for node in ast.walk(function) if isinstance(node, (ast.If, ast.While, ast.IfExp)))

    return {
        'paste_function': True,
        'paste_sequence_call': contains_name(paste, 'GetClipboardSequenceNumber'),
        'paste_sequence_guard': has_sequence_guard(paste),
        'paste_enumerates_formats': contains_name(paste, 'EnumClipboardFormats'),
        'paste_restores_formats': contains_name(paste, 'SetClipboardData'),
        'paste_writes_text': contains_name(paste, 'SetClipboardText'),
        'paste_empties_clipboard': contains_name(paste, 'EmptyClipboard'),
        'paste_waits': contains_name(paste, 'sleep'),
        'paste_100ms_constant': any(isinstance(node, ast.Constant) and node.value == 0.1 for node in ast.walk(paste)),
        'reachable_sequence_guard': any(has_sequence_guard(function) for function in reachable),
        'reachable_passed_sequence_guard': any(guard_checks_sequence(function) for function in reachable),
        'reachable_enumerates_formats': reachable_name('EnumClipboardFormats'),
        'reachable_restores_formats': reachable_name('SetClipboardData'),
        'reachable_writes_text': reachable_name('SetClipboardText'),
        'reachable_empties_clipboard': reachable_name('EmptyClipboard'),
        'reachable_waits': reachable_name('sleep'),
        'reachable_registers_format': reachable_name('RegisterClipboardFormat'),
    }



def main():
    try:
        path = Path(sys.argv[1])
        result = inspect_source(path.read_text(encoding='utf-8-sig'))
    except Exception:
        result = {'source_inspection_failed': True}
    output = os.environ.get('GITHUB_OUTPUT')
    lines = ''.join(f'ast_{name}=true\n' for name, present in result.items() if present)
    if output:
        with open(output, 'a', encoding='utf-8') as stream:
            stream.write(lines)
    else:
        print(lines, end='')


if __name__ == '__main__':
    main()
