import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import * as api from "./api";

afterEach(() => {
  vi.restoreAllMocks();
});

describe("App", () => {
  it("renders title and default page-size copy", async () => {
    vi.spyOn(api, "fetchBestStories").mockResolvedValue([]);
    render(<App />);

    expect(await screen.findByRole("heading", { name: "Best stories" })).toBeTruthy();
    expect(screen.getByText(/Default page size is 20/i)).toBeTruthy();
  });

  it("shows fetched story row", async () => {
    vi.spyOn(api, "fetchBestStories").mockResolvedValue([
      {
        title: "A uBlock Origin update was rejected from the Chrome Web Store",
        uri: "https://example.com/1",
        postedBy: "ismaildonmez",
        time: "2019-10-12T13:43:01+00:00",
        score: 1716,
        commentCount: 572
      }
    ]);

    render(<App />);

    expect(await screen.findByText("ismaildonmez")).toBeTruthy();
    expect(screen.getByRole("link", { name: "A uBlock Origin update was rejected from the Chrome Web Store" }).getAttribute("href")).toBe("https://example.com/1");
    expect(screen.getByText("1 stories")).toBeTruthy();
  });
});
