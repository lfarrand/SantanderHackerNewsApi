import { MAX_STORIES, type Story } from "./types";

const apiBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? "";

const RETRYABLE_STATUS = new Set([429, 502, 503, 504]);

export type FetchLike = typeof fetch;
export type SleepFn = (ms: number) => Promise<void>;

export type FetchBestStoriesOptions = {
  fetchImpl?: FetchLike;
  sleep?: SleepFn;
  maxAttempts?: number;
  baseDelayMs?: number;
};

const defaultSleep: SleepFn = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export function retryDelayMs(attemptIndex: number, baseDelayMs = 250): number {
  return baseDelayMs * 2 ** attemptIndex;
}

export function isRetryableStatus(status: number): boolean {
  return RETRYABLE_STATUS.has(status);
}

export async function fetchBestStories(
  n = MAX_STORIES,
  options: FetchBestStoriesOptions = {}
): Promise<Story[]> {
  const doFetch = options.fetchImpl ?? fetch;
  const sleep = options.sleep ?? defaultSleep;
  const maxAttempts = options.maxAttempts ?? 4;
  const baseDelayMs = options.baseDelayMs ?? 250;
  const url = `${apiBase}/api/best-stories?n=${n}`;

  let lastError: Error = new Error("Request failed");

  for (let attempt = 0; attempt < maxAttempts; attempt++) {
    let response: Response;
    try {
      response = await doFetch(url);
    } catch (err) {
      lastError = err instanceof Error ? err : new Error(String(err));
      if (attempt === maxAttempts - 1) {
        throw lastError;
      }

      await sleep(retryDelayMs(attempt, baseDelayMs));
      continue;
    }

    if (response.ok) {
      return (await response.json()) as Story[];
    }

    if (isRetryableStatus(response.status) && attempt < maxAttempts - 1) {
      await sleep(retryDelayMs(attempt, baseDelayMs));
      continue;
    }

    const detail = await response.text();
    throw new Error(detail || `Request failed (${response.status})`);
  }

  throw lastError;
}
