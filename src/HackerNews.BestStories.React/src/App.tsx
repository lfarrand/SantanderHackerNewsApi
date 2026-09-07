import { useEffect, useMemo, useState } from "react";
import { fetchBestStories } from "./api";
import { DEFAULT_PAGE_SIZE, MAX_STORIES, type Story } from "./types";

const PAGE_SIZES = [10, 20, 50, 100];

function formatTime(value: string): string {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toISOString().replace(".000", "");
}

export default function App() {
  const [stories, setStories] = useState<Story[]>([]);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    fetchBestStories(MAX_STORIES)
      .then((data) => {
        if (!cancelled) {
          setStories(data);
        }
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Failed to load stories");
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const pageCount = Math.max(1, Math.ceil(stories.length / pageSize));
  const currentPage = Math.min(page, pageCount);

  const pageRows = useMemo(() => {
    const start = (currentPage - 1) * pageSize;
    return stories.slice(start, start + pageSize);
  }, [stories, currentPage, pageSize]);

  return (
    <main className="page">
      <header className="hero">
        <p className="eyebrow">Hacker News</p>
        <h1>Best stories</h1>
        <p className="lede">
          Ranked by score from <code>/api/best-stories</code>. Default page size is {DEFAULT_PAGE_SIZE}.
        </p>
      </header>

      <section className="toolbar">
        <div>
          {loading ? "Loading…" : `${stories.length} stories`}
          {error ? <span className="error"> — {error}</span> : null}
        </div>
        <label>
          Per page
          <select
            value={pageSize}
            onChange={(event) => {
              setPageSize(Number(event.target.value));
              setPage(1);
            }}
          >
            {PAGE_SIZES.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </select>
        </label>
      </section>

      <div className="grid-wrap">
        <table className="grid">
          <thead>
            <tr>
              <th className="num">#</th>
              <th>Title</th>
              <th>Posted by</th>
              <th>Time (UTC)</th>
              <th className="num">Score</th>
              <th className="num">Comments</th>
            </tr>
          </thead>
          <tbody>
            {pageRows.map((story, index) => {
              const rank = (currentPage - 1) * pageSize + index + 1;
              return (
                <tr key={`${story.uri}-${rank}`}>
                  <td className="num">{rank}</td>
                  <td>
                    <a href={story.uri} target="_blank" rel="noreferrer">
                      {story.title}
                    </a>
                  </td>
                  <td>{story.postedBy}</td>
                  <td className="mono">{formatTime(story.time)}</td>
                  <td className="num">{story.score}</td>
                  <td className="num">{story.commentCount}</td>
                </tr>
              );
            })}
            {!loading && pageRows.length === 0 ? (
              <tr>
                <td colSpan={6} className="empty">
                  No stories to display.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>

      <nav className="pager">
        <button type="button" disabled={currentPage <= 1} onClick={() => setPage(1)}>
          First
        </button>
        <button type="button" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>
          Previous
        </button>
        <span>
          Page {currentPage} of {pageCount}
        </span>
        <button
          type="button"
          disabled={currentPage >= pageCount}
          onClick={() => setPage(currentPage + 1)}
        >
          Next
        </button>
        <button
          type="button"
          disabled={currentPage >= pageCount}
          onClick={() => setPage(pageCount)}
        >
          Last
        </button>
      </nav>
    </main>
  );
}
