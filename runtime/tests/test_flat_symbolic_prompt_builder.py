from __future__ import annotations

from terraclaw_runtime.prompt_builder import FlatSymbolicPromptBuilder


def test_builds_prompt_from_generated_legend() -> None:
    system, messages = FlatSymbolicPromptBuilder.build(
        system="Base system.",
        instruction="Say hello.",
        observation={"npc": {"name": "terra"}},
        symbolic_observation={
            "data": {"n": [2, "terra"], "x": {"state": "idle"}},
            "legend": {"n": ["id", "name"], "x": ["state"]},
            "docs": {"n": {"description": "NPC", "fields": {"id": "whoAmI"}}},
            "conventions": {"booleans": "0=false, 1=true"},
        },
        output_contract={
            "flat": {
                "mode": "one",
                "choices": [{
                    "type": "talk",
                    "fields": [{"name": "text", "type": "string", "required": True, "max": 80}],
                }],
            },
            "legend": [{
                "type": "talk",
                "fields": [{"name": "text", "required": True}],
            }],
            "schema": {"type": "object"},
        },
    )

    assert "n=[id,name]" in system
    assert "x=[state]" in system
    assert "n.id: whoAmI" in system
    assert 'talk={type:"talk",text:str! max=80}' in system
    assert "mode=one" in messages[0]["content"]
    assert "choices=talk(text:str! max=80)" in messages[0]["content"]
    assert '"n":[2,"terra"]' in messages[0]["content"]


def test_falls_back_to_uncompressed_observation() -> None:
    system, messages = FlatSymbolicPromptBuilder.build(
        system="Base system.",
        instruction="Act.",
        observation={"custom": 1},
        symbolic_observation=None,
        output_contract={"schema": {"type": "object"}},
    )

    assert "x=[custom]" in system
    assert '"x":{"custom":1}' in messages[0]["content"]
