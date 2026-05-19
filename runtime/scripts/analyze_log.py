#!/usr/bin/env python3
"""Analyze TerraClaw runtime.log — extract timeline, LLM behavior, tool usage, and problems.

Usage:
    python scripts/analyze_log.py                          # default: logs/runtime.log
    python scripts/analyze_log.py path/to/runtime.log      # custom path
    python scripts/analyze_log.py --json                   # JSON output
    python scripts/analyze_log.py --no-timeline            # suppress full timeline
    python scripts/analyze_log.py --turn 3                 # show full context for LLM call #3
"""

import re
import sys
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path


def parse_log(path: str) -> list[dict]:
    """Parse structlog KeyValueRenderer lines into list of event dicts."""
    with open(path, encoding="utf-8") as f:
        lines = f.readlines()

    events = []
    for line in lines:
        fields = {}
        for m in re.finditer(r"(\w+)='([^']*)'", line):
            fields[m.group(1)] = m.group(2)
        if "event" in fields:
            fields["raw"] = line.strip()
            events.append(fields)
    return events


def fmt_elapsed(seconds: float) -> str:
    if seconds < 60:
        return f"{seconds:.1f}s"
    return f"{seconds/60:.0f}m{seconds%60:.0f}s"


def show_turn(events: list[dict], turn_n: int):
    """Show detailed context for a specific LLM call turn."""
    # Find all llm_call events
    call_indices = [i for i, e in enumerate(events) if e["event"] == "llm_call"]
    if not call_indices:
        print("No llm_call events found in log.")
        return

    if turn_n < 1 or turn_n > len(call_indices):
        print(f"Turn {turn_n} out of range. Only {len(call_indices)} LLM calls (1-{len(call_indices)}).")
        return

    idx = call_indices[turn_n - 1]
    call = events[idx]
    ts = call.get("timestamp", "")

    print(f"{'='*60}")
    print(f"  LLM CALL #{turn_n}")
    print(f"  Time: {_fmt_ts(ts)}")
    print(f"{'='*60}")

    # Show all context fields from llm_call
    _show_fields(call, skip=("event", "timestamp", "level", "raw", "msg"))

    msg_text = call.get("msg", "")
    if msg_text:
        print(f"\n  Type: {msg_text}")

    # Show follow-up events (response + tool_results), skip httpx/httpcore noise
    _NOISE_EVENTS = frozenset({"Request options:", "Sending HTTP Request", "HTTP Response",
                                "request_id", "send_request_headers", "send_request_body",
                                "response_closed", "connect_tcp", "start_tls",
                                "receive_response_body"})
    print(f"\n  ── Subsequent events ──")
    for j in range(idx + 1, min(idx + 30, len(events))):
        e = events[j]
        evt = e["event"]
        if evt == "llm_call":
            break  # next turn
        if any(evt.startswith(n) for n in _NOISE_EVENTS):
            continue

        elapsed = _elapsed_since(events[0], e)

        if evt == "llm_turn_start":
            print(f"\n  [{elapsed:>6.1f}s] {evt}")
        elif evt == "llm_response":
            print(f"\n  [{elapsed:>6.1f}s] llm_response")
            _show_response_detail(e)
        elif evt == "tool_result":
            _show_tool_result_detail(e, elapsed)
    print()


def _fmt_ts(ts: str) -> str:
    try:
        t = datetime.fromisoformat(ts.replace("Z", "+00:00"))
        return t.strftime("%H:%M:%S.%f")[:12]
    except:
        return ts[-12:]


def _elapsed_since(first: dict, event: dict) -> float:
    try:
        t0 = datetime.fromisoformat(first.get("timestamp", "").replace("Z", "+00:00"))
        t1 = datetime.fromisoformat(event.get("timestamp", "").replace("Z", "+00:00"))
        return (t1 - t0).total_seconds()
    except:
        return 0.0


def _show_fields(event: dict, skip: tuple[str, ...] = ()):
    raw = event.get("raw", "")
    for key in ("pending_actions", "has_instructions", "action_results", "tick",
                "messages", "tools", "pos", "finish_reason", "tokens", "tool_args",
                "tool_calls", "text", "result", "instructions_summary", "completed_actions",
                "action_results_summary"):
        if key in skip:
            continue
        val = event.get(key)
        if val is not None:
            # Decode escaped strings
            display = val.replace("\\n", "\n    ").replace("\\'", "'") if isinstance(val, str) else val
            print(f"    {key}: {display}")


