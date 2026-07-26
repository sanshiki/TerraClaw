#!/usr/bin/env python3
"""
Test TerraClawKnowledge-style Terraria Wiki queries outside tModLoader.

This simulates the lightweight tool flow used by ExampleTerraClawAgent:
1. Accept a plain query or an LLM-style {"type":"knowledge_query","query":"..."} JSON object.
2. Query terraria.wiki.gg through MediaWiki's Action API.
3. Prefer exact page candidates, parse expanded page HTML, and print compact "know" context.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass
from typing import Any

from bs4 import BeautifulSoup, Tag


DEFAULT_API = "https://terraria.wiki.gg/api.php"
INTENT_WORDS = {
    "recipe", "recipes", "crafting", "craft", "crafted", "make", "making",
    "drop", "drops", "dropped", "obtain", "obtained", "get", "getting",
    "how", "to", "do", "i", "terraria", "wiki", "guide", "for", "of", "the",
}


@dataclass(frozen=True)
class KnowledgeSearchResult:
    title: str
    url: str
    extract: str
    snippet: str


@dataclass(frozen=True)
class KnowledgeQueryResult:
    query: str
    results: list[KnowledgeSearchResult]
    from_cache: bool
    source: str


def main() -> int:
    configure_output()
    parser = argparse.ArgumentParser(
        description="Query Terraria Wiki like TerraClawKnowledge and print the returned context."
    )
    parser.add_argument("query", nargs="?", help='Plain search query, for example "Zenith recipe".')
    parser.add_argument(
        "--llm-json",
        help='LLM-style output JSON, for example {"type":"knowledge_query","query":"Eye of Cthulhu drops"}.',
    )
    parser.add_argument("--api", default=DEFAULT_API, help=f"MediaWiki API endpoint. Default: {DEFAULT_API}")
    parser.add_argument("--limit", type=int, default=3, help="Maximum search results to return.")
    parser.add_argument("--extract-chars", type=int, default=900, help="Maximum characters per page extract.")
    parser.add_argument("--timeout", type=float, default=8.0, help="HTTP timeout in seconds.")
    parser.add_argument(
        "--format",
        choices=("text", "json", "context"),
        default="text",
        help="Output format. context prints only the compact know observation text.",
    )
    args = parser.parse_args()

    try:
        query = resolve_query(args.query, args.llm_json)
        result = query_wiki(
            query=query,
            api=args.api,
            limit=max(1, min(args.limit, 8)),
            extract_chars=max(120, min(args.extract_chars, 3000)),
            timeout=args.timeout,
        )
    except (ImportError, ValueError, urllib.error.URLError, TimeoutError) as exc:
        print(f"knowledge_query failed: {exc}", file=sys.stderr)
        return 1

    if args.format == "json":
        print(json.dumps(asdict(result), ensure_ascii=False, indent=2))
    elif args.format == "context":
        print(format_knowledge_context(result))
    else:
        print_text_result(result)
    return 0


def configure_output() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")


def resolve_query(query: str | None, llm_json: str | None) -> str:
    if llm_json:
        try:
            payload = json.loads(llm_json)
        except json.JSONDecodeError as exc:
            raise ValueError(f"--llm-json is not valid JSON: {exc}") from exc
        if not isinstance(payload, dict):
            raise ValueError("--llm-json must be a JSON object")
        output_type = str(payload.get("type", "")).strip()
        if output_type and output_type != "knowledge_query":
            raise ValueError(f"--llm-json type must be knowledge_query, got {output_type!r}")
        query = str(payload.get("query", "")).strip()

    if not query or not query.strip():
        raise ValueError("query is required")
    return query.strip()[:120]


def query_wiki(query: str, api: str, limit: int, extract_chars: int, timeout: float) -> KnowledgeQueryResult:
    hits = merge_hits(infer_title_candidates(query), search(api, query, limit, timeout), limit)
    if not hits:
        return KnowledgeQueryResult(query=query, results=[], from_cache=False, source=api)

    pages = get_page_summaries(api, [hit["title"] for hit in hits], query, extract_chars, timeout)
    results: list[KnowledgeSearchResult] = []
    for hit in hits:
        title = hit["title"]
        page = pages.get(title, {})
        resolved_title = str(page.get("title", title))
        extract = str(page.get("extract", ""))
        snippet = truncate(clean_text(str(hit.get("snippet", ""))), min(extract_chars, 320))
        url = str(page.get("url", "")) or build_page_url(resolved_title)
        if extract or snippet:
            results.append(KnowledgeSearchResult(title=resolved_title, url=url, extract=extract, snippet=snippet))
    return KnowledgeQueryResult(query=query, results=results, from_cache=False, source=api)


def merge_hits(candidates: list[str], search_hits: list[dict[str, str]], limit: int) -> list[dict[str, str]]:
    merged: list[dict[str, str]] = []
    seen: set[str] = set()
    for title in candidates:
        key = normalize_title(title)
        if key and key not in seen:
            merged.append({"title": title, "snippet": "direct page candidate from query"})
            seen.add(key)
    for hit in search_hits:
        key = normalize_title(hit["title"])
        if key and key not in seen:
            merged.append(hit)
            seen.add(key)
    return merged[:limit]


def infer_title_candidates(query: str) -> list[str]:
    words = re.findall(r"[A-Za-z0-9']+", query)
    kept = [word for word in words if word.lower() not in INTENT_WORDS]
    candidate = " ".join(kept).strip()
    return [candidate] if candidate else []


def search(api: str, query: str, limit: int, timeout: float) -> list[dict[str, str]]:
    data = get_json(
        api,
        {
            "action": "query",
            "list": "search",
            "srnamespace": "0",
            "srsearch": query,
            "srlimit": str(limit),
            "format": "json",
            "formatversion": "2",
        },
        timeout,
    )
    raw_hits = data.get("query", {}).get("search", [])
    if not isinstance(raw_hits, list):
        return []

    hits: list[dict[str, str]] = []
    for item in raw_hits:
        if not isinstance(item, dict):
            continue
        title = str(item.get("title", "")).strip()
        if title:
            hits.append({"title": title, "snippet": str(item.get("snippet", ""))})
    return hits


def get_page_summaries(
    api: str,
    titles: list[str],
    query: str,
    extract_chars: int,
    timeout: float,
) -> dict[str, dict[str, str]]:
    result: dict[str, dict[str, str]] = {}
    for title in dict.fromkeys(title for title in titles if title.strip()):
        page = parse_page(api, title, query, extract_chars, timeout)
        if page:
            result[title] = page
            result[page["title"]] = page
    return result


def parse_page(api: str, title: str, query: str, extract_chars: int, timeout: float) -> dict[str, str] | None:
    data = get_json(
        api,
        {
            "action": "parse",
            "page": title,
            "prop": "text|displaytitle",
            "redirects": "1",
            "format": "json",
            "formatversion": "2",
        },
        timeout,
    )
    if "error" in data or not isinstance(data.get("parse"), dict):
        return None
    parsed = data["parse"]
    resolved_title = str(parsed.get("title", title)).strip() or title
    soup = BeautifulSoup(str(parsed.get("text", "")), "html.parser")
    cleanup_soup(soup)
    return {
        "title": resolved_title,
        "url": build_page_url(resolved_title),
        "extract": extract_context(soup, query, extract_chars),
    }


def cleanup_soup(soup: BeautifulSoup) -> None:
    for selector in (
        "script", "style", "noscript", "img", "sup.reference", ".reference", ".mw-editsection",
        ".toc", "#toc", ".metadata", ".noprint", ".navbox", ".catlinks", ".printfooter", ".eico",
    ):
        for node in soup.select(selector):
            node.decompose()


def extract_context(soup: BeautifulSoup, query: str, max_length: int) -> str:
    query_lower = query.lower()
    if any(word in query_lower for word in ("recipe", "recipes", "craft", "crafting", "make")):
        recipes = extract_recipe_tables(soup)
        if recipes:
            return truncate("Recipes: " + " ; ".join(recipes), max_length)

    section = find_relevant_section(soup, query)
    if section:
        return truncate(clean_text(" ".join(node.get_text(" ", strip=True) for node in section)), max_length)

    return truncate(clean_text(soup.get_text(" ", strip=True)), max_length)


def extract_recipe_tables(soup: BeautifulSoup) -> list[str]:
    rows: list[str] = []
    tables = soup.select("table.recipes") or soup.find_all("table", class_=lambda value: value and "recipes" in value)
    for table in tables:
        if not isinstance(table, Tag):
            continue
        headers = [clean_text(cell.get_text(" ", strip=True)).lower() for cell in table.find_all("th")]
        if not any("ingredient" in header for header in headers):
            continue

        for tr in table.find_all("tr"):
            cells = [cell for cell in tr.find_all("td", recursive=False)]
            if len(cells) < 2:
                continue
            result = clean_cell(cells[0])
            ingredients = clean_ingredient_cell(cells[1])
            station = clean_cell(cells[2]) if len(cells) > 2 else ""
            if result and ingredients:
                parts = [f"Result: {result}", f"Ingredients: {ingredients}"]
                if station:
                    parts.append(f"Station: {station}")
                rows.append("; ".join(parts))
    return dedupe(rows)


def clean_ingredient_cell(cell: Tag) -> str:
    items = [clean_cell(li) for li in cell.find_all("li")]
    items = [item for item in items if item]
    return ", ".join(dedupe(items)) if items else clean_cell(cell)


def clean_cell(cell: Tag) -> str:
    text = clean_text(cell.get_text(" ", strip=True))
    text = re.sub(r"\((Desktop|Console|Mobile|Old-gen console|3DS)[^)]+versions?\)", "", text)
    text = re.sub(r"^only:\s*", "", text, flags=re.IGNORECASE)
    text = re.sub(r"\s+", " ", text).strip(" ,;")
    return text


def find_relevant_section(soup: BeautifulSoup, query: str) -> list[Tag]:
    query_lower = query.lower()
    candidates: list[str] = []
    if any(word in query_lower for word in ("drop", "drops", "dropped")):
        candidates.extend(["drops", "drop rates", "loot"])
    if any(word in query_lower for word in ("recipe", "recipes", "craft", "crafting", "make")):
        candidates.extend(["crafting", "recipes", "used in"])
    candidates.extend(query_terms(query))

    for heading in soup.find_all(re.compile("^h[1-6]$")):
        heading_text = clean_text(heading.get_text(" ", strip=True)).lower()
        if not any(candidate.lower() in heading_text for candidate in candidates):
            continue
        return collect_section_nodes(heading)
    return []


def collect_section_nodes(heading: Tag) -> list[Tag]:
    result: list[Tag] = []
    level = int(heading.name[1]) if heading.name and heading.name[1:].isdigit() else 2
    for sibling in heading.next_siblings:
        if isinstance(sibling, Tag) and re.fullmatch(r"h[1-6]", sibling.name or ""):
            sibling_level = int(sibling.name[1])
            if sibling_level <= level:
                break
        if isinstance(sibling, Tag):
            result.append(sibling)
    return result


def query_terms(query: str) -> list[str]:
    return [word for word in re.findall(r"[A-Za-z0-9']+", query) if word.lower() not in INTENT_WORDS]


def get_json(api: str, params: dict[str, str], timeout: float) -> dict[str, Any]:
    url = api + ("&" if "?" in api else "?") + urllib.parse.urlencode(params)
    request = urllib.request.Request(url, headers={"User-Agent": "TerraClawKnowledgeTest/0.1"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        charset = response.headers.get_content_charset() or "utf-8"
        payload = response.read().decode(charset)
    data = json.loads(payload)
    if not isinstance(data, dict):
        raise ValueError("MediaWiki API returned non-object JSON")
    return data


def format_knowledge_context(result: KnowledgeQueryResult) -> str:
    if not result.results:
        return f"No Terraria Wiki results for '{truncate(result.query, 80)}'."

    parts: list[str] = []
    for index, item in enumerate(result.results[:3], start=1):
        text = item.extract or item.snippet or "No summary available."
        parts.append(f"{index}. {item.title}: {truncate(text, 520)} Source: {item.url}")
    return " | ".join(parts)


def print_text_result(result: KnowledgeQueryResult) -> None:
    print(f"query: {result.query}")
    print(f"source: {result.source}")
    print(f"results: {len(result.results)}")
    print()
    for index, item in enumerate(result.results, start=1):
        text = item.extract or item.snippet or "No summary available."
        print(f"{index}. {item.title}")
        print(f"   url: {item.url}")
        print(f"   text: {text}")
        print()
    print("know context:")
    print(format_knowledge_context(result))


def clean_text(text: str) -> str:
    if not text.strip():
        return ""
    without_tags = BeautifulSoup(text, "html.parser").get_text(" ", strip=True) if "<" in text else text
    decoded = re.sub(r"\s+", " ", without_tags).strip()
    return decoded


def truncate(text: str, max_length: int) -> str:
    if not text.strip() or max_length <= 0 or len(text) <= max_length:
        return text
    return text[:max_length].rstrip()


def normalize_title(title: str) -> str:
    return re.sub(r"\s+", " ", title.replace("_", " ").strip()).lower()


def build_page_url(title: str) -> str:
    return "https://terraria.wiki.gg/wiki/" + urllib.parse.quote(title.replace(" ", "_"))


def dedupe(values: list[str]) -> list[str]:
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        key = normalize_title(value)
        if key and key not in seen:
            result.append(value)
            seen.add(key)
    return result


if __name__ == "__main__":
    raise SystemExit(main())