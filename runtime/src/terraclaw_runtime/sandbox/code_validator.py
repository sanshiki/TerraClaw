"""Code validator — static analysis before sandbox execution."""

from __future__ import annotations

import ast
from dataclasses import dataclass


FORBIDDEN_MODULES = {
    "os", "sys", "subprocess", "socket", "requests",
    "urllib", "http", "ftplib", "telnetlib", "smtplib",
    "ctypes", "multiprocessing", "threading", "signal",
    "importlib", "pkgutil", "inspect", "builtins",
    "shutil", "pathlib", "io",
}

FORBIDDEN_BUILTINS = {
    "eval", "exec", "compile", "__import__", "open",
    "input", "breakpoint", "memoryview",
}

ALLOWED_MODULES = {
    "math", "random", "itertools", "collections", "typing",
    "functools", "operator", "json", "datetime", "re",
}


@dataclass
class ValidationResult:
    is_valid: bool
    reason: str = ""
    node_count: int = 0

    @classmethod
    def valid(cls, node_count: int = 0) -> ValidationResult:
        return cls(is_valid=True, node_count=node_count)

    @classmethod
    def invalid(cls, reason: str) -> ValidationResult:
        return cls(is_valid=False, reason=reason)


class CodeValidator:
    """Validates generated Python code before sandbox execution."""

    MAX_CODE_LENGTH = 10000
    MAX_NODE_COUNT = 1000

    def validate(self, code: str) -> ValidationResult:
        if len(code) > self.MAX_CODE_LENGTH:
            return ValidationResult.invalid(
                f"Code too long: {len(code)} chars (max {self.MAX_CODE_LENGTH})",
            )

        try:
            tree = ast.parse(code)
        except SyntaxError as e:
            return ValidationResult.invalid(f"Syntax error: {e}")

        node_count = sum(1 for _ in ast.walk(tree))
        if node_count > self.MAX_NODE_COUNT:
            return ValidationResult.invalid(
                f"Code too complex: {node_count} AST nodes (max {self.MAX_NODE_COUNT})",
            )

        # Check all nodes
        for node in ast.walk(tree):
            result = self._check_node(node)
            if not result.is_valid:
                return result

        return ValidationResult.valid(node_count=node_count)

    def _check_node(self, node: ast.AST) -> ValidationResult:
        if isinstance(node, ast.Import):
            for alias in node.names:
                base = alias.name.split(".")[0]
                if base in FORBIDDEN_MODULES:
                    return ValidationResult.invalid(f"Forbidden import: {alias.name}")
                if base not in ALLOWED_MODULES:
                    return ValidationResult.invalid(
                        f"Module not in allowlist: {alias.name}",
                    )

        elif isinstance(node, ast.ImportFrom):
            if node.module:
                base = node.module.split(".")[0]
                if base in FORBIDDEN_MODULES:
                    return ValidationResult.invalid(f"Forbidden import: {node.module}")
                if base not in ALLOWED_MODULES:
                    return ValidationResult.invalid(
                        f"Module not in allowlist: {node.module}",
                    )

        elif isinstance(node, ast.Call):
            if isinstance(node.func, ast.Name):
                if node.func.id in FORBIDDEN_BUILTINS:
                    return ValidationResult.invalid(f"Forbidden builtin: {node.func.id}")

        return ValidationResult.valid()
