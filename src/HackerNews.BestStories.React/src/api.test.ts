import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_PAGE_SIZE, MAX_STORIES } from "./types";
import { fetchBestStories, retryDelayMs } from "./api";

const story = {
  title: "A uBlock Origin update was rejected from the Chrome Web Store",
  uri: "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
  postedBy: "ismaildonmez",
  time: "2019-10-12T13:43:01+00:00",
  score: 1716,
  commentCount: 572
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}

describe("pagination defaults", () => {
  it("defaults to 20 items per page and loads 500 stories", () => {
    expect(DEFAULT_PAGE_SIZE).toBe(20);
    expect(MAX_STORIES).toBe(500);
  });
});

describe("retryDelayMs", () => {
  it("doubles each attempt", () => {
    expect(retryDelayMs(0)).toBe(250);
    expect(retryDelayMs(1)).toBe(500);
    expect(retryDelayMs(2)).toBe(1000);
  });
});

describe("fetchBestStories", () => {
  describe("API base normalization", () => {
    beforeEach(() => {
      vi.resetModules();
    });

    afterEach(() => {
      vi.unstubAllEnvs();
      vi.resetModules();
    });

    it.each([
      [undefined, "/api/best-stories?n=7"],
      ["", "/api/best-stories?n=7"],
      ["/", "/api/best-stories?n=7"],
      ["https://example.com", "https://example.com/api/best-stories?n=7"],
      ["https://example.com/", "https://example.com/api/best-stories?n=7"],
      ["https://example.com///", "https://example.com/api/best-stories?n=7"],
      ["/proxy", "/proxy/api/best-stories?n=7"],
      ["/proxy///", "/proxy/api/best-stories?n=7"],
      ["https://example.com/proxy/", "https://example.com/proxy/api/best-stories?n=7"]
    ])("requests the exact URL for base %s", async (base, expectedUrl) => {
      vi.stubEnv("VITE_API_BASE", base);
      const api = await import("./api");
      const fetchImpl = vi.fn().mockResolvedValue(jsonResponse([story]));

      const result = await api.fetchBestStories(7, { fetchImpl });

      expect(result).toEqual([story]);
      expect(fetchImpl).toHaveBeenCalledTimes(1);
      expect(fetchImpl).toHaveBeenCalledWith(expectedUrl);
    });
  });

  it("returns stories on the first 200", async () => {
    const fetchImpl = vi.fn().mockResolvedValue(jsonResponse([story]));
    const result = await fetchBestStories(1, { fetchImpl, sleep: async () => undefined });
    expect(result).toEqual([story]);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });

  it("retries 502 then succeeds", async () => {
    const fetchImpl = vi
      .fn()
      .mockResolvedValueOnce(new Response("bad gateway", { status: 502 }))
      .mockResolvedValueOnce(jsonResponse([story]));
    const delays: number[] = [];
    const result = await fetchBestStories(1, {
      fetchImpl,
      sleep: async (ms) => {
        delays.push(ms);
      }
    });
    expect(result[0].postedBy).toBe("ismaildonmez");
    expect(fetchImpl).toHaveBeenCalledTimes(2);
    expect(delays).toEqual([250]);
  });

  it("does not retry 400", async () => {
    const fetchImpl = vi.fn().mockResolvedValue(new Response("bad n", { status: 400 }));
    await expect(
      fetchBestStories(1, { fetchImpl, sleep: async () => undefined })
    ).rejects.toThrow("bad n");
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });

  it("gives up after four 502s", async () => {
    const fetchImpl = vi.fn().mockResolvedValue(new Response("bad gateway", { status: 502 }));
    await expect(
      fetchBestStories(1, { fetchImpl, sleep: async () => undefined })
    ).rejects.toThrow("bad gateway");
    expect(fetchImpl).toHaveBeenCalledTimes(4);
  });
});
