"""Docker sandbox executor — safely runs generated code in isolated containers."""

from __future__ import annotations

import asyncio
import json
import tempfile
from pathlib import Path


class SandboxExecutor:
    """Executes generated code in a Docker sandbox with strict limits."""

    def __init__(
        self,
        image: str = "terraclaw-sandbox:latest",
        max_runtime_s: float = 30.0,
        max_output_chars: int = 10000,
        max_memory_mb: int = 256,
        network_enabled: bool = False,
    ):
        self._image = image
        self._max_runtime = max_runtime_s
        self._max_output = max_output_chars
        self._max_memory = max_memory_mb
        self._network = network_enabled

    async def execute(self, code: str, input_data: dict | None = None) -> SandboxResult:
        """Execute code in a sandboxed Docker container."""
        # Write code to temp file
        with tempfile.NamedTemporaryFile(
            mode="w", suffix=".py", delete=False, prefix="terraclaw_sandbox_",
        ) as f:
            f.write(code)
            code_path = f.name

        try:
            cmd = [
                "docker", "run", "--rm",
                "--read-only",
                "--tmpfs", "/tmp:size=64M,noexec",
                "--tmpfs", "/home/sandbox/work:size=128M,exec",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges:true",
                "--memory", f"{self._max_memory}m",
                "--cpus", "0.5",
                "--pids-limit", "50",
                "--ulimit", "nproc=30:30",
                "--ulimit", "nofile=64:64",
            ]

            if not self._network:
                cmd.append("--network=none")

            cmd.extend([
                "-v", f"{code_path}:/home/sandbox/work/code.py:ro",
                self._image,
                "python", "/home/sandbox/work/code.py",
            ])

            try:
                proc = await asyncio.create_subprocess_exec(
                    *cmd,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                )
                stdout, stderr = await asyncio.wait_for(
                    proc.communicate(),
                    timeout=self._max_runtime,
                )

                output = stdout.decode("utf-8", errors="replace")[:self._max_output]
                error_output = stderr.decode("utf-8", errors="replace")[:self._max_output]

                return SandboxResult(
                    success=proc.returncode == 0,
                    return_code=proc.returncode or 0,
                    stdout=output,
                    stderr=error_output,
                )

            except asyncio.TimeoutError:
                # Kill the container
                await self._kill_container()
                return SandboxResult(
                    success=False,
                    return_code=-1,
                    stdout="",
                    stderr=f"Execution timed out after {self._max_runtime}s",
                )

        finally:
            Path(code_path).unlink(missing_ok=True)

    async def _kill_container(self) -> None:
        """Kill the most recently started terraclaw-sandbox container."""
        proc = await asyncio.create_subprocess_exec(
            "docker", "ps", "-q", "--filter", f"ancestor={self._image}", "--latest",
            stdout=asyncio.subprocess.PIPE,
        )
        stdout, _ = await proc.communicate()
        container_id = stdout.decode().strip()
        if container_id:
            await asyncio.create_subprocess_exec(
                "docker", "kill", container_id,
                stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.DEVNULL,
            )


from dataclasses import dataclass


@dataclass
class SandboxResult:
    success: bool
    return_code: int
    stdout: str
    stderr: str

    @property
    def output(self) -> str:
        return self.stdout if self.success else self.stderr or self.stdout
