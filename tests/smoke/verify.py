import json
import time
import urllib.error
import urllib.request


EXPECTED = [
    {"title": "Highest score, last upstream", "uri": "https://fixture.invalid/104", "postedBy": "dora", "time": "2023-11-14T22:13:20+00:00", "score": 900, "commentCount": 4},
    {"title": "Tie, smaller ID", "uri": "https://fixture.invalid/102", "postedBy": "bob", "time": "2023-11-14T22:13:20+00:00", "score": 150, "commentCount": 2},
    {"title": "Tie, larger ID", "uri": "https://fixture.invalid/103", "postedBy": "carol", "time": "2023-11-14T22:13:20+00:00", "score": 150, "commentCount": 3},
    {"title": "First, not best", "uri": "https://fixture.invalid/101", "postedBy": "alice", "time": "2023-11-14T22:13:20+00:00", "score": 5, "commentCount": 1},
]
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def request(url, headers=None):
    try:
        response = OPENER.open(urllib.request.Request(url, headers=headers or {}), timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        status = response.getcode()
        return status, response.read(), response.headers.get_content_type()


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def test_proxy_contract():
    for count, expected in [(1, EXPECTED[:1]), (4, EXPECTED)]:
        status, body, content_type = request(f"http://web/api/best-stories?n={count}")
        check(status == 200, f"n={count}: expected HTTP 200, got {status}: {body!r}")
        check(content_type == "application/json", f"n={count}: expected JSON, got {content_type}")
        actual = json.loads(body)
        check(actual == expected, f"n={count}: exact six-field response mismatch: {actual!r}")
        for story in actual:
            check(type(story["score"]) is int and type(story["commentCount"]) is int, "Scores/counts must be JSON integers")
    for base in ("http://api:8080", "http://web"):
        for query, label in [("", "missing n"), ("?n=not-an-integer", "malformed n")]:
            status, body, content_type = request(f"{base}/api/best-stories{query}")
            check(status == 400 and content_type == "application/problem+json", f"{label} through {base}: expected HTTP 400 application/problem+json, got {status} {content_type}: {body!r}")
            problem = json.loads(body)
            check(isinstance(problem, dict) and problem.get("status") == 400, f"Unexpected {label} problem through {base}: {problem!r}")
            check(isinstance(problem.get("detail"), str) and problem["detail"].strip(), f"Missing {label} problem detail through {base}: {problem!r}")
            if not query:
                check(problem.get("title") == "Invalid n" and "required" in problem["detail"], f"Unexpected missing-n problem through {base}: {problem!r}")
            else:
                check(problem.get("title") == "Request failed" and "'n' must be an integer" in problem["detail"], f"Unexpected malformed-n problem through {base}: {problem!r}")
    print("PASS test_proxy_contract: intact /api path, n=1 highest score, n=4 descending/tie order, exact six fields and values, missing and malformed n=400 problem bodies/media types on direct and proxy paths", flush=True)


def test_one_hop_spoof_resilience(started):
    # Six preceding requests (two 200s and four 400s) used this real client's
    # shared partition. Neither switching paths nor forged headers may split it.
    for index in range(6, 64):
        base = "http://api:8080" if index % 2 == 0 else "http://web"
        headers = {
            "X-Forwarded-For": f"198.51.100.{index}, 203.0.113.{index}",
            "X-Forwarded-Proto": "https" if index % 2 == 0 else "http",
            "X-Real-IP": f"192.0.2.{index}",
            "Forwarded": f"for=198.51.100.{index};proto=https, for=203.0.113.{index};proto=http",
        }
        status, body, content_type = request(f"{base}/api/best-stories?n=1", headers)
        expected_status = 200 if index < 60 else 429
        check(status == expected_status, f"Request {index + 1} through {base}: expected {expected_status}, got {status}: {body!r}")
        if status == 200:
            check(content_type == "application/json", f"Request {index + 1} through {base}: expected JSON, got {content_type}")
            check(json.loads(body) == EXPECTED[:1], "Spoofed request changed response")
        else:
            check(content_type == "application/problem+json", f"Request {index + 1} through {base}: expected application/problem+json, got {content_type}: {body!r}")
            problem = json.loads(body)
            check(isinstance(problem, dict) and problem.get("status") == 429, f"Unexpected rate-limit problem through {base}: {problem!r}")
            check(isinstance(problem.get("title"), str) and problem["title"].strip(), f"Missing rate-limit problem title through {base}: {problem!r}")
    check(time.monotonic() - started < 30, "Probe exceeded half of the 60-second rate-limit window; timing evidence is inconclusive")
    print("PASS test_one_hop_spoof_resilience: one combined 60-request allowance (including four 400s) with interleaved direct/proxy traffic; requests 61/63 direct and 62/64 proxy all 429 problem bodies/media types despite rotating forged headers", flush=True)


def test_fixture_only_single_hydration():
    status, body, _ = request("http://127.0.0.1:8080/__requests")
    expected = {"/v0/beststories.json": 1, **{f"/v0/item/{key}.json": 1 for key in (101, 102, 103, 104)}}
    check(status == 200 and json.loads(body) == expected, f"Unexpected upstream request counts: {body!r}")
    print("PASS test_fixture_only_single_hydration: one ID-list fetch, all four candidates fetched once, no unexpected paths", flush=True)


if __name__ == "__main__":
    started = time.monotonic()
    test_proxy_contract()
    test_one_hop_spoof_resilience(started)
    test_fixture_only_single_hydration()