def _show_response_detail(e: dict):
    fr = e.get("finish_reason", "")
    toks = e.get("tokens", "")
    tc_raw = e.get("tool_calls", "[]")
    ta_raw = e.get("tool_args", "[]")
    text = e.get("text", "")

    if toks:
        print(f"    tokens={toks}  finish={fr}")
    if tc_raw and tc_raw != "[]":
        # show tool_calls with their args
        try:
            tcs = eval(tc_raw)
            tas = eval(ta_raw) if ta_raw and ta_raw != "[]" else []
            for i, tname in enumerate(tcs):
                targs = tas[i] if i < len(tas) else "?"
                print(f"    tool: {tname}({targs})")
        except:
            print(f"    tools: {tc_raw}")
    if text:
        text_short = text[:300]
        print(f"    text: {text_short}")


def _show_tool_result_detail(e: dict, elapsed: float):
    raw = e.get("raw", "")
    tool = re.search(r"tool='([^']*)'", raw)
    tname = tool.group(1) if tool else "?"

    err = ""
    if "FAILED" in raw or "error" in raw.lower():
        err_match = re.search(r"error':\s*'([^']*)'", raw)
        if err_match:
            err = err_match.group(1)
    elif "'status':" in raw:
        s = re.search(r"'status':\s*'([^']*)'", raw)
        status = s.group(1) if s else "?"
        print(f"  [{elapsed:>6.1f}s] tool: {tname}  → {status}")
        return

    if err:
        print(f"  [{elapsed:>6.1f}s] tool: {tname}  ERROR: {err[:150]}")
    else:
        print(f"  [{elapsed:>6.1f}s] tool: {tname}  {raw[-100:]}")


def analyze(events: list[dict], show_timeline: bool = True):
    start_ts = events[0].get("timestamp", "") if events else ""
    try:
        t0 = datetime.fromisoformat(start_ts.replace("Z", "+00:00"))
    except (ValueError, IndexError):
        t0 = None

    total_time = 0.0
    if t0 and len(events) >= 2:
        for e in reversed(events):
            ts = e.get("timestamp", "")
            if ts:
                try:
                    t = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                    total_time = (t - t0).total_seconds()
                    break
                except ValueError:
                    pass

    # ── Event type counts ──
    event_types = Counter(e["event"] for e in events)
    levels = Counter(e.get("level", "?") for e in events)

    print(f"Runtime duration: {fmt_elapsed(total_time)}")
    print(f"Total events: {len(events)}")
    print(f"Levels: {dict(levels)}")
    print()

    # ── Errors / warnings ──
    errs = [e for e in events if e.get("level") in ("error", "warning", "critical")]
    if errs:
        print(f"=== ERRORS & WARNINGS ({len(errs)}) ===")
        for e in errs:
            print(f"  [{e.get('level')}] {e.get('event')}: {e.get('raw', '')[:200]}")
        print()

    # ── LLM analysis ──
    llm_calls = [e for e in events if e["event"] == "llm_call"]
    llm_responses = [e for e in events if e["event"] == "llm_response"]

    print(f"=== LLM: {len(llm_calls)} calls → {len(llm_responses)} responses ===")

    # Intervals
    timestamps = [e["timestamp"] for e in llm_calls if e.get("timestamp")]
    if len(timestamps) >= 2:
        gaps = []
        for i in range(1, len(timestamps)):
            try:
                t1 = datetime.fromisoformat(timestamps[i - 1].replace("Z", "+00:00"))
                t2 = datetime.fromisoformat(timestamps[i].replace("Z", "+00:00"))
                gaps.append((t2 - t1).total_seconds())
            except ValueError:
                pass
        if gaps:
            print(f"  Call interval: min={min(gaps):.1f}s  max={max(gaps):.1f}s  "
                  f"avg={sum(gaps)/len(gaps):.1f}s  median={sorted(gaps)[len(gaps)//2]:.1f}s")

    # Token usage
    token_values = []
    for e in llm_responses:
        m = re.search(r"tokens=(\d+)", e.get("raw", ""))
        if m:
            token_values.append(int(m.group(1)))
    if token_values:
        print(f"  Input tokens per call: min={min(token_values)}  max={max(token_values)}  "
              f"avg={sum(token_values)//len(token_values)}  total={sum(token_values)}")

    # Pending actions per call
    pending_counts = []
    for e in llm_calls:
        m = re.search(r"pending_actions=(\d+)", e.get("raw", ""))
        if m:
            pending_counts.append(int(m.group(1)))
    if pending_counts:
        print(f"  Pending actions per call: min={min(pending_counts)}  max={max(pending_counts)}  "
              f"avg={sum(pending_counts)/len(pending_counts):.1f}")

    # Instructions per call
    instr_counts = [e for e in llm_calls if "has_instructions=True" in e.get("raw", "")]
    if instr_counts:
        print(f"  Calls with player instructions: {len(instr_counts)}/{len(llm_calls)}")

    # Finish reasons
    finish_reasons = Counter()
    for e in llm_responses:
        m = re.search(r"finish_reason='([^']*)'", e.get("raw", ""))
        if m:
            finish_reasons[m.group(1)] += 1
    if finish_reasons:
        print(f"  Finish reasons: {dict(finish_reasons)}")
    print()

    # ── Tool usage ──
    tool_results = [e for e in events if e["event"] == "tool_result"]
    if tool_results:
        tool_stats = Counter()
        action_status = Counter()
        for e in tool_results:
            t = re.search(r"tool='([^']*)'", e.get("raw", ""))
            name = t.group(1) if t else "?"
            tool_stats[name] += 1
            s = re.search(r"'status':\s*'([^']*)'", e.get("raw", ""))
            if s:
                action_status[s.group(1)] += 1
            # also check for errors
            if "FAILED" in e.get("raw", "") or "error" in e.get("raw", "").lower():
                pass  # errors are counted below

        print(f"=== TOOL CALLS: {sum(tool_stats.values())} total ===")
        for name, count in tool_stats.most_common():
            error_count = 0
            for e in tool_results:
                if f"tool='{name}'" in e.get("raw", "") and ("FAILED" in e.get("raw", "") or "error':" in e.get("raw", "").lower()):
                    # only count actual errors, not status='not_found' etc
                    if "'status':" not in e.get("raw", "") or "error" in e.get("raw", "").lower().split("status")[0] if "status" in e.get("raw", "") else True:
                        pass
            print(f"  {name}: {count}")
        if action_status:
            print(f"  Statuses: {dict(action_status)}")

        # Count errors in tool results
        tool_errors = [e for e in tool_results if "FAILED" in e.get("raw", "") or "error':" in e.get("raw", "").lower()]
        if tool_errors:
            print(f"  Tool errors: {len(tool_errors)}")
        print()

    # ── Instructions / chat ──
    chat_events = [e for e in events if e.get("level", "?") != "?"
                   and ("instruct" in e["event"].lower()
                        or "chat" in e["event"].lower()
                        or "player" in e["event"].lower()
                        or "instruction" in e["event"].lower())]
    if chat_events:
        print(f"=== PLAYER INSTRUCTIONS ({len(chat_events)}) ===")
        for e in chat_events:
            print(f"  {e.get('raw', '')[:200]}")
        print()

    # ── Timeline ──
    if show_timeline:
        print("=== TIMELINE (use --turn N to inspect a call's full context) ===")
        turn_num = 0
        for e in events:
            ts = e.get("timestamp", "")
            if not ts:
                continue
            try:
                t = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                elapsed = (t - t0).total_seconds() if t0 else 0
            except ValueError:
                continue

            lvl = e.get("level", "?")
            evt = e.get("event", "?")

            turn_tag = ""
            if evt == "llm_call":
                turn_num += 1
                turn_tag = f"  [#{turn_num}]"

            extra = ""
            if evt == "llm_response":
                fr = e.get("finish_reason", "")
                toks = e.get("tokens", "")
                tc = e.get("tool_calls", "[]")
                extra = f"  fr={fr} tok={toks} tc={tc}" if tc != "[]" else f"  fr={fr} tok={toks} text"
            elif evt == "tool_result":
                tt = re.search(r"tool='([^']*)'", e.get("raw", ""))
                if tt:
                    s = re.search(r"'status':\s*'([^']*)'", e.get("raw", ""))
                    st = f" → {s.group(1)}" if s else ""
                    extra = f"  {tt.group(1)}{st}"
                elif "FAILED" in e.get("raw", ""):
                    tt = re.search(r"tool='([^']*)'", e.get("raw", ""))
                    extra = f"  {tt.group(1) if tt else '?'} ERROR"
            elif evt == "llm_call":
                msgs = e.get("messages", "?")
                pend = e.get("pending_actions", "?")
                instr = e.get("has_instructions", "")
                instr_tag = " instr" if instr == "True" else ""
                extra = f"  (history={msgs}, pending={pend}{instr_tag})"

            prefix = f"  +{elapsed:>6.1f}s"
            if turn_tag:
                print(f"{prefix} [{lvl}] {evt}{turn_tag}{extra}")
            elif lvl in ("info",):
                print(f"{prefix} [{lvl}] {evt}")
            elif lvl in ("error", "warning"):
                print(f"{prefix} [{lvl.upper()}] {evt}")
            else:
                print(f"{prefix}   {evt}{extra}")

    # ── Summary / problems ──
    print()
    problems = []

    # Problem: too many cancellations
    tool_stats = Counter()
    for e in tool_results if 'tool_results' in dir() else []:
        t = re.search(r"tool='([^']*)'", e.get("raw", ""))
        name = t.group(1) if t else "?"
        tool_stats[name] += 1
    # recreate tool_stats from events
    tool_stats = Counter()
    for e in [ev for ev in events if ev["event"] == "tool_result"]:
        t = re.search(r"tool='([^']*)'", e.get("raw", ""))
        name = t.group(1) if t else "?"
        tool_stats[name] += 1

    total_tools = sum(tool_stats.values())
    if total_tools > 0 and tool_stats.get("cancel_action", 0) > total_tools * 0.3:
        problems.append(
            f"  - High cancel_action ratio: {tool_stats.get('cancel_action', 0)}/{total_tools} "
            f"({100 * tool_stats.get('cancel_action', 0) // total_tools}%)"
        )

    # Problem: many get_action_status calls returning errors
    tool_errors = [e for e in events if e["event"] == "tool_result" and "FAILED" in e.get("raw", "")]
    if len(tool_errors) > 5:
        problems.append(
            f"  - {len(tool_errors)} tool errors — mostly stale action_id lookups"
        )

    # Problem: message history overflow
    for e in llm_calls:
        m = e.get("messages", "")
        if m and m != "?" and int(m) > 15:
            problems.append(
                f"  - Message history spike: {m} messages in one call"
            )
            break

    # Problem: many follow-ups
    follow_ups = [e for e in llm_calls if "follow-up" in e.get("msg", "").lower()]
    if len(follow_ups) > 3:
        problems.append(
            f"  - {len(follow_ups)} follow-up calls"
        )

    # Problem: no LLM calls
    if len(llm_calls) == 0:
        problems.append("  - No llm_call events")

    # Problem: runtime too short
    if total_time < 30:
        problems.append(f"  - Runtime only {fmt_elapsed(total_time)} — may have crashed early")

    # Problem: pending actions never cleared
    if pending_counts and pending_counts[-1] > 0:
        problems.append(
            f"  - {pending_counts[-1]} pending actions at end of session — may not be cleaning up"
        )

    if problems:
        print("=== POTENTIAL PROBLEMS ===")
        for p in problems:
            print(p)
        print()


def main():
    args = sys.argv[1:]
    log_path = "logs/runtime.log"
    show_timeline = True
    output_json = False
    turn_n = None

    i = 0
    while i < len(args):
        arg = args[i]
        if arg == "--no-timeline":
            show_timeline = False
        elif arg == "--json":
            output_json = True
        elif arg == "--turn":
            i += 1
            turn_n = int(args[i]) if i < len(args) else None
        elif not arg.startswith("--"):
            log_path = arg
        i += 1

    # Resolve path relative to project root (where scripts/ lives)
    path = Path(log_path)
    if not path.exists():
        alt = Path(__file__).resolve().parent.parent / log_path
        if alt.exists():
            path = alt
        else:
            print(f"Log file not found: {log_path}", file=sys.stderr)
            print(f"Also tried: {alt}", file=sys.stderr)
            sys.exit(1)

    events = parse_log(str(path))
    if not events:
        print(f"No structured log events found in {path}", file=sys.stderr)
        sys.exit(1)

    if turn_n is not None:
        show_turn(events, turn_n)
        return

    if output_json:
        result = {
            "path": str(path),
            "total_events": len(events),
            "event_types": dict(Counter(e["event"] for e in events)),
            "events": events,
        }
        json.dump(result, sys.stdout, indent=2, default=str)
    else:
        print(f"Analyzing: {path}")
        analyze(events, show_timeline=show_timeline)


if __name__ == "__main__":
    main()